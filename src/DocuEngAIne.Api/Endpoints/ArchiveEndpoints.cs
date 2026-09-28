using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

/// <summary>
/// The Museum: DELETE on an asset / document / runbook / Keeper link archives it (soft delete +
/// <see cref="ArchiveEntry"/>) instead of destroying it. Archived rows are hidden by the global
/// query filter everywhere; this surface lists them, restores them, and — Admin only — destroys
/// them for good, leaving the entry as a tombstone.
/// </summary>
public static class ArchiveEndpoints
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    public const string StateArchived = "archived";
    public const string StateRestored = "restored";
    public const string StateDeleted = "deleted";
    public const string StateAll = "all";

    public static IEndpointRouteBuilder MapArchiveEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/archive").RequireAuthorization();

        group.MapGet("", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPost("/{id:guid}/restore", RestoreAsync);
        // Irreversible, so it sits above the per-resource write gate that archive/restore use.
        group.MapDelete("/{id:guid}", PermanentDeleteAsync).RequireAuthorization(AuthExtensions.AdminPolicy);

        return app;
    }

    public sealed record ArchiveEntryItem(
        Guid Id,
        string ResourceType,
        Guid ResourceId,
        string ResourceLabel,
        string? Reason,
        string State,
        DateTimeOffset ArchivedAt,
        string? ArchivedByName,
        string? ArchivedByObjectId,
        DateTimeOffset? RestoredAt,
        DateTimeOffset? PermanentlyDeletedAt);

    public sealed record ArchivePage(int Total, int Page, int PageSize, IReadOnlyList<ArchiveEntryItem> Items);

    /// <summary>
    /// Archives one live resource: stamps <c>DeletedAt</c>, registers the entry, audits the act.
    /// Callers have already run the resource's write gate and loaded the row ForTenant.
    /// </summary>
    public static async Task ArchiveAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IAuditService? audit,
        ISoftDeletable entity,
        string resourceType,
        Guid resourceId,
        string label,
        string? reason,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var trimmedReason = string.IsNullOrWhiteSpace(reason) ? null : Truncate(reason.Trim(), 1000);

        entity.DeletedAt = now;
        db.ArchiveEntries.Add(new ArchiveEntry
        {
            TenantId = user.TenantId!.Value,
            ResourceType = resourceType,
            ResourceId = resourceId,
            // Captured now: once archived, the row is hidden and the Museum is scoped on this.
            CompanyId = entity switch
            {
                Asset a => a.CompanyId,
                Document d => d.CompanyId,
                Runbook r => r.CompanyId,
                KeeperLink k => k.CompanyId,
                _ => null,
            },
            ResourceLabel = Truncate(string.IsNullOrWhiteSpace(label) ? resourceType : label, 450),
            Reason = trimmedReason,
            ArchivedAt = now,
            ArchivedByObjectId = user.ObjectId,
            ArchivedByName = user.DisplayName ?? user.Email,
        });
        await db.SaveChangesAsync(cancellationToken);

        if (audit is not null)
        {
            await audit.LogAsync(
                new AuditEntry(
                    $"{resourceType}.Archive",
                    resourceType,
                    resourceId,
                    trimmedReason is null ? "Archived to the Museum" : $"Archived to the Museum: {trimmedReason}",
                    Category: AuditCategories.Archive,
                    TargetLabel: label),
                cancellationToken);
        }
    }

    public static async Task<IResult> ListAsync(
        [FromQuery] string? resourceType,
        [FromQuery] string? state,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        var query = db.ArchiveEntries.ForTenant(user).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(resourceType))
            query = query.Where(e => e.ResourceType == resourceType);

        query = (state ?? StateArchived).ToLowerInvariant() switch
        {
            StateRestored => query.Where(e => e.RestoredAt != null),
            StateDeleted => query.Where(e => e.PermanentlyDeletedAt != null),
            StateAll => query,
            _ => query.Where(e => e.RestoredAt == null && e.PermanentlyDeletedAt == null),
        };

        var total = await query.CountAsync(cancellationToken);
        var currentPage = Math.Max(page ?? 1, 1);
        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var rows = await query
            .OrderByDescending(e => e.ArchivedAt)
            .ThenByDescending(e => e.Id)
            .Skip((currentPage - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken);

        return Results.Ok(new ArchivePage(total, currentPage, size, rows.Select(Map).ToList()));
    }

    public static async Task<IResult> GetAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        var entry = await db.ArchiveEntries.ForTenant(user).AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        return entry is null ? Results.NotFound() : Results.Ok(Map(entry));
    }

    public static async Task<IResult> RestoreAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IResourceAuthorizationService authorization,
        IAuditService? audit = null,
        ISearchService? search = null,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        var entry = await db.ArchiveEntries.ForTenant(user).FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entry is null)
            return Results.NotFound();

        // Restoring is the inverse of archiving, so it takes the same per-resource write gate.
        if (await ResourceWriteGuard.RequireWriteAsync(authorization, user, entry.ResourceId, entry.ResourceType, cancellationToken, CompanyAccessLevel.Manage) is { } denied)
            return denied;

        if (entry.PermanentlyDeletedAt is not null)
            return Results.Conflict(new { error = "This item was permanently deleted and cannot be restored." });
        if (entry.RestoredAt is not null)
            return Results.Conflict(new { error = "This item was already restored." });

        var entity = await FindAsync(db, user, entry.ResourceType, entry.ResourceId, cancellationToken);
        if (entity is null)
            return Results.Conflict(new { error = "The archived item no longer exists." });
        if (entity.DeletedAt is null)
            return Results.Conflict(new { error = "This item is not archived." });

        // A live row may have taken the slug while this one sat in the Museum. The unique index
        // only covers live rows, so restoring as-is would collide: suffix instead of refusing. The
        // check spans every company, because the index does.
        string? slugNote = null;
        switch (entity)
        {
            case Document { Slug: { Length: > 0 } original } doc:
            {
                doc.Slug = await FreeSlugAsync(original, s => db.Documents.IgnoreQueryFilters([DocuEngAIneDbContext.CompanyScopeFilter]).ForTenant(user).AnyAsync(d => d.Slug == s, cancellationToken));
                if (doc.Slug != original)
                    slugNote = $"slug changed from '{original}' to '{doc.Slug}' (taken while archived)";
                break;
            }
            case Runbook { Slug: { Length: > 0 } original } runbook:
            {
                runbook.Slug = await FreeSlugAsync(original, s => db.Runbooks.IgnoreQueryFilters([DocuEngAIneDbContext.CompanyScopeFilter]).ForTenant(user).AnyAsync(r => r.Slug == s, cancellationToken));
                if (runbook.Slug != original)
                    slugNote = $"slug changed from '{original}' to '{runbook.Slug}' (taken while archived)";
                break;
            }
        }

        entity.DeletedAt = null;
        entry.RestoredAt = DateTimeOffset.UtcNow;
        entry.RestoredByObjectId = user.ObjectId;
        await db.SaveChangesAsync(cancellationToken);

        if (entity is Document restored)
            await DocumentEndpoints.IndexDocumentAsync(search, restored, cancellationToken);

        if (audit is not null)
        {
            await audit.LogAsync(
                new AuditEntry(
                    $"{entry.ResourceType}.Restore",
                    entry.ResourceType,
                    entry.ResourceId,
                    slugNote ?? "Restored from the Museum",
                    Category: AuditCategories.Archive,
                    TargetLabel: entry.ResourceLabel),
                cancellationToken);
        }

        return Results.Ok(Map(entry));
    }

    public static async Task<IResult> PermanentDeleteAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        var entry = await db.ArchiveEntries.ForTenant(user).FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entry is null)
            return Results.NotFound();
        if (entry.PermanentlyDeletedAt is not null)
            return Results.Conflict(new { error = "This item was already permanently deleted." });
        if (entry.RestoredAt is not null)
            return Results.Conflict(new { error = "This item was restored. Archive it again before deleting it permanently." });

        var entity = await FindAsync(db, user, entry.ResourceType, entry.ResourceId, cancellationToken);
        if (entity is not null)
        {
            if (entity.DeletedAt is null)
                return Results.Conflict(new { error = "Only archived items can be permanently deleted." });

            await RemoveDependentsAsync(db, user, entry.ResourceType, entry.ResourceId, cancellationToken);
            db.Remove((object)entity);
        }

        // The entry stays: a tombstone of what existed, who archived it, and who destroyed it.
        entry.PermanentlyDeletedAt = DateTimeOffset.UtcNow;
        entry.PermanentlyDeletedByObjectId = user.ObjectId;
        await db.SaveChangesAsync(cancellationToken);

        if (audit is not null)
        {
            await audit.LogAsync(
                new AuditEntry(
                    $"{entry.ResourceType}.PermanentDelete",
                    entry.ResourceType,
                    entry.ResourceId,
                    "Permanently deleted from the Museum",
                    Category: AuditCategories.Archive,
                    TargetLabel: entry.ResourceLabel),
                cancellationToken);
        }

        return Results.NoContent();
    }

    /// <summary>
    /// Archived or live — the Museum reads past the soft-delete filter on purpose, and only that
    /// filter: company scoping still applies.
    /// </summary>
    private static async Task<ISoftDeletable?> FindAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        string resourceType,
        Guid resourceId,
        CancellationToken cancellationToken) => resourceType switch
        {
            ResourceType.Asset => (ISoftDeletable?)await db.Assets.IgnoreQueryFilters([ModelBuilderExtensions.SoftDeleteFilter]).ForTenant(user)
                .FirstOrDefaultAsync(x => x.Id == resourceId, cancellationToken),
            ResourceType.Document => (ISoftDeletable?)await db.Documents.IgnoreQueryFilters([ModelBuilderExtensions.SoftDeleteFilter]).ForTenant(user)
                .FirstOrDefaultAsync(x => x.Id == resourceId, cancellationToken),
            ResourceType.Runbook => (ISoftDeletable?)await db.Runbooks.IgnoreQueryFilters([ModelBuilderExtensions.SoftDeleteFilter]).ForTenant(user)
                .FirstOrDefaultAsync(x => x.Id == resourceId, cancellationToken),
            ResourceType.KeeperLink => (ISoftDeletable?)await db.KeeperLinks.IgnoreQueryFilters([ModelBuilderExtensions.SoftDeleteFilter]).ForTenant(user)
                .FirstOrDefaultAsync(x => x.Id == resourceId, cancellationToken),
            _ => null,
        };

    /// <summary>
    /// Removes what the database would otherwise orphan or refuse: child rows (explicitly, so the
    /// Restrict document-link FK cannot block the delete) and the polymorphic flags, links and
    /// grants that name the resource by type + id with no FK at all.
    /// </summary>
    private static async Task RemoveDependentsAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        string resourceType,
        Guid resourceId,
        CancellationToken cancellationToken)
    {
        switch (resourceType)
        {
            case ResourceType.Asset:
                db.CustomFieldValues.RemoveRange(await db.CustomFieldValues.IgnoreQueryFilters()
                    .Where(v => v.AssetId == resourceId).ToListAsync(cancellationToken));
                db.AssetDocumentLinks.RemoveRange(await db.AssetDocumentLinks.IgnoreQueryFilters()
                    .Where(l => l.AssetId == resourceId).ToListAsync(cancellationToken));
                break;
            case ResourceType.Document:
                db.AssetDocumentLinks.RemoveRange(await db.AssetDocumentLinks.IgnoreQueryFilters()
                    .Where(l => l.DocumentId == resourceId).ToListAsync(cancellationToken));
                db.DocumentVersions.RemoveRange(await db.DocumentVersions.IgnoreQueryFilters()
                    .Where(v => v.DocumentId == resourceId).ToListAsync(cancellationToken));
                break;
            case ResourceType.Runbook:
                db.RunbookSteps.RemoveRange(await db.RunbookSteps.IgnoreQueryFilters()
                    .Where(s => s.RunbookId == resourceId).ToListAsync(cancellationToken));
                db.RunbookRuns.RemoveRange(await db.RunbookRuns.IgnoreQueryFilters().ForTenant(user)
                    .Where(r => r.RunbookId == resourceId).ToListAsync(cancellationToken));
                break;
        }

        db.FlagAssignments.RemoveRange(await db.FlagAssignments.ForTenant(user)
            .Where(a => a.EntityType == resourceType && a.EntityId == resourceId).ToListAsync(cancellationToken));
        db.ResourceLinks.RemoveRange(await db.ResourceLinks.ForTenant(user)
            .Where(l => (l.FromType == resourceType && l.FromId == resourceId)
                || (l.ToType == resourceType && l.ToId == resourceId)).ToListAsync(cancellationToken));
        db.ResourceRoleAssignments.RemoveRange(await db.ResourceRoleAssignments.ForTenant(user)
            .Where(g => g.ResourceType == resourceType && g.ResourceId == resourceId).ToListAsync(cancellationToken));
    }

    private static async Task<string> FreeSlugAsync(string slug, Func<string, Task<bool>> taken)
    {
        if (!await taken(slug))
            return slug;

        var candidate = $"{slug}-restored";
        var i = 2;
        while (await taken(candidate))
        {
            candidate = $"{slug}-restored-{i}";
            i++;
        }

        return candidate;
    }

    private static ArchiveEntryItem Map(ArchiveEntry e) => new(
        e.Id,
        e.ResourceType,
        e.ResourceId,
        e.ResourceLabel,
        e.Reason,
        e.PermanentlyDeletedAt is not null ? StateDeleted : e.RestoredAt is not null ? StateRestored : StateArchived,
        e.ArchivedAt,
        e.ArchivedByName,
        e.ArchivedByObjectId,
        e.RestoredAt,
        e.PermanentlyDeletedAt);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
