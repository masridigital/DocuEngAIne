using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Core.Mcp;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Infrastructure.Integrations.Migration;

/// <summary>
/// One-shot Hudu import. Companies converge through <see cref="CompanyIdentity"/> using
/// ExternalIdsJson key <see cref="CompanyIdentity.HuduKey"/> ("hudu"). Articles become
/// <see cref="Document"/> rows in a company folder named from Hudu. Password tools and
/// password payload rows are skipped — Keeper is the vault.
/// </summary>
public sealed class HuduMigrationService : IHuduMigrationService
{
    public const string DefaultFolderName = "Hudu";
    public const string ArticleSlugPrefix = "hudu-";
    public const string ArticleTagPrefix = "hudu:";
    public const string PayloadSource = "payload";
    public const string CompactSource = "compact";

    /// <summary>
    /// Compact password tools that exist on the <c>hudu_</c> prefix. The import must never call
    /// these — secrets stay in Keeper, not SQL.
    /// </summary>
    public static readonly string[] PasswordToolNames =
    [
        "hudu_list_passwords",
        "hudu_get_password",
        "hudu_search_passwords",
        "hudu_list_password_folders",
        "hudu_get_password_folder",
        "hudu_search_password_folders",
        "hudu_create_password",
        "hudu_update_password",
        "hudu_archive_password",
        "hudu_unarchive_password",
        "hudu_delete_password",
    ];

    public const string MissingToolsMessage =
        "This Compact server does not expose hudu_list_companies or hudu_list_articles. "
        + "Supply a companies+articles JSON payload, or enable the Compact Hudu connector "
        + "(prefix hudu_). Password tools (hudu_list_passwords / hudu_get_password) are never called.";

    private readonly DocuEngAIneDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IMcpClient _mcpClient;
    private readonly IAuditService _audit;

    public HuduMigrationService(
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

    public async Task<HuduImportResult?> ImportAsync(
        Guid mcpServerId,
        HuduImportPayload? payload = null,
        CancellationToken cancellationToken = default)
    {
        var server = await _db.McpServers.ForTenant(_user)
            .FirstOrDefaultAsync(
                s => s.Id == mcpServerId && s.Kind == McpServerKind.StackJackCompact,
                cancellationToken);
        if (server is null)
            return null;

        var toolsUsed = new List<string>();
        IReadOnlyList<ExternalCompanyDto> companies;
        IReadOnlyList<HuduArticleRecord> articles;
        IReadOnlyList<HuduFolderRecord> folders = [];
        var passwordsSkipped = payload?.PasswordCount ?? 0;
        string source;

        if (payload?.Companies is not null || payload?.Articles is not null || (payload?.PasswordCount ?? 0) > 0)
        {
            source = PayloadSource;
            companies = payload?.Companies ?? [];
            articles = payload?.Articles ?? [];
        }
        else
        {
            source = CompactSource;
            var listBody = await _mcpClient.ListToolsAsync(mcpServerId, cancellationToken);
            var toolNames = HuduMcpPayload.ReadToolNames(listBody);

            if (!toolNames.Contains(HuduCompanyMapper.ToolName) && !toolNames.Contains(HuduArticleMapper.ListToolName))
                throw new InvalidOperationException(MissingToolsMessage);

            companies = [];
            if (toolNames.Contains(HuduCompanyMapper.ToolName))
            {
                companies = await HuduCompanyMapper.PullAsync(_mcpClient, mcpServerId, cancellationToken: cancellationToken);
                toolsUsed.Add(HuduCompanyMapper.ToolName);
            }

            if (toolNames.Contains(HuduFolderMapper.ToolName))
            {
                folders = await HuduFolderMapper.PullAsync(_mcpClient, mcpServerId, cancellationToken: cancellationToken);
                toolsUsed.Add(HuduFolderMapper.ToolName);
            }

            articles = [];
            if (toolNames.Contains(HuduArticleMapper.ListToolName))
            {
                var listed = await HuduArticleMapper.PullAsync(_mcpClient, mcpServerId, cancellationToken: cancellationToken);
                toolsUsed.Add(HuduArticleMapper.ListToolName);

                if (toolNames.Contains(HuduArticleMapper.GetToolName)
                    && listed.Any(a => string.IsNullOrWhiteSpace(a.Content)))
                {
                    listed = await FillArticleContentAsync(listed, mcpServerId, cancellationToken);
                    toolsUsed.Add(HuduArticleMapper.GetToolName);
                }

                articles = listed;
            }
        }

        articles = ApplyFolderNames(articles, folders);

        var (companiesCreated, companiesUpdated, companiesSkipped, companyByHuduId) =
            await ImportCompaniesAsync(companies, cancellationToken);

        var (articlesCreated, articlesUpdated, articlesSkipped) =
            await ImportArticlesAsync(articles, companyByHuduId, cancellationToken);

        await _db.SaveChangesAsync(cancellationToken);
        await _audit.LogAsync(
            "Migration.Hudu",
            nameof(McpServer),
            mcpServerId,
            $"source={source} companies +{companiesCreated}/~{companiesUpdated} articles +{articlesCreated}/~{articlesUpdated} passwordsSkipped={passwordsSkipped}",
            cancellationToken);

        return new HuduImportResult(
            CompaniesCreated: companiesCreated,
            CompaniesUpdated: companiesUpdated,
            CompaniesSkipped: companiesSkipped,
            ArticlesCreated: articlesCreated,
            ArticlesUpdated: articlesUpdated,
            ArticlesSkipped: articlesSkipped,
            PasswordsSkipped: passwordsSkipped,
            Source: source,
            ToolsUsed: toolsUsed,
            Message: passwordsSkipped > 0
                ? "Password entities were skipped. Import credentials into Keeper; DocuEngAIne stores Keeper links only."
                : null);
    }

    private async Task<IReadOnlyList<HuduArticleRecord>> FillArticleContentAsync(
        IReadOnlyList<HuduArticleRecord> listed,
        Guid mcpServerId,
        CancellationToken cancellationToken)
    {
        var filled = new List<HuduArticleRecord>(listed.Count);
        foreach (var article in listed)
        {
            if (!string.IsNullOrWhiteSpace(article.Content))
            {
                filled.Add(article);
                continue;
            }

            var detail = await HuduArticleMapper.GetAsync(_mcpClient, mcpServerId, article.ExternalId, cancellationToken);
            if (detail is null)
            {
                filled.Add(article);
                continue;
            }

            filled.Add(article with
            {
                Content = detail.Content ?? article.Content,
                Title = string.IsNullOrWhiteSpace(detail.Title) ? article.Title : detail.Title,
                Slug = article.Slug ?? detail.Slug,
                FolderName = article.FolderName ?? detail.FolderName,
                FolderExternalId = article.FolderExternalId ?? detail.FolderExternalId,
                CompanyExternalId = article.CompanyExternalId ?? detail.CompanyExternalId,
                Draft = article.Draft || detail.Draft,
            });
        }

        return filled;
    }

    private static IReadOnlyList<HuduArticleRecord> ApplyFolderNames(
        IReadOnlyList<HuduArticleRecord> articles,
        IReadOnlyList<HuduFolderRecord> folders)
    {
        if (folders.Count == 0)
            return articles;

        var byId = folders.ToDictionary(f => f.ExternalId, f => f.Name, StringComparer.OrdinalIgnoreCase);
        return articles.Select(a =>
        {
            if (!string.IsNullOrWhiteSpace(a.FolderName))
                return a;
            if (a.FolderExternalId is string folderId && byId.TryGetValue(folderId, out var name))
                return a with { FolderName = name };
            return a;
        }).ToList();
    }

    private async Task<(int Created, int Updated, int Skipped, Dictionary<string, Company> ByHuduId)> ImportCompaniesAsync(
        IReadOnlyList<ExternalCompanyDto> companies,
        CancellationToken cancellationToken)
    {
        var created = 0;
        var updated = 0;
        var skipped = 0;
        var byHuduId = new Dictionary<string, Company>(StringComparer.OrdinalIgnoreCase);

        var index = new CompanyMatchIndex(await _db.Companies.ForTenant(_user).ToListAsync(cancellationToken));
        var claimed = new HashSet<Guid>();

        foreach (var dto in companies)
        {
            if (string.IsNullOrWhiteSpace(dto.ExternalId) || string.IsNullOrWhiteSpace(dto.Name))
            {
                skipped++;
                continue;
            }

            var match = index.Find(CompanyIdentity.HuduKey, dto);
            if (match is not null && !claimed.Contains(match.Company.Id))
            {
                var company = match.Company;
                StampHuduId(company, dto.ExternalId);
                index.Add(company);
                claimed.Add(company.Id);
                byHuduId[dto.ExternalId] = company;
                updated++;
                continue;
            }

            if (match is not null && claimed.Contains(match.Company.Id))
            {
                // Two Hudu rows collapsed onto one local company. Keep the first claim.
                skipped++;
                continue;
            }

            var slug = string.IsNullOrWhiteSpace(dto.Slug) ? Slugify(dto.Name) : dto.Slug!;
            var companyNew = new Company
            {
                TenantId = _user.TenantId!.Value,
                Name = dto.Name,
                Slug = await EnsureUniqueSlugAsync(slug, cancellationToken),
                PrimaryDomain = dto.PrimaryDomain,
                City = dto.City,
                State = dto.State,
                Website = dto.Website,
                Address = dto.Address,
                IsActive = dto.IsInactive != true,
            };
            StampHuduId(companyNew, dto.ExternalId);
            _db.Companies.Add(companyNew);
            await _db.SaveChangesAsync(cancellationToken);

            index.Add(companyNew);
            claimed.Add(companyNew.Id);
            byHuduId[dto.ExternalId] = companyNew;
            created++;
        }

        return (created, updated, skipped, byHuduId);
    }

    private async Task<(int Created, int Updated, int Skipped)> ImportArticlesAsync(
        IReadOnlyList<HuduArticleRecord> articles,
        Dictionary<string, Company> companyByHuduId,
        CancellationToken cancellationToken)
    {
        var created = 0;
        var updated = 0;
        var skipped = 0;

        var documents = await _db.Documents.ForTenant(_user).ToListAsync(cancellationToken);
        var byHuduId = new Dictionary<string, Document>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in documents)
        {
            if (TryReadHuduArticleId(doc, out var id))
                byHuduId.TryAdd(id, doc);
        }

        var folders = await _db.DocumentFolders.ForTenant(_user).ToListAsync(cancellationToken);

        foreach (var article in articles)
        {
            if (string.IsNullOrWhiteSpace(article.ExternalId) || string.IsNullOrWhiteSpace(article.Title))
            {
                skipped++;
                continue;
            }

            Guid? companyId = null;
            if (!string.IsNullOrWhiteSpace(article.CompanyExternalId))
            {
                if (!companyByHuduId.TryGetValue(article.CompanyExternalId, out var company))
                {
                    company = await FindCompanyByHuduIdAsync(article.CompanyExternalId, cancellationToken);
                    if (company is not null)
                        companyByHuduId[article.CompanyExternalId] = company;
                }

                if (company is null)
                {
                    skipped++;
                    continue;
                }

                companyId = company.Id;
            }

            var folderName = string.IsNullOrWhiteSpace(article.FolderName) ? DefaultFolderName : article.FolderName.Trim();
            var folder = await EnsureFolderAsync(folders, companyId, folderName, cancellationToken);

            if (byHuduId.TryGetValue(article.ExternalId, out var existing))
            {
                existing.Title = article.Title;
                existing.Content = article.Content ?? existing.Content;
                existing.CompanyId = companyId ?? existing.CompanyId;
                existing.FolderId = folder.Id;
                existing.IsPublished = !article.Draft;
                existing.Tags = MergeHuduTag(existing.Tags, article.ExternalId);
                updated++;
                continue;
            }

            var docNew = new Document
            {
                TenantId = _user.TenantId!.Value,
                Title = article.Title,
                Slug = ArticleSlug(article.ExternalId),
                Summary = article.Slug,
                Content = article.Content,
                Tags = ArticleTag(article.ExternalId),
                IsPublished = !article.Draft,
                CompanyId = companyId,
                FolderId = folder.Id,
            };
            _db.Documents.Add(docNew);
            byHuduId[article.ExternalId] = docNew;
            created++;
        }

        return (created, updated, skipped);
    }

    private async Task<Company?> FindCompanyByHuduIdAsync(string huduId, CancellationToken cancellationToken)
    {
        var companies = await _db.Companies.ForTenant(_user).ToListAsync(cancellationToken);
        foreach (var company in companies)
        {
            if (CompanyIdentity.ReadExternalIds(company.ExternalIdsJson)
                .TryGetValue(CompanyIdentity.HuduKey, out var id)
                && string.Equals(id, huduId, StringComparison.OrdinalIgnoreCase))
            {
                return company;
            }
        }

        return null;
    }

    private async Task<DocumentFolder> EnsureFolderAsync(
        List<DocumentFolder> folders,
        Guid? companyId,
        string name,
        CancellationToken cancellationToken)
    {
        var existing = folders.FirstOrDefault(f =>
            f.CompanyId == companyId
            && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return existing;

        var folder = new DocumentFolder
        {
            TenantId = _user.TenantId!.Value,
            Name = name,
            CompanyId = companyId,
        };
        _db.DocumentFolders.Add(folder);
        await _db.SaveChangesAsync(cancellationToken);
        folders.Add(folder);
        return folder;
    }

    private static void StampHuduId(Company company, string externalId)
    {
        if (CompanyIdentity.ReadExternalIds(company.ExternalIdsJson).ContainsKey(CompanyIdentity.HuduKey))
            return;

        company.ExternalIdsJson = CompanyIdentity.UpsertExternalId(
            company.ExternalIdsJson, CompanyIdentity.HuduKey, externalId);
    }

    public static string ArticleSlug(string externalId) => $"{ArticleSlugPrefix}{externalId}";

    public static string ArticleTag(string externalId) => $"{ArticleTagPrefix}{externalId}";

    public static bool TryReadHuduArticleId(Document document, out string externalId)
    {
        if (!string.IsNullOrWhiteSpace(document.Slug)
            && document.Slug.StartsWith(ArticleSlugPrefix, StringComparison.OrdinalIgnoreCase))
        {
            externalId = document.Slug[ArticleSlugPrefix.Length..];
            if (!string.IsNullOrWhiteSpace(externalId))
                return true;
        }

        if (!string.IsNullOrWhiteSpace(document.Tags))
        {
            foreach (var token in document.Tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.StartsWith(ArticleTagPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    externalId = token[ArticleTagPrefix.Length..];
                    if (!string.IsNullOrWhiteSpace(externalId))
                        return true;
                }
            }
        }

        externalId = "";
        return false;
    }

    private static string MergeHuduTag(string? tags, string externalId)
    {
        var tag = ArticleTag(externalId);
        if (string.IsNullOrWhiteSpace(tags))
            return tag;

        var parts = tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Any(p => string.Equals(p, tag, StringComparison.OrdinalIgnoreCase)))
            return string.Join(',', parts);

        parts.Add(tag);
        return string.Join(',', parts);
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
