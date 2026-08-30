using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

public static class LinkEndpoints
{
    public const string EntityNotFoundMessage = "Entity not found.";
    public const string UnknownEntityTypeMessage = "Unknown entity type.";
    public const string SelfLinkMessage = "Cannot link an entity to itself.";
    public const string DuplicateLinkMessage = "Link already exists.";
    public const string TypeAndIdRequiredMessage = "type and id are required.";

    public static IEndpointRouteBuilder MapLinkEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/links").RequireAuthorization();

        group.MapGet("", async (
            [FromQuery] string? type,
            [FromQuery] Guid? id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(type) || id is null)
                return Results.BadRequest(TypeAndIdRequiredMessage);
            if (!LinkEntityType.TryNormalize(type, out var entityType))
                return Results.BadRequest(UnknownEntityTypeMessage);

            var items = await ListForEntityAsync(db, user, entityType, id.Value, cancellationToken);
            return Results.Ok(items);
        });

        group.MapPost("", async (
            [FromBody] CreateResourceLinkRequest request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
            await CreateAsync(request, db, user, cancellationToken));

        group.MapDelete("/{id:guid}", async (
            Guid id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
            await DeleteAsync(id, db, user, cancellationToken));

        return app;
    }

    public static async Task<IReadOnlyList<ResourceLinkItem>> ListForEntityAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        string entityType,
        Guid entityId,
        CancellationToken cancellationToken = default)
    {
        var links = await db.ResourceLinks.ForTenant(user).AsNoTracking()
            .Where(l =>
                (l.FromType == entityType && l.FromId == entityId)
                || (l.ToType == entityType && l.ToId == entityId))
            .OrderByDescending(l => l.UpdatedAt)
            .ToListAsync(cancellationToken);

        return await MapResolvedAsync(db, user, links, cancellationToken);
    }

    public static async Task<(int Count, IReadOnlyList<RelatedLinkListItem> Items)> LoadRelatedForEntityAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        string entityType,
        Guid entityId,
        int take = CompanyEndpoints.RelatedTake,
        CancellationToken cancellationToken = default)
    {
        var mapped = await ListForEntityAsync(db, user, entityType, entityId, cancellationToken);
        var items = mapped
            .Select(l => ToRelatedItem(l, entityType, entityId))
            .ToList();
        return (items.Count, items.Take(take).ToList());
    }

    public static async Task<IResult> CreateAsync(
        CreateResourceLinkRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        if (!LinkEntityType.TryNormalize(request.FromType, out var fromType)
            || !LinkEntityType.TryNormalize(request.ToType, out var toType))
            return Results.BadRequest(UnknownEntityTypeMessage);

        if (fromType == toType && request.FromId == request.ToId)
            return Results.BadRequest(SelfLinkMessage);

        if (!await EntityExistsInTenantAsync(db, user, fromType, request.FromId, cancellationToken)
            || !await EntityExistsInTenantAsync(db, user, toType, request.ToId, cancellationToken))
            return Results.BadRequest(EntityNotFoundMessage);

        var already = await db.ResourceLinks.ForTenant(user).AnyAsync(
            l => l.FromType == fromType
                 && l.FromId == request.FromId
                 && l.ToType == toType
                 && l.ToId == request.ToId,
            cancellationToken);
        if (already)
            return Results.Conflict(DuplicateLinkMessage);

        var label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim();
        if (label is { Length: > 256 })
            label = label[..256];

        var link = new ResourceLink
        {
            TenantId = user.TenantId.Value,
            FromType = fromType,
            FromId = request.FromId,
            ToType = toType,
            ToId = request.ToId,
            Label = label,
        };
        db.ResourceLinks.Add(link);
        await db.SaveChangesAsync(cancellationToken);

        var mapped = await MapResolvedAsync(db, user, [link], cancellationToken);
        return Results.Created($"/api/links/{link.Id}", mapped[0]);
    }

    public static async Task<IResult> DeleteAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var link = await db.ResourceLinks.ForTenant(user).FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (link is null)
            return Results.NotFound();

        db.ResourceLinks.Remove(link);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    public static async Task<bool> EntityExistsInTenantAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        string entityType,
        Guid entityId,
        CancellationToken cancellationToken = default)
    {
        return entityType switch
        {
            LinkEntityType.Company => await db.Companies.ForTenant(user).AnyAsync(c => c.Id == entityId, cancellationToken),
            LinkEntityType.Asset => await db.Assets.ForTenant(user).AnyAsync(a => a.Id == entityId, cancellationToken),
            LinkEntityType.Document => await db.Documents.ForTenant(user).AnyAsync(d => d.Id == entityId, cancellationToken),
            LinkEntityType.Runbook => await db.Runbooks.ForTenant(user).AnyAsync(r => r.Id == entityId, cancellationToken),
            LinkEntityType.KeeperLink => await db.KeeperLinks.ForTenant(user).AnyAsync(k => k.Id == entityId, cancellationToken),
            _ => false,
        };
    }

    private static async Task<IReadOnlyList<ResourceLinkItem>> MapResolvedAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IReadOnlyList<ResourceLink> links,
        CancellationToken cancellationToken)
    {
        if (links.Count == 0)
            return [];

        var names = await LoadEntityNamesAsync(db, user, links, cancellationToken);
        var items = new List<ResourceLinkItem>(links.Count);
        foreach (var link in links)
        {
            if (!names.TryGetValue((link.FromType, link.FromId), out var fromName))
                continue;
            if (!names.TryGetValue((link.ToType, link.ToId), out var toName))
                continue;
            items.Add(new ResourceLinkItem(
                link.Id,
                link.FromType,
                link.FromId,
                fromName,
                link.ToType,
                link.ToId,
                toName,
                link.Label,
                link.CreatedAt));
        }

        return items;
    }

    private static RelatedLinkListItem ToRelatedItem(ResourceLinkItem link, string entityType, Guid entityId)
    {
        var fromMatch = link.FromType == entityType && link.FromId == entityId;
        return fromMatch
            ? new RelatedLinkListItem(link.Id, link.ToType, link.ToId, link.ToName, link.Label)
            : new RelatedLinkListItem(link.Id, link.FromType, link.FromId, link.FromName, link.Label);
    }

    public static async Task<Dictionary<(string Type, Guid Id), string>> LoadEntityNamesAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IReadOnlyList<ResourceLink> links,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<(string Type, Guid Id), string>();
        var ids = new HashSet<(string Type, Guid Id)>();
        foreach (var link in links)
        {
            ids.Add((link.FromType, link.FromId));
            ids.Add((link.ToType, link.ToId));
        }

        var companyIds = ids.Where(i => i.Type == LinkEntityType.Company).Select(i => i.Id).Distinct().ToList();
        if (companyIds.Count > 0)
        {
            var rows = await db.Companies.ForTenant(user).AsNoTracking()
                .Where(c => companyIds.Contains(c.Id))
                .Select(c => new { c.Id, c.Name })
                .ToListAsync(cancellationToken);
            foreach (var c in rows)
                result[(LinkEntityType.Company, c.Id)] = c.Name;
        }

        var assetIds = ids.Where(i => i.Type == LinkEntityType.Asset).Select(i => i.Id).Distinct().ToList();
        if (assetIds.Count > 0)
        {
            var rows = await db.Assets.ForTenant(user).AsNoTracking()
                .Where(a => assetIds.Contains(a.Id))
                .Select(a => new { a.Id, a.Name })
                .ToListAsync(cancellationToken);
            foreach (var a in rows)
                result[(LinkEntityType.Asset, a.Id)] = a.Name;
        }

        var documentIds = ids.Where(i => i.Type == LinkEntityType.Document).Select(i => i.Id).Distinct().ToList();
        if (documentIds.Count > 0)
        {
            var rows = await db.Documents.ForTenant(user).AsNoTracking()
                .Where(d => documentIds.Contains(d.Id))
                .Select(d => new { d.Id, d.Title })
                .ToListAsync(cancellationToken);
            foreach (var d in rows)
                result[(LinkEntityType.Document, d.Id)] = d.Title;
        }

        var runbookIds = ids.Where(i => i.Type == LinkEntityType.Runbook).Select(i => i.Id).Distinct().ToList();
        if (runbookIds.Count > 0)
        {
            var rows = await db.Runbooks.ForTenant(user).AsNoTracking()
                .Where(r => runbookIds.Contains(r.Id))
                .Select(r => new { r.Id, r.Title })
                .ToListAsync(cancellationToken);
            foreach (var r in rows)
                result[(LinkEntityType.Runbook, r.Id)] = r.Title;
        }

        var keeperIds = ids.Where(i => i.Type == LinkEntityType.KeeperLink).Select(i => i.Id).Distinct().ToList();
        if (keeperIds.Count > 0)
        {
            var rows = await db.KeeperLinks.ForTenant(user).AsNoTracking()
                .Where(k => keeperIds.Contains(k.Id))
                .Select(k => new { k.Id, k.Name })
                .ToListAsync(cancellationToken);
            foreach (var k in rows)
                result[(LinkEntityType.KeeperLink, k.Id)] = k.Name;
        }

        return result;
    }
}

public sealed record ResourceLinkItem(
    Guid Id,
    string FromType,
    Guid FromId,
    string FromName,
    string ToType,
    Guid ToId,
    string ToName,
    string? Label,
    DateTimeOffset CreatedAt);

public sealed record RelatedLinkListItem(
    Guid Id,
    string EntityType,
    Guid EntityId,
    string Name,
    string? Label);

public record CreateResourceLinkRequest(
    string FromType,
    Guid FromId,
    string ToType,
    Guid ToId,
    string? Label = null);
