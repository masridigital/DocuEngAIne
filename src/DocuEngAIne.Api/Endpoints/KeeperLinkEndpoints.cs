using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

public static class KeeperLinkEndpoints
{
    public static IEndpointRouteBuilder MapKeeperLinkEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/keeper").RequireAuthorization();

        group.MapGet("", async (
            [FromQuery] string? resourceType,
            [FromQuery] Guid? resourceId,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var query = db.KeeperLinks.ForTenant(user).AsNoTracking();

            if (!string.IsNullOrWhiteSpace(resourceType) && resourceId.HasValue)
            {
                query = query.Where(k => k.AssociatedResourceType == resourceType && k.AssociatedResourceId == resourceId.Value);
            }

            var links = await query.OrderBy(k => k.Name).ToListAsync(cancellationToken);
            return Results.Ok(links.Select(k => MapLink(k, includeUrl: false)));
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var link = await db.KeeperLinks.ForTenant(user).AsNoTracking().FirstOrDefaultAsync(k => k.Id == id, cancellationToken);
            return link is null ? Results.NotFound() : Results.Ok(MapLink(link, includeUrl: false));
        });

        group.MapPost("/{id:guid}/reveal", async (
            Guid id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            IAuditService audit,
            CancellationToken cancellationToken) =>
        {
            var link = await db.KeeperLinks.ForTenant(user).FirstOrDefaultAsync(k => k.Id == id, cancellationToken);
            if (link is null)
                return Results.NotFound();

            if (string.IsNullOrWhiteSpace(link.KeeperRecordUrl))
                return Results.BadRequest("No Keeper URL configured for this link.");

            await audit.LogAsync("KeeperLink.Reveal", nameof(KeeperLink), link.Id, $"User revealed link '{link.Name}'", cancellationToken);

            return Results.Ok(new { link.KeeperRecordUrl, link.Name });
        });

        group.MapPost("", async (
            [FromBody] CreateKeeperLinkRequest request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            if (await CompanyEndpoints.EnsureCompanyInTenantAsync(db, user, request.CompanyId, cancellationToken) is { } badCompany)
                return badCompany;

            var link = new KeeperLink
            {
                TenantId = user.TenantId!.Value,
                Name = request.Name,
                UsernameHint = request.UsernameHint,
                KeeperRecordUrl = request.KeeperRecordUrl,
                KeeperRecordUid = request.KeeperRecordUid,
                Notes = request.Notes,
                AssociatedResourceType = request.AssociatedResourceType,
                AssociatedResourceId = request.AssociatedResourceId,
                CompanyId = request.CompanyId,
            };

            db.KeeperLinks.Add(link);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/keeper/{link.Id}", MapLink(link, includeUrl: false));
        });

        group.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] UpdateKeeperLinkRequest request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var link = await db.KeeperLinks.ForTenant(user).FirstOrDefaultAsync(k => k.Id == id, cancellationToken);
            if (link is null)
                return Results.NotFound();

            if (await CompanyEndpoints.EnsureCompanyInTenantAsync(db, user, request.CompanyId, cancellationToken) is { } badCompany)
                return badCompany;
            if (request.CompanyId is Guid companyId)
                link.CompanyId = companyId;

            link.Name = request.Name ?? link.Name;
            link.UsernameHint = request.UsernameHint ?? link.UsernameHint;
            link.KeeperRecordUrl = request.KeeperRecordUrl ?? link.KeeperRecordUrl;
            link.KeeperRecordUid = request.KeeperRecordUid ?? link.KeeperRecordUid;
            link.Notes = request.Notes ?? link.Notes;
            link.AssociatedResourceType = request.AssociatedResourceType ?? link.AssociatedResourceType;
            link.AssociatedResourceId = request.AssociatedResourceId ?? link.AssociatedResourceId;

            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        group.MapDelete("/{id:guid}", async (
            Guid id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var link = await db.KeeperLinks.ForTenant(user).FirstOrDefaultAsync(k => k.Id == id, cancellationToken);
            if (link is null)
                return Results.NotFound();

            db.KeeperLinks.Remove(link);
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        return app;
    }

    private static object MapLink(KeeperLink link, bool includeUrl) => new
    {
        link.Id,
        link.Name,
        link.UsernameHint,
        KeeperRecordUrl = includeUrl ? link.KeeperRecordUrl : null,
        link.KeeperRecordUid,
        link.Notes,
        link.AssociatedResourceType,
        link.AssociatedResourceId,
        link.CompanyId,
        link.UpdatedAt,
    };
}

public record CreateKeeperLinkRequest(
    string Name,
    string? UsernameHint,
    string KeeperRecordUrl,
    string? KeeperRecordUid,
    string? Notes,
    string? AssociatedResourceType,
    Guid? AssociatedResourceId,
    Guid? CompanyId = null);

public record UpdateKeeperLinkRequest(
    string? Name,
    string? UsernameHint,
    string? KeeperRecordUrl,
    string? KeeperRecordUid,
    string? Notes,
    string? AssociatedResourceType,
    Guid? AssociatedResourceId,
    Guid? CompanyId = null);
