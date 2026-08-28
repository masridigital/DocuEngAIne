using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Infrastructure.Integrations;

public class IntegrationSyncService : IIntegrationSyncService
{
    private readonly DocuEngAIneDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IMcpClient _mcpClient;
    private readonly IAuditService _audit;

    public IntegrationSyncService(
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

    public async Task<(bool Ok, string Message)> TestConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        var connection = await _db.IntegrationConnections.ForTenant(_user)
            .FirstOrDefaultAsync(c => c.Id == connectionId, cancellationToken);
        if (connection is null)
            return (false, "Integration not found.");

        if (connection.McpServerId is Guid mcpId)
        {
            try
            {
                await _mcpClient.ListToolsAsync(mcpId, cancellationToken);
                connection.Status = IntegrationStatus.Connected;
                connection.LastError = null;
                await _db.SaveChangesAsync(cancellationToken);
                return (true, "MCP server responded to tools/list.");
            }
            catch (Exception ex)
            {
                connection.Status = IntegrationStatus.Error;
                connection.LastError = ex.Message;
                await _db.SaveChangesAsync(cancellationToken);
                return (false, ex.Message);
            }
        }

        if (string.IsNullOrWhiteSpace(connection.AuthSecretName) && string.IsNullOrWhiteSpace(connection.ConfigJson))
        {
            connection.Status = IntegrationStatus.Error;
            connection.LastError = "AuthSecretName or McpServerId required.";
            await _db.SaveChangesAsync(cancellationToken);
            return (false, connection.LastError);
        }

        connection.Status = IntegrationStatus.Connected;
        connection.LastError = null;
        await _db.SaveChangesAsync(cancellationToken);
        return (true, "Configuration present. Live API probe deferred until credentials are loaded from Key Vault.");
    }

    public async Task<SyncRun> SyncAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        var connection = await _db.IntegrationConnections.ForTenant(_user)
            .FirstOrDefaultAsync(c => c.Id == connectionId, cancellationToken)
            ?? throw new InvalidOperationException("Integration not found.");

        if (connection.Provider == IntegrationProvider.Halo)
        {
            if (connection.McpServerId is not Guid mcpId)
            {
                return await FailRunAsync(connection,
                    "Halo sync requires a linked StackJack Compact MCP server (McpServerId). AuthSecretName is a Key Vault name only; secrets are never stored in SQL.",
                    cancellationToken);
            }

            try
            {
                var companies = await PullHaloCompaniesAsync(connection, mcpId, cancellationToken);
                return await SyncFromPayloadAsync(connection.Id, companies, cancellationToken);
            }
            catch (Exception ex)
            {
                return await FailRunAsync(connection, ex.Message, cancellationToken);
            }
        }

        if (string.IsNullOrWhiteSpace(connection.AuthSecretName) && connection.McpServerId is null)
        {
            return await FailRunAsync(connection,
                "Configure AuthSecretName (Key Vault) or link an McpServer before syncing.",
                cancellationToken);
        }

        // Ninja/CIPP/Meraki/UniFi/Composio live pulls are out of scope this slice — payload path remains.
        return await FailRunAsync(connection,
            "No sync payload supplied. Use SyncFromPayload (tests/importers) or wire MCP tool results into company upsert.",
            cancellationToken);
    }

    public async Task<SyncRun> SyncFromPayloadAsync(
        Guid connectionId,
        IReadOnlyList<ExternalCompanyDto> companies,
        CancellationToken cancellationToken = default)
    {
        var connection = await _db.IntegrationConnections.ForTenant(_user)
            .FirstOrDefaultAsync(c => c.Id == connectionId, cancellationToken)
            ?? throw new InvalidOperationException("Integration not found.");

        var run = await StartRunAsync(connection, cancellationToken);
        connection.Status = IntegrationStatus.Syncing;

        try
        {
            // SkipContacts/SkipLocations/SkipAssets/AutoUpdateAssetNames document intent for
            // later live Halo/Ninja pulls. v1 payload upsert is companies only.
            foreach (var dto in companies)
            {
                if (connection.SkipInactive && dto.IsInactive == true)
                {
                    run.ItemsSkipped++;
                    continue;
                }

                var mapping = await _db.IntegrationMappings.ForTenant(_user)
                    .FirstOrDefaultAsync(m =>
                        m.IntegrationConnectionId == connection.Id
                        && m.ExternalType == "company"
                        && m.ExternalId == dto.ExternalId, cancellationToken);

                Company company;
                if (mapping is null)
                {
                    var slug = string.IsNullOrWhiteSpace(dto.Slug)
                        ? Slugify(dto.Name)
                        : dto.Slug!;

                    company = new Company
                    {
                        TenantId = _user.TenantId!.Value,
                        Name = dto.Name,
                        Slug = await EnsureUniqueSlugAsync(slug, cancellationToken),
                        PrimaryDomain = dto.PrimaryDomain,
                        City = dto.City,
                        State = dto.State,
                        Website = dto.Website,
                        Address = dto.Address,
                        HaloClientId = connection.Provider == IntegrationProvider.Halo ? dto.ExternalId : null,
                        NinjaOrganizationId = connection.Provider == IntegrationProvider.NinjaOne ? dto.ExternalId : null,
                    };
                    _db.Companies.Add(company);
                    await _db.SaveChangesAsync(cancellationToken);

                    _db.IntegrationMappings.Add(new IntegrationMapping
                    {
                        TenantId = _user.TenantId!.Value,
                        IntegrationConnectionId = connection.Id,
                        ExternalId = dto.ExternalId,
                        ExternalType = "company",
                        LocalEntityType = nameof(Company),
                        LocalEntityId = company.Id,
                    });
                    run.ItemsCreated++;
                }
                else
                {
                    company = await _db.Companies.ForTenant(_user)
                        .FirstAsync(c => c.Id == mapping.LocalEntityId, cancellationToken);
                    if (connection.UpdateCompanyDetails)
                    {
                        company.Name = dto.Name;
                        company.PrimaryDomain = dto.PrimaryDomain ?? company.PrimaryDomain;
                        company.City = dto.City ?? company.City;
                        company.State = dto.State ?? company.State;
                        company.Website = dto.Website ?? company.Website;
                        company.Address = dto.Address ?? company.Address;
                    }
                    if (connection.Provider == IntegrationProvider.Halo
                        && (connection.UpdateCompanyDetails || string.IsNullOrEmpty(company.HaloClientId)))
                        company.HaloClientId = dto.ExternalId;
                    if (connection.Provider == IntegrationProvider.NinjaOne
                        && (connection.UpdateCompanyDetails || string.IsNullOrEmpty(company.NinjaOrganizationId)))
                        company.NinjaOrganizationId = dto.ExternalId;
                    run.ItemsUpdated++;
                }
            }

            await _db.SaveChangesAsync(cancellationToken);
            run.Status = SyncRunStatus.Succeeded;
            run.FinishedAt = DateTimeOffset.UtcNow;
            connection.Status = IntegrationStatus.Connected;
            connection.LastSyncAt = run.FinishedAt;
            connection.LastError = null;
            await _audit.LogAsync("Integration.Sync", nameof(IntegrationConnection), connection.Id,
                $"created={run.ItemsCreated} updated={run.ItemsUpdated}", cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            return run;
        }
        catch (Exception ex)
        {
            run.Status = SyncRunStatus.Failed;
            run.FinishedAt = DateTimeOffset.UtcNow;
            run.ErrorSummary = ex.Message;
            connection.Status = IntegrationStatus.Error;
            connection.LastError = ex.Message;
            await _db.SaveChangesAsync(cancellationToken);
            return run;
        }
    }

    private async Task<IReadOnlyList<ExternalCompanyDto>> PullHaloCompaniesAsync(
        IntegrationConnection connection,
        Guid mcpServerId,
        CancellationToken cancellationToken)
    {
        var server = await _db.McpServers.ForTenant(_user)
            .FirstOrDefaultAsync(s => s.Id == mcpServerId, cancellationToken)
            ?? throw new InvalidOperationException("MCP server not found.");

        if (server.Kind != McpServerKind.StackJackCompact)
            throw new InvalidOperationException("Halo company pull requires a StackJack Compact MCP server. Composio is not a Halo connector.");

        const int maxPages = 500;
        var companies = new List<ExternalCompanyDto>();
        for (var pageNo = 1; pageNo <= maxPages; pageNo++)
        {
            var args = HaloClientMapper.BuildArgumentsJson(pageNo, connection.SkipInactive);
            var body = await _mcpClient.CallToolAsync(mcpServerId, HaloClientMapper.ToolName, args, cancellationToken);
            var page = HaloClientMapper.MapClients(body);
            companies.AddRange(page);
            if (page.Count < HaloClientMapper.DefaultPageSize)
                break;
        }

        return companies;
    }

    private async Task<SyncRun> FailRunAsync(IntegrationConnection connection, string error, CancellationToken cancellationToken)
    {
        var run = await StartRunAsync(connection, cancellationToken);
        run.Status = SyncRunStatus.Failed;
        run.FinishedAt = DateTimeOffset.UtcNow;
        run.ErrorSummary = error;
        connection.Status = IntegrationStatus.Error;
        connection.LastError = error;
        await _db.SaveChangesAsync(cancellationToken);
        return run;
    }

    private async Task<SyncRun> StartRunAsync(IntegrationConnection connection, CancellationToken cancellationToken)
    {
        var run = new SyncRun
        {
            TenantId = _user.TenantId!.Value,
            IntegrationConnectionId = connection.Id,
            StartedAt = DateTimeOffset.UtcNow,
            Status = SyncRunStatus.Running,
        };
        _db.SyncRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken);
        return run;
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
