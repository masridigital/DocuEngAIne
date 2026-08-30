using System.Text.Json;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

/// <summary>
/// One-shot Hudu import. Not a live integration: there is no recurring pull and no
/// <c>IntegrationProvider.Hudu</c>. Named distinctly from the IT Glue importer
/// (<c>ItGlueMigrationEndpoints</c>) so both can live under <c>/api/migrations</c>.
/// </summary>
public static class HuduMigrationEndpoints
{
    public static IEndpointRouteBuilder MapHuduMigrationEndpoints(this IEndpointRouteBuilder app)
    {
        // Admin-only: this reaches out through Compact on the tenant's behalf and writes companies/docs.
        var group = app.MapGroup("/api/migrations").RequireAuthorization(AuthExtensions.AdminPolicy);

        group.MapPost("/hudu", ImportHuduAsync);
        return app;
    }

    /// <summary>
    /// One-shot Hudu import through a tenant Compact MCP server. Other-tenant or non-Compact
    /// <c>McpServerId</c> is 404. Password entities in the payload are counted and discarded.
    /// </summary>
    public static async Task<IResult> ImportHuduAsync(
        [FromBody] HuduImportRequest request,
        IHuduMigrationService migration,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        var server = await db.McpServers.ForTenant(user)
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.Id == request.McpServerId && s.Kind == McpServerKind.StackJackCompact,
                cancellationToken);
        if (server is null)
            return Results.NotFound();

        try
        {
            var result = await migration.ImportAsync(request.McpServerId, ToPayload(request), cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    }

    private static HuduImportPayload? ToPayload(HuduImportRequest request)
    {
        if (request.Companies is null && request.Articles is null && request.Passwords is null)
            return null;

        return new HuduImportPayload(
            Companies: request.Companies?.Select(c => new ExternalCompanyDto(
                c.ExternalId, c.Name, c.Slug, c.PrimaryDomain, c.City, c.State, c.Website, c.Address, c.IsInactive)).ToList(),
            Articles: request.Articles?.Select(a => new HuduArticleRecord(
                a.ExternalId, a.Title, a.Content, a.Slug, a.CompanyExternalId, a.FolderExternalId, a.FolderName, a.Draft)).ToList(),
            PasswordCount: request.Passwords?.Count ?? 0);
    }
}

public record HuduImportRequest(
    Guid McpServerId,
    List<SyncCompanyDto>? Companies = null,
    List<HuduArticleImportDto>? Articles = null,
    List<JsonElement>? Passwords = null);

public record HuduArticleImportDto(
    string ExternalId,
    string Title,
    string? Content = null,
    string? Slug = null,
    string? CompanyExternalId = null,
    string? FolderExternalId = null,
    string? FolderName = null,
    bool Draft = false);
