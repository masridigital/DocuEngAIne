using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Infrastructure.Integrations.Migration;

/// <summary>
/// One-shot IT Glue import. Does not register a recurring <see cref="IntegrationProvider"/> pull.
/// Companies converge through <see cref="CompanyIdentity"/> with key <see cref="CompanyIdentity.ItGlueKey"/>.
/// </summary>
public sealed class ItGlueMigrationService : IItGlueMigrationService
{
    private readonly DocuEngAIneDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IMcpClient _mcpClient;
    private readonly IAuditService _audit;

    public ItGlueMigrationService(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IMcpClient mcpClient,
        IAuditService audit)
    {
        _db = db;
        _user = user;
        _mcpClient = mcpClient;
        _audit = audit;
    }

    public async Task<ItGlueMigrationResult> ImportAsync(
        Guid? mcpServerId,
        string? payloadJson,
        CancellationToken cancellationToken = default)
    {
        if (_user.TenantId is null)
            throw new InvalidOperationException("Tenant is required for this operation.");

        try
        {
            var slice = await LoadSliceAsync(mcpServerId, payloadJson, cancellationToken);
            var result = await ApplySliceAsync(slice, cancellationToken);
            await _audit.LogAsync(
                "ItGlue.Import",
                "Migration",
                null,
                $"companies +{result.CompaniesCreated}/~{result.CompaniesUpdated} "
                + $"docs +{result.DocumentsCreated}/~{result.DocumentsUpdated} "
                + $"assets +{result.AssetsCreated}/~{result.AssetsUpdated} "
                + $"skipped={result.ItemsSkipped}",
                cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            return new ItGlueMigrationResult(
                Status: nameof(SyncRunStatus.Failed),
                CompaniesCreated: 0,
                CompaniesUpdated: 0,
                DocumentsCreated: 0,
                DocumentsUpdated: 0,
                AssetsCreated: 0,
                AssetsUpdated: 0,
                ItemsSkipped: 0,
                ErrorSummary: ex.Message);
        }
    }

    private async Task<ItGlueImportSlice> LoadSliceAsync(
        Guid? mcpServerId,
        string? payloadJson,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(payloadJson))
            return ItGlueJsonApiMapper.Parse(payloadJson);

        if (mcpServerId is not Guid serverId)
            throw new InvalidOperationException("McpServerId or a JSON:API payload is required.");

        var server = await _db.McpServers.ForTenant(_user)
            .FirstOrDefaultAsync(s => s.Id == serverId, cancellationToken)
            ?? throw new InvalidOperationException("MCP server not found.");

        if (server.Kind != McpServerKind.StackJackCompact)
            throw new InvalidOperationException(
                "IT Glue import via Compact requires a StackJack Compact MCP server.");

        return await ItGlueJsonApiMapper.PullOrganizationsAsync(_mcpClient, server.Id, cancellationToken);
    }

    private async Task<ItGlueMigrationResult> ApplySliceAsync(
        ItGlueImportSlice slice,
        CancellationToken cancellationToken)
    {
        var companiesCreated = 0;
        var companiesUpdated = 0;
        var companyByItGlueId = await ImportOrganizationsAsync(
            slice.Organizations, cancellationToken, incrementCreated: () => companiesCreated++, incrementUpdated: () => companiesUpdated++);

        var documentsCreated = 0;
        var documentsUpdated = 0;
        await ImportDocumentsAsync(
            slice.Documents, companyByItGlueId, cancellationToken,
            incrementCreated: () => documentsCreated++, incrementUpdated: () => documentsUpdated++);

        var assetsCreated = 0;
        var assetsUpdated = 0;
        await ImportFlexibleAssetsAsync(
            slice.FlexibleAssets, companyByItGlueId, cancellationToken,
            incrementCreated: () => assetsCreated++, incrementUpdated: () => assetsUpdated++);

        await _db.SaveChangesAsync(cancellationToken);

        return new ItGlueMigrationResult(
            Status: nameof(SyncRunStatus.Succeeded),
            CompaniesCreated: companiesCreated,
            CompaniesUpdated: companiesUpdated,
            DocumentsCreated: documentsCreated,
            DocumentsUpdated: documentsUpdated,
            AssetsCreated: assetsCreated,
            AssetsUpdated: assetsUpdated,
            ItemsSkipped: slice.PasswordsSkipped);
    }

    private async Task<Dictionary<string, Guid>> ImportOrganizationsAsync(
        IReadOnlyList<ExternalCompanyDto> organizations,
        CancellationToken cancellationToken,
        Action incrementCreated,
        Action incrementUpdated)
    {
        var index = new CompanyMatchIndex(
            await _db.Companies.ForTenant(_user).ToListAsync(cancellationToken));
        var claimed = new HashSet<Guid>();
        var byItGlueId = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        foreach (var company in await _db.Companies.ForTenant(_user).ToListAsync(cancellationToken))
        {
            if (CompanyIdentity.ReadExternalIds(company.ExternalIdsJson)
                .TryGetValue(CompanyIdentity.ItGlueKey, out var existingId))
            {
                byItGlueId[existingId] = company.Id;
            }
        }

        foreach (var dto in organizations)
        {
            var match = index.Find(CompanyIdentity.ItGlueKey, dto);
            if (match is not null && !claimed.Contains(match.Company.Id))
            {
                StampItGlueId(match.Company, dto.ExternalId);
                ApplyEmptyDetails(match.Company, dto);
                index.Add(match.Company);
                claimed.Add(match.Company.Id);
                byItGlueId[dto.ExternalId] = match.Company.Id;
                incrementUpdated();
                continue;
            }

            var slug = string.IsNullOrWhiteSpace(dto.Slug) ? Slugify(dto.Name) : dto.Slug!;
            var company = new Company
            {
                TenantId = _user.TenantId!.Value,
                Name = dto.Name,
                Slug = await EnsureUniqueSlugAsync(slug, cancellationToken),
                PrimaryDomain = dto.PrimaryDomain,
                City = dto.City,
                State = dto.State,
                Website = dto.Website,
                Address = dto.Address,
            };
            StampItGlueId(company, dto.ExternalId);
            _db.Companies.Add(company);
            await _db.SaveChangesAsync(cancellationToken);

            index.Add(company);
            claimed.Add(company.Id);
            byItGlueId[dto.ExternalId] = company.Id;
            incrementCreated();
        }

        return byItGlueId;
    }

    private async Task ImportDocumentsAsync(
        IReadOnlyList<ItGlueDocumentDto> documents,
        IReadOnlyDictionary<string, Guid> companyByItGlueId,
        CancellationToken cancellationToken,
        Action incrementCreated,
        Action incrementUpdated)
    {
        var existing = await _db.Documents.ForTenant(_user)
            .Where(d => d.Slug != null && d.Slug.StartsWith(ItGlueJsonApiMapper.DocumentSlugPrefix))
            .ToListAsync(cancellationToken);
        var bySlug = existing
            .Where(d => d.Slug is not null)
            .ToDictionary(d => d.Slug!, StringComparer.OrdinalIgnoreCase);

        foreach (var dto in documents)
        {
            var slug = ItGlueJsonApiMapper.DocumentSlug(dto.ExternalId);
            var companyId = ResolveCompanyId(dto.OrganizationExternalId, companyByItGlueId);

            if (bySlug.TryGetValue(slug, out var doc))
            {
                doc.Title = dto.Title;
                doc.Summary = dto.Summary;
                doc.Content = dto.Content;
                doc.CompanyId = companyId ?? doc.CompanyId;
                incrementUpdated();
                continue;
            }

            doc = new Document
            {
                TenantId = _user.TenantId!.Value,
                Title = dto.Title,
                Slug = slug,
                Summary = dto.Summary,
                Content = dto.Content,
                Tags = $"itglue:{dto.ExternalId}",
                CompanyId = companyId,
                IsPublished = true,
            };
            _db.Documents.Add(doc);
            bySlug[slug] = doc;
            incrementCreated();
        }
    }

    private async Task ImportFlexibleAssetsAsync(
        IReadOnlyList<ItGlueFlexibleAssetDto> assets,
        IReadOnlyDictionary<string, Guid> companyByItGlueId,
        CancellationToken cancellationToken,
        Action incrementCreated,
        Action incrementUpdated)
    {
        if (assets.Count == 0)
            return;

        var assetType = await EnsureFlexibleAssetTypeAsync(cancellationToken);
        var idField = await EnsureItGlueIdFieldAsync(assetType, cancellationToken);

        var existing = await _db.Assets.ForTenant(_user)
            .Include(a => a.CustomFieldValues)
            .Where(a => a.AssetTypeId == assetType.Id)
            .ToListAsync(cancellationToken);
        var byItGlueId = new Dictionary<string, Asset>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in existing)
        {
            var value = asset.CustomFieldValues.FirstOrDefault(v => v.FieldDefinitionId == idField.Id)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
                byItGlueId[value] = asset;
        }

        foreach (var dto in assets)
        {
            var companyId = ResolveCompanyId(dto.OrganizationExternalId, companyByItGlueId);
            if (byItGlueId.TryGetValue(dto.ExternalId, out var asset))
            {
                asset.Name = dto.Name;
                asset.Notes = dto.Notes;
                asset.CompanyId = companyId ?? asset.CompanyId;
                incrementUpdated();
                continue;
            }

            asset = new Asset
            {
                TenantId = _user.TenantId!.Value,
                Name = dto.Name,
                Notes = dto.Notes,
                CompanyId = companyId,
                AssetTypeId = assetType.Id,
            };
            _db.Assets.Add(asset);
            _db.CustomFieldValues.Add(new CustomFieldValue
            {
                Asset = asset,
                FieldDefinitionId = idField.Id,
                Value = dto.ExternalId,
            });
            byItGlueId[dto.ExternalId] = asset;
            incrementCreated();
        }
    }

    private async Task<AssetType> EnsureFlexibleAssetTypeAsync(CancellationToken cancellationToken)
    {
        var types = await _db.AssetTypes.ForTenant(_user).ToListAsync(cancellationToken);
        var existing = types.FirstOrDefault(t =>
            string.Equals(t.Name, ItGlueJsonApiMapper.FlexibleAssetTypeName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return existing;

        var assetType = new AssetType
        {
            TenantId = _user.TenantId!.Value,
            Name = ItGlueJsonApiMapper.FlexibleAssetTypeName,
            Description = "Flexible assets imported from IT Glue. One-shot migration; not a live pull.",
        };
        _db.AssetTypes.Add(assetType);
        await _db.SaveChangesAsync(cancellationToken);
        return assetType;
    }

    private async Task<FieldDefinition> EnsureItGlueIdFieldAsync(AssetType assetType, CancellationToken cancellationToken)
    {
        await _db.Entry(assetType).Collection(t => t.Fields).LoadAsync(cancellationToken);
        var existing = assetType.Fields.FirstOrDefault(f =>
            string.Equals(f.Name, ItGlueJsonApiMapper.ItGlueIdFieldName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return existing;

        var field = new FieldDefinition
        {
            AssetTypeId = assetType.Id,
            Name = ItGlueJsonApiMapper.ItGlueIdFieldName,
            FieldType = "Text",
            SortOrder = 0,
        };
        _db.FieldDefinitions.Add(field);
        await _db.SaveChangesAsync(cancellationToken);
        return field;
    }

    private static Guid? ResolveCompanyId(string? organizationExternalId, IReadOnlyDictionary<string, Guid> companyByItGlueId)
    {
        if (string.IsNullOrWhiteSpace(organizationExternalId))
            return null;
        return companyByItGlueId.TryGetValue(organizationExternalId, out var id) ? id : null;
    }

    private static void StampItGlueId(Company company, string externalId)
    {
        if (!CompanyIdentity.ReadExternalIds(company.ExternalIdsJson).ContainsKey(CompanyIdentity.ItGlueKey))
            company.ExternalIdsJson = CompanyIdentity.UpsertExternalId(
                company.ExternalIdsJson, CompanyIdentity.ItGlueKey, externalId);
    }

    /// <summary>Fills blank company fields from IT Glue. Never overwrites a name or a field a tech already set.</summary>
    private static void ApplyEmptyDetails(Company company, ExternalCompanyDto dto)
    {
        company.PrimaryDomain ??= dto.PrimaryDomain;
        company.City ??= dto.City;
        company.State ??= dto.State;
        company.Website ??= dto.Website;
        company.Address ??= dto.Address;
    }

    private async Task<string> EnsureUniqueSlugAsync(string slug, CancellationToken cancellationToken)
    {
        var candidate = slug;
        var i = 2;
        while (await _db.Companies.ForTenant(_user).AnyAsync(c => c.Slug == candidate, cancellationToken))
        {
            candidate = $"{slug}-{i}";
            i++;
        }
        return candidate;
    }

    private static string Slugify(string name)
    {
        var chars = name.Trim().ToLowerInvariant().Select(ch =>
            char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal))
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        return slug.Trim('-');
    }
}
