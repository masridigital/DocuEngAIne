using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Core.Mcp;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using DocuEngAIne.Infrastructure.Integrations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

public static class IntegrationEndpoints
{
    public static IEndpointRouteBuilder MapIntegrationEndpoints(this IEndpointRouteBuilder app)
    {
        MapMcpServers(app);
        MapIntegrations(app);
        return app;
    }

    private static void MapMcpServers(IEndpointRouteBuilder app)
    {
        // Admin-only as a group, reads included: an MCP server row is an outbound egress target plus
        // the name of a Key Vault secret, so even listing it tells a caller where our credentials point.
        var group = app.MapGroup("/api/mcp/servers").RequireAuthorization(AuthExtensions.AdminPolicy);

        group.MapGet("", async (DocuEngAIneDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var items = await db.McpServers.ForTenant(user).AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
            return Results.Ok(items.Select(MapServer));
        });

        group.MapGet("/{id:guid}", async (Guid id, DocuEngAIneDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var server = await db.McpServers.ForTenant(user).AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
            return server is null ? Results.NotFound() : Results.Ok(MapServer(server));
        });

        group.MapPost("", CreateMcpServerAsync);

        group.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] UpdateMcpServerRequest request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken ct) =>
        {
            var server = await db.McpServers.ForTenant(user).FirstOrDefaultAsync(s => s.Id == id, ct);
            if (server is null)
                return Results.NotFound();

            server.Name = request.Name ?? server.Name;
            if (request.Kind.HasValue)
                server.Kind = request.Kind.Value;
            if (request.Transport.HasValue)
                server.Transport = request.Transport.Value;
            if (request.EndpointUrl is not null)
                server.EndpointUrl = McpServerDefaults.ResolveEndpoint(server.Kind, request.EndpointUrl);
            else if (request.Kind.HasValue)
                server.EndpointUrl = McpServerDefaults.EndpointFor(server.Kind);
            server.Command = request.Command ?? server.Command;
            server.ArgsJson = request.ArgsJson ?? server.ArgsJson;
            if (request.Enabled.HasValue)
                server.Enabled = request.Enabled.Value;
            server.AuthSecretName = request.AuthSecretName ?? server.AuthSecretName;
            server.Notes = request.Notes ?? server.Notes;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapDelete("/{id:guid}", async (Guid id, DocuEngAIneDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var server = await db.McpServers.ForTenant(user).FirstOrDefaultAsync(s => s.Id == id, ct);
            if (server is null)
                return Results.NotFound();
            db.McpServers.Remove(server);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static void MapIntegrations(IEndpointRouteBuilder app)
    {
        // Admin-only as a group: these routes hold PSA/RMM connection config and secret names, and
        // /sync and /test reach out to third-party systems on the tenant's behalf.
        var group = app.MapGroup("/api/integrations").RequireAuthorization(AuthExtensions.AdminPolicy);

        group.MapGet("", async (DocuEngAIneDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var items = await db.IntegrationConnections.ForTenant(user).AsNoTracking()
                .OrderBy(i => i.DisplayName).ToListAsync(ct);
            return Results.Ok(items.Select(MapIntegration));
        });

        group.MapGet("/{id:guid}", GetIntegrationAsync);

        group.MapPost("", CreateIntegrationAsync);

        group.MapPut("/{id:guid}", UpdateIntegrationAsync);

        group.MapDelete("/{id:guid}", async (Guid id, DocuEngAIneDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var connection = await db.IntegrationConnections.ForTenant(user).FirstOrDefaultAsync(i => i.Id == id, ct);
            if (connection is null)
                return Results.NotFound();
            db.IntegrationConnections.Remove(connection);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapPost("/{id:guid}/test", async (
            Guid id,
            IIntegrationSyncService sync,
            CancellationToken ct) =>
        {
            var (ok, message) = await sync.TestConnectionAsync(id, ct);
            return ok ? Results.Ok(new { ok, message }) : Results.BadRequest(new { ok, message });
        });

        group.MapPost("/{id:guid}/sync", SyncAsync);

        group.MapGet("/{id:guid}/runs", ListRunsAsync);

        group.MapGet("/{id:guid}/mappings", async (
            Guid id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken ct) =>
        {
            var exists = await db.IntegrationConnections.ForTenant(user).AnyAsync(i => i.Id == id, ct);
            if (!exists)
                return Results.NotFound();

            var mappings = await db.IntegrationMappings.ForTenant(user).AsNoTracking()
                .Where(m => m.IntegrationConnectionId == id)
                .OrderBy(m => m.ExternalType).ThenBy(m => m.ExternalId)
                .ToListAsync(ct);
            return Results.Ok(mappings.Select(m => new
            {
                m.Id,
                m.ExternalId,
                m.ExternalType,
                m.LocalEntityType,
                m.LocalEntityId,
                m.MetadataJson,
            }));
        });
    }

    public static async Task<IResult> CreateMcpServerAsync(
        [FromBody] CreateMcpServerRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken ct = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        var kind = request.Kind ?? McpServerKind.StackJackCompact;
        var secretName = string.IsNullOrWhiteSpace(request.AuthSecretName) ? null : request.AuthSecretName.Trim();
        var server = new McpServer
        {
            TenantId = user.TenantId.Value,
            Name = request.Name,
            Kind = kind,
            Transport = request.Transport,
            EndpointUrl = McpServerDefaults.ResolveEndpoint(kind, request.EndpointUrl),
            Command = request.Command,
            ArgsJson = request.ArgsJson,
            Enabled = request.Enabled ?? true,
            AuthSecretName = secretName,
            Notes = request.Notes,
        };
        db.McpServers.Add(server);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/mcp/servers/{server.Id}", MapServer(server));
    }

    public static async Task<IResult> GetIntegrationAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken ct = default)
    {
        var connection = await db.IntegrationConnections.ForTenant(user).AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == id, ct);
        return connection is null ? Results.NotFound() : Results.Ok(MapIntegration(connection));
    }

    public static async Task<IResult> UpdateIntegrationAsync(
        Guid id,
        [FromBody] UpdateIntegrationRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken ct = default)
    {
        var connection = await db.IntegrationConnections.ForTenant(user).FirstOrDefaultAsync(i => i.Id == id, ct);
        if (connection is null)
            return Results.NotFound();

        connection.DisplayName = request.DisplayName ?? connection.DisplayName;
        connection.ConfigJson = request.ConfigJson ?? connection.ConfigJson;
        connection.AuthSecretName = request.AuthSecretName ?? connection.AuthSecretName;
        if (request.McpServerId.HasValue)
            connection.McpServerId = request.McpServerId;
        if (request.IsEnabled.HasValue)
            connection.IsEnabled = request.IsEnabled.Value;
        if (request.SkipInactive.HasValue)
            connection.SkipInactive = request.SkipInactive.Value;
        if (request.SkipContacts.HasValue)
            connection.SkipContacts = request.SkipContacts.Value;
        if (request.SkipLocations.HasValue)
            connection.SkipLocations = request.SkipLocations.Value;
        if (request.SkipAssets.HasValue)
            connection.SkipAssets = request.SkipAssets.Value;
        if (request.AutoUpdateAssetNames.HasValue)
            connection.AutoUpdateAssetNames = request.AutoUpdateAssetNames.Value;
        if (request.UpdateCompanyDetails.HasValue)
            connection.UpdateCompanyDetails = request.UpdateCompanyDetails.Value;
        // Zero (or negative) clears the override and hands cadence back to the detected plan;
        // SyncCadencePolicy already ignores non-positive values, so storing null keeps the two ways
        // of saying "no override" from disagreeing in the API response.
        if (request.SyncIntervalMinutesOverride.HasValue)
        {
            connection.SyncIntervalMinutesOverride = request.SyncIntervalMinutesOverride.Value > 0
                ? request.SyncIntervalMinutesOverride.Value
                : null;
        }
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Creates an integration. StackJack Compact is built in: for a Compact-backed provider the caller
    /// supplies a Key Vault secret name and nothing else, and the tenant's Compact registration is
    /// resolved — or created once, at <see cref="McpServerDefaults.StackJackCompactEndpoint"/> — here.
    /// An explicit <c>McpServerId</c> still wins, for anyone pointing at a specific server.
    /// </summary>
    public static async Task<IResult> CreateIntegrationAsync(
        [FromBody] CreateIntegrationRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken ct = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        var secretName = string.IsNullOrWhiteSpace(request.AuthSecretName) ? null : request.AuthSecretName.Trim();
        var mcpServerId = request.McpServerId;

        if (mcpServerId is Guid explicitId)
        {
            // Checked here rather than at first sync: an id belonging to another tenant, or a typo,
            // would otherwise sit on the connection until someone pressed Sync and got "not found".
            if (!await db.McpServers.ForTenant(user).AnyAsync(s => s.Id == explicitId, ct))
                return Results.BadRequest(new { message = "McpServerId does not match an MCP server for this tenant." });
        }
        else if (McpServerDefaults.IsCompactBacked(request.Provider))
        {
            var compact = await db.McpServers.ForTenant(user)
                .Where(s => s.Kind == McpServerKind.StackJackCompact)
                .OrderByDescending(s => s.Enabled)
                .ThenBy(s => s.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (compact is null)
            {
                if (secretName is null)
                {
                    return Results.BadRequest(new
                    {
                        message = "AuthSecretName is required: it is the Key Vault secret name holding this tenant's "
                            + "StackJack Compact API key. Supply it, or link an existing server with McpServerId.",
                    });
                }

                compact = new McpServer
                {
                    TenantId = user.TenantId.Value,
                    Name = McpServerDefaults.StackJackCompactName,
                    Kind = McpServerKind.StackJackCompact,
                    Transport = McpTransport.Http,
                    EndpointUrl = McpServerDefaults.StackJackCompactEndpoint,
                    Enabled = true,
                    AuthSecretName = secretName,
                };
                db.McpServers.Add(compact);
                await db.SaveChangesAsync(ct);
            }
            else if (string.IsNullOrWhiteSpace(compact.AuthSecretName))
            {
                // Filling in a registration that never carried a secret name is not an overwrite.
                // But if the caller supplied nothing either, the connection would be created against a
                // server with no credential and every sync would fail later as an opaque vendor 401.
                if (string.IsNullOrWhiteSpace(secretName))
                {
                    return Results.BadRequest(
                        $"MCP server '{compact.Name}' has no Key Vault secret name, so this integration would have "
                        + "no credential to authenticate with. Supply authSecretName.");
                }

                compact.AuthSecretName = secretName;
            }
            else if (secretName is not null
                && !string.Equals(compact.AuthSecretName, secretName, StringComparison.OrdinalIgnoreCase))
            {
                // Deliberately neither of the silent options. Reusing the stored name would
                // authenticate with a credential the admin did not name; rewriting it would repoint
                // every other integration already running through this server. Make them choose.
                return Results.Conflict(new
                {
                    message = $"MCP server '{compact.Name}' already points at Key Vault secret '{compact.AuthSecretName}'. "
                        + "Omit AuthSecretName to reuse it, pass McpServerId to use a different server, or update the "
                        + "server itself at PUT /api/mcp/servers/{id} if the credential really moved.",
                });
            }

            mcpServerId = compact.Id;
        }

        var connection = new IntegrationConnection
        {
            TenantId = user.TenantId.Value,
            Provider = request.Provider,
            DisplayName = request.DisplayName,
            ConfigJson = request.ConfigJson,
            AuthSecretName = secretName,
            McpServerId = mcpServerId,
            IsEnabled = request.IsEnabled ?? true,
            Status = IntegrationStatus.Disconnected,
            SkipInactive = request.SkipInactive ?? true,
            SkipContacts = request.SkipContacts ?? false,
            SkipLocations = request.SkipLocations ?? false,
            SkipAssets = request.SkipAssets ?? false,
            AutoUpdateAssetNames = request.AutoUpdateAssetNames ?? false,
            UpdateCompanyDetails = request.UpdateCompanyDetails ?? false,
        };
        db.IntegrationConnections.Add(connection);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/integrations/{connection.Id}", MapIntegration(connection));
    }

    /// <summary>
    /// The 50 most recent <see cref="SyncRun"/> rows for one integration. Tenant-scoped on both
    /// the connection and the runs so another tenant's id is a 404, never a leak.
    /// </summary>
    public static async Task<IResult> ListRunsAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken ct = default)
    {
        var connection = await db.IntegrationConnections.ForTenant(user).AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == id, ct);
        if (connection is null)
            return Results.NotFound();

        var runs = await db.SyncRuns.ForTenant(user).AsNoTracking()
            .Where(r => r.IntegrationConnectionId == id)
            .OrderByDescending(r => r.StartedAt)
            .Take(50)
            .ToListAsync(ct);
        return Results.Ok(runs.Select(r => MapRun(r, connection.Provider)));
    }

    public static async Task<IResult> SyncAsync(
        Guid id,
        [FromBody] SyncPayloadRequest? request,
        IIntegrationSyncService sync,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken ct = default)
    {
        var connection = await db.IntegrationConnections.ForTenant(user).AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == id, ct);
        if (connection is null)
            return Results.NotFound();

        if (request?.Companies is { Count: > 0 })
        {
            var run = await sync.SyncFromPayloadAsync(id, request.Companies.Select(c =>
                new ExternalCompanyDto(c.ExternalId, c.Name, c.Slug, c.PrimaryDomain, c.City, c.State, c.Website, c.Address, c.IsInactive)).ToList(), ct);
            return Results.Ok(MapRun(run, connection.Provider));
        }

        var result = await sync.SyncAsync(id, ct);
        return result.Status == SyncRunStatus.Succeeded
            ? Results.Ok(MapRun(result, connection.Provider))
            : Results.BadRequest(MapRun(result, connection.Provider));
    }

    private static object MapServer(McpServer s) => new
    {
        s.Id,
        s.Name,
        Kind = s.Kind.ToString(),
        Transport = s.Transport.ToString(),
        s.EndpointUrl,
        s.Command,
        s.ArgsJson,
        s.Enabled,
        s.AuthSecretName,
        s.Notes,
        s.CreatedAt,
        s.UpdatedAt,
    };

    private static object MapIntegration(IntegrationConnection i) => new
    {
        i.Id,
        Provider = i.Provider.ToString(),
        i.DisplayName,
        Status = i.Status.ToString(),
        i.ConfigJson,
        i.AuthSecretName,
        i.McpServerId,
        i.LastSyncAt,
        i.LastError,
        i.IsEnabled,
        i.SkipInactive,
        i.SkipContacts,
        i.SkipLocations,
        i.SkipAssets,
        i.AutoUpdateAssetNames,
        i.UpdateCompanyDetails,
        StackJackPlan = i.StackJackPlan.ToString(),
        i.MonthlyCallLimit,
        i.PlanDetectedAt,
        i.SyncIntervalMinutesOverride,
        // Derived, never stored: override if set, otherwise the plan-derived interval. Null means
        // manual only — no allowance has been detected and the scheduler will skip the connection.
        SyncIntervalMinutes = SyncCadencePolicy.IntervalMinutesFor(i),
        NextSyncDueAt = SyncCadencePolicy.NextDueAt(i),
        i.CreatedAt,
        i.UpdatedAt,
    };

    private static object MapRun(SyncRun r, IntegrationProvider provider) => new
    {
        r.Id,
        r.IntegrationConnectionId,
        Provider = provider.ToString(),
        r.StartedAt,
        r.FinishedAt,
        Status = r.Status.ToString(),
        r.ItemsCreated,
        r.ItemsUpdated,
        r.ItemsSkipped,
        r.ErrorSummary,
    };
}

public record CreateMcpServerRequest(
    string Name,
    McpServerKind? Kind = null,
    McpTransport Transport = McpTransport.Http,
    string? EndpointUrl = null,
    string? Command = null,
    string? ArgsJson = null,
    bool? Enabled = null,
    string? AuthSecretName = null,
    string? Notes = null);

public record UpdateMcpServerRequest(
    string? Name = null,
    McpServerKind? Kind = null,
    McpTransport? Transport = null,
    string? EndpointUrl = null,
    string? Command = null,
    string? ArgsJson = null,
    bool? Enabled = null,
    string? AuthSecretName = null,
    string? Notes = null);

public record CreateIntegrationRequest(
    IntegrationProvider Provider,
    string DisplayName,
    string? ConfigJson = null,
    string? AuthSecretName = null,
    Guid? McpServerId = null,
    bool? IsEnabled = null,
    bool? SkipInactive = null,
    bool? SkipContacts = null,
    bool? SkipLocations = null,
    bool? SkipAssets = null,
    bool? AutoUpdateAssetNames = null,
    bool? UpdateCompanyDetails = null);

public record UpdateIntegrationRequest(
    string? DisplayName = null,
    string? ConfigJson = null,
    string? AuthSecretName = null,
    Guid? McpServerId = null,
    bool? IsEnabled = null,
    bool? SkipInactive = null,
    bool? SkipContacts = null,
    bool? SkipLocations = null,
    bool? SkipAssets = null,
    bool? AutoUpdateAssetNames = null,
    bool? UpdateCompanyDetails = null,
    // Minutes between scheduled checks. Omit to leave as-is; 0 clears the override.
    int? SyncIntervalMinutesOverride = null);

public record SyncPayloadRequest(List<SyncCompanyDto>? Companies = null);

public record SyncCompanyDto(
    string ExternalId,
    string Name,
    string? Slug = null,
    string? PrimaryDomain = null,
    string? City = null,
    string? State = null,
    string? Website = null,
    string? Address = null,
    bool? IsInactive = null);
