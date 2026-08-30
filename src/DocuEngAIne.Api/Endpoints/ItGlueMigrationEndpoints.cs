using System.Text.Json;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using DocuEngAIne.Infrastructure.Integrations.Migration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

/// <summary>
/// One-shot IT Glue import. Not a live integration: there is no recurring pull and no
/// <c>IntegrationProvider.ITGlue</c>. Named distinctly from a future Hudu importer.
/// </summary>
public static class ItGlueMigrationEndpoints
{
    public const string McpServerOrPayloadRequiredMessage =
        "McpServerId or a JSON:API payload is required.";

    public static IEndpointRouteBuilder MapItGlueMigrationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/migrations").RequireAuthorization(AuthExtensions.AdminPolicy);
        group.MapPost("/itglue", ImportAsync);
        return app;
    }

    public static async Task<IResult> ImportAsync(
        [FromBody] ItGlueImportRequest? request,
        IItGlueMigrationService migration,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken ct = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        request ??= new ItGlueImportRequest();

        if (request.McpServerId is Guid mcpId)
        {
            var exists = await db.McpServers.ForTenant(user).AnyAsync(s => s.Id == mcpId, ct);
            if (!exists)
                return Results.NotFound();
        }

        var payloadJson = ResolvePayloadJson(request);
        if (request.McpServerId is null && payloadJson is null)
            return Results.BadRequest(new { message = McpServerOrPayloadRequiredMessage });

        var result = await migration.ImportAsync(request.McpServerId, payloadJson, ct);
        return result.Status == nameof(SyncRunStatus.Succeeded)
            ? Results.Ok(MapResult(result))
            : Results.BadRequest(MapResult(result));
    }

    /// <summary>
    /// Accepts either <c>{ payload: { data: [...] } }</c> or a raw JSON:API envelope
    /// <c>{ data: [...] }</c> as the request body.
    /// </summary>
    public static string? ResolvePayloadJson(ItGlueImportRequest request)
    {
        if (request.Payload.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            return request.Payload.GetRawText();

        if (request.Data.ValueKind == JsonValueKind.Array)
            return JsonSerializer.Serialize(new { data = JsonSerializer.Deserialize<JsonElement>(request.Data.GetRawText()) });

        if (request.Data.ValueKind == JsonValueKind.Object)
            return JsonSerializer.Serialize(new { data = JsonSerializer.Deserialize<JsonElement>(request.Data.GetRawText()) });

        return null;
    }

    private static object MapResult(ItGlueMigrationResult r) => new
    {
        status = r.Status,
        companiesCreated = r.CompaniesCreated,
        companiesUpdated = r.CompaniesUpdated,
        documentsCreated = r.DocumentsCreated,
        documentsUpdated = r.DocumentsUpdated,
        assetsCreated = r.AssetsCreated,
        assetsUpdated = r.AssetsUpdated,
        itemsSkipped = r.ItemsSkipped,
        errorSummary = r.ErrorSummary,
    };
}

/// <param name="McpServerId">Tenant Compact server used to call <c>itg_list_organizations</c>.</param>
/// <param name="Payload">JSON:API envelope. Alternative to <see cref="Data"/>.</param>
/// <param name="Data">Raw JSON:API <c>data</c> when the body is the fixture itself.</param>
public record ItGlueImportRequest(
    Guid? McpServerId = null,
    JsonElement Payload = default,
    JsonElement Data = default);
