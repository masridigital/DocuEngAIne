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

        // Reveal is a POST but semantically a read, and it is left on the tenant-wide Reader gate
        // that already covers reads. Routing it through CanReadAsync would deny nobody who can reach
        // it today — every provisioned User row is Reader or better, and the resource service falls
        // back to that role — while denying a caller whose row has not been provisioned yet. The
        // gap that leaves is real and named in the report: a grant that *lowers* someone to None on
        // one link does not stop them revealing it.
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

        group.MapPost("", PostAsync);
        group.MapPut("/{id:guid}", PutAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);

        return app;
    }

    public static async Task<IResult> PostAsync(
        [FromBody] CreateKeeperLinkRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IResourceAuthorizationService authorization,
        CancellationToken cancellationToken = default)
    {
        // The link does not exist yet, so no grant can name it: creation gates on the tenant-wide
        // role.
        if (await ResourceWriteGuard.RequireTenantWriteAsync(authorization, user, ResourceType.KeeperLink, cancellationToken) is { } denied)
            return denied;

        return await CreateAsync(request, db, user, cancellationToken);
    }

    public static async Task<IResult> PutAsync(
        Guid id,
        [FromBody] UpdateKeeperLinkRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IResourceAuthorizationService authorization,
        CancellationToken cancellationToken = default)
    {
        if (await ResourceWriteGuard.RequireWriteAsync(authorization, user, id, ResourceType.KeeperLink, cancellationToken) is { } denied)
            return denied;

        return await UpdateAsync(id, request, db, user, cancellationToken);
    }

    public static async Task<IResult> DeleteAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IResourceAuthorizationService authorization,
        CancellationToken cancellationToken = default)
    {
        if (await ResourceWriteGuard.RequireWriteAsync(authorization, user, id, ResourceType.KeeperLink, cancellationToken) is { } denied)
            return denied;

        var link = await db.KeeperLinks.ForTenant(user).FirstOrDefaultAsync(k => k.Id == id, cancellationToken);
        if (link is null)
            return Results.NotFound();

        db.KeeperLinks.Remove(link);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    public static async Task<IResult> CreateAsync(
        CreateKeeperLinkRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
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
    }

    public static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateKeeperLinkRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var link = await db.KeeperLinks.ForTenant(user).FirstOrDefaultAsync(k => k.Id == id, cancellationToken);
        if (link is null)
            return Results.NotFound();

        if (await CompanyEndpoints.ApplyCompanyIdOnUpdateAsync(
                db, user, request.CompanyId, request.CompanyIdClear, value => link.CompanyId = value, cancellationToken)
            is { } badCompany)
            return badCompany;

        link.Name = request.Name ?? link.Name;
        link.UsernameHint = request.UsernameHint ?? link.UsernameHint;
        link.KeeperRecordUrl = request.KeeperRecordUrl ?? link.KeeperRecordUrl;
        link.KeeperRecordUid = request.KeeperRecordUid ?? link.KeeperRecordUid;
        link.Notes = request.Notes ?? link.Notes;
        link.AssociatedResourceType = request.AssociatedResourceType ?? link.AssociatedResourceType;
        link.AssociatedResourceId = request.AssociatedResourceId ?? link.AssociatedResourceId;

        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
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
    Guid? CompanyId = null,
    bool CompanyIdClear = false);
