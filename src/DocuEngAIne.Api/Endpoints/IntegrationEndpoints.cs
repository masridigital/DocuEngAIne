using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
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
        var group = app.MapGroup("/api/mcp/servers").RequireAuthorization();

        group.MapGet("", async (DocuEngAIneDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var items = await db.McpServers.ForTenant(user).AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
            return Results.Ok(items.Select(MapServer));
        });

        group.MapPost("", async (
            [FromBody] CreateMcpServerRequest request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken ct) =>
        {
            if (user.TenantId is null)
                return Results.Unauthorized();

            var server = new McpServer
            {
                TenantId = user.TenantId.Value,
                Name = request.Name,
                Transport = request.Transport,
                EndpointUrl = request.EndpointUrl,
                Command = request.Command,
                ArgsJson = request.ArgsJson,
                Enabled = request.Enabled ?? true,
                AuthSecretName = request.AuthSecretName,
                Notes = request.Notes,
            };
            db.McpServers.Add(server);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/mcp/servers/{server.Id}", MapServer(server));
        });

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
            if (request.Transport.HasValue)
                server.Transport = request.Transport.Value;
            server.EndpointUrl = request.EndpointUrl ?? server.EndpointUrl;
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
        var group = app.MapGroup("/api/integrations").RequireAuthorization();

        group.MapGet("", async (DocuEngAIneDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var items = await db.IntegrationConnections.ForTenant(user).AsNoTracking()
                .OrderBy(i => i.DisplayName).ToListAsync(ct);
            return Results.Ok(items.Select(MapIntegration));
        });

        group.MapPost("", async (
            [FromBody] CreateIntegrationRequest request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken ct) =>
        {
            if (user.TenantId is null)
                return Results.Unauthorized();

            var connection = new IntegrationConnection
            {
                TenantId = user.TenantId.Value,
                Provider = request.Provider,
                DisplayName = request.DisplayName,
                ConfigJson = request.ConfigJson,
                AuthSecretName = request.AuthSecretName,
                McpServerId = request.McpServerId,
                IsEnabled = request.IsEnabled ?? true,
                Status = IntegrationStatus.Disconnected,
            };
            db.IntegrationConnections.Add(connection);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/integrations/{connection.Id}", MapIntegration(connection));
        });

        group.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] UpdateIntegrationRequest request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken ct) =>
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
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

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

        group.MapPost("/{id:guid}/sync", async (
            Guid id,
            [FromBody] SyncPayloadRequest? request,
            IIntegrationSyncService sync,
            CancellationToken ct) =>
        {
            if (request?.Companies is { Count: > 0 })
            {
                var run = await sync.SyncFromPayloadAsync(id, request.Companies.Select(c =>
                    new ExternalCompanyDto(c.ExternalId, c.Name, c.Slug, c.PrimaryDomain, c.City, c.State, c.Website)).ToList(), ct);
                return Results.Ok(MapRun(run));
            }

            var result = await sync.SyncAsync(id, ct);
            return result.Status == SyncRunStatus.Succeeded
                ? Results.Ok(MapRun(result))
                : Results.BadRequest(MapRun(result));
        });

        group.MapGet("/{id:guid}/runs", async (
            Guid id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken ct) =>
        {
            var exists = await db.IntegrationConnections.ForTenant(user).AnyAsync(i => i.Id == id, ct);
            if (!exists)
                return Results.NotFound();

            var runs = await db.SyncRuns.ForTenant(user).AsNoTracking()
                .Where(r => r.IntegrationConnectionId == id)
                .OrderByDescending(r => r.StartedAt)
                .Take(50)
                .ToListAsync(ct);
            return Results.Ok(runs.Select(MapRun));
        });

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

    private static object MapServer(McpServer s) => new
    {
        s.Id,
        s.Name,
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
        i.CreatedAt,
        i.UpdatedAt,
    };

    private static object MapRun(SyncRun r) => new
    {
        r.Id,
        r.IntegrationConnectionId,
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
    McpTransport Transport = McpTransport.Http,
    string? EndpointUrl = null,
    string? Command = null,
    string? ArgsJson = null,
    bool? Enabled = null,
    string? AuthSecretName = null,
    string? Notes = null);

public record UpdateMcpServerRequest(
    string? Name = null,
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
    bool? IsEnabled = null);

public record UpdateIntegrationRequest(
    string? DisplayName = null,
    string? ConfigJson = null,
    string? AuthSecretName = null,
    Guid? McpServerId = null,
    bool? IsEnabled = null);

public record SyncPayloadRequest(List<SyncCompanyDto>? Companies = null);

public record SyncCompanyDto(
    string ExternalId,
    string Name,
    string? Slug = null,
    string? PrimaryDomain = null,
    string? City = null,
    string? State = null,
    string? Website = null);
