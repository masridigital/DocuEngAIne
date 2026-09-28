using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

/// <summary>
/// The Museum: DELETE on a company / asset / document / runbook / Keeper link archives it (soft
/// delete + <see cref="ArchiveEntry"/>) instead of destroying it. Archived rows are hidden by the
/// global query filter everywhere; this surface lists them, restores them, and — Admin only —
/// destroys them for good, leaving the entry as a tombstone. A company takes what it owns with it:
/// each item gets an entry under the company's (<see cref="ArchiveEntry.ParentEntryId"/>), listed
/// with <c>?parentId=</c>, and comes back or goes for good with the company.
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
        DateTimeOffset? PermanentlyDeletedAt,
        Guid? ParentEntryId = null,
        int Items = 0);

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

    /// <summary>
    /// Archives a company with everything it owns: its assets, documents, runbooks and Keeper links
    /// each get an entry under the company's, so they come back (or go for good) with it. Its
    /// folders and runs are hidden while it is archived. Callers have run the company's write gate
    /// and checked it has no live sub-companies.
    /// </summary>
    /// <returns>The company's entry and how many items went with it.</returns>
    public static async Task<(ArchiveEntry Entry, int Items)> ArchiveCompanyAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IAuditService? audit,
        ISearchService? search,
        Company company,
        string? reason,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var tenantId = user.TenantId!.Value;
        var trimmedReason = string.IsNullOrWhiteSpace(reason) ? null : Truncate(reason.Trim(), 1000);
        var companyEntry = new ArchiveEntry
        {
            TenantId = tenantId,
            ResourceType = ResourceType.Company,
            ResourceId = company.Id,
            CompanyId = company.Id,
            ResourceLabel = Truncate(company.Name, 450),
            Reason = trimmedReason,
            ArchivedAt = now,
            ArchivedByObjectId = user.ObjectId,
            ArchivedByName = user.DisplayName ?? user.Email,
        };
        db.ArchiveEntries.Add(companyEntry);
        company.DeletedAt = now;

        var items = 0;
        void Archive(ISoftDeletable entity, string resourceType, Guid resourceId, string label)
        {
            entity.DeletedAt = now;
            db.ArchiveEntries.Add(new ArchiveEntry
            {
                TenantId = tenantId,
                ResourceType = resourceType,
                ResourceId = resourceId,
                CompanyId = company.Id,
                ParentEntryId = companyEntry.Id,
                ResourceLabel = Truncate(string.IsNullOrWhiteSpace(label) ? resourceType : label, 450),
                Reason = trimmedReason,
                ArchivedAt = now,
                ArchivedByObjectId = user.ObjectId,
                ArchivedByName = user.DisplayName ?? user.Email,
            });
            items++;
        }

        // Live records only: anything already in the Museum keeps its own entry.
        foreach (var asset in await db.Assets.ForTenant(user).Where(a => a.CompanyId == company.Id).ToListAsync(cancellationToken))
            Archive(asset, ResourceType.Asset, asset.Id, asset.Name);
        var documents = await db.Documents.ForTenant(user).Where(d => d.CompanyId == company.Id).ToListAsync(cancellationToken);
        foreach (var document in documents)
            Archive(document, ResourceType.Document, document.Id, document.Title);
        foreach (var runbook in await db.Runbooks.ForTenant(user).Where(r => r.CompanyId == company.Id).ToListAsync(cancellationToken))
            Archive(runbook, ResourceType.Runbook, runbook.Id, runbook.Title);
        foreach (var link in await db.KeeperLinks.ForTenant(user).Where(k => k.CompanyId == company.Id).ToListAsync(cancellationToken))
            Archive(link, ResourceType.KeeperLink, link.Id, link.Name);

        await db.SaveChangesAsync(cancellationToken);

        if (search is not null)
        {
            foreach (var document in documents)
                await search.RemoveDocumentAsync(document.Id, document.TenantId, cancellationToken);
        }

        if (audit is not null)
        {
            var details = $"Archived to the Museum with {items} item(s)";
            await audit.LogAsync(
                new AuditEntry(
                    "Company.Archive",
                    ResourceType.Company,
                    company.Id,
                    trimmedReason is null ? details : $"{details}: {trimmedReason}",
                    Category: AuditCategories.Archive,
                    TargetLabel: company.Name),
                cancellationToken);
        }

        return (companyEntry, items);
    }

    public static async Task<IResult> ListAsync(
        [FromQuery] string? resourceType,
        [FromQuery] string? state,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default,
        [FromQuery] Guid? parentId = null)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        // Items archived with a company are listed under it (?parentId=), not beside it.
        var query = db.ArchiveEntries.ForTenant(user).AsNoTracking()
            .Where(e => e.ParentEntryId == parentId);
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

        var parentIds = rows.Where(r => r.ResourceType == ResourceType.Company).Select(r => r.Id).ToList();
        var itemCounts = parentIds.Count == 0
            ? new Dictionary<Guid, int>()
            : await db.ArchiveEntries.ForTenant(user).AsNoTracking()
                .Where(e => e.ParentEntryId != null && parentIds.Contains(e.ParentEntryId.Value))
                .GroupBy(e => e.ParentEntryId!.Value)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);

        return Results.Ok(new ArchivePage(
            total,
            currentPage,
            size,
            rows.Select(r => Map(r, itemCounts.GetValueOrDefault(r.Id))).ToList()));
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
        if (entry is null)
            return Results.NotFound();

        var items = entry.ResourceType == ResourceType.Company
            ? await db.ArchiveEntries.ForTenant(user).CountAsync(e => e.ParentEntryId == entry.Id, cancellationToken)
            : 0;
        return Results.Ok(Map(entry, items));
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

        // A company comes back with everything that was archived with it.
        if (entity is Company archivedCompany)
            return await RestoreCompanyAsync(entry, archivedCompany, db, user, audit, search, cancellationToken);

        // Nothing is restored into a company that is still in the Museum.
        if (await CompanyArchivedAsync(db, user, CompanyOf(entity), cancellationToken))
        {
            return Results.Conflict(new
            {
                error = entry.ParentEntryId is null
                    ? "Its company is in the Museum. Restore the company first."
                    : "This item was archived with its company. Restore the company to bring it back.",
            });
        }

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
        if (entity is Company archivedCompany)
        {
            if (archivedCompany.DeletedAt is null)
                return Results.Conflict(new { error = "Only archived items can be permanently deleted." });
            return await PermanentlyDeleteCompanyAsync(entry, archivedCompany, db, user, audit, cancellationToken);
        }

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
    /// Restores a company and every item archived with it (still archived, not destroyed since),
    /// suffixing any slug taken in the meantime. A company whose parent is still archived waits for it.
    /// </summary>
    private static async Task<IResult> RestoreCompanyAsync(
        ArchiveEntry entry,
        Company company,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IAuditService? audit,
        ISearchService? search,
        CancellationToken cancellationToken)
    {
        if (await CompanyArchivedAsync(db, user, company.ParentCompanyId, cancellationToken))
            return Results.Conflict(new { error = "Its parent company is in the Museum. Restore the parent first." });

        var now = DateTimeOffset.UtcNow;
        var notes = new List<string>();
        var originalSlug = company.Slug;
        company.Slug = await FreeSlugAsync(originalSlug, s => db.Companies.IgnoreQueryFilters([DocuEngAIneDbContext.CompanyScopeFilter]).ForTenant(user).AnyAsync(c => c.Slug == s, cancellationToken));
        if (company.Slug != originalSlug)
            notes.Add($"slug changed from '{originalSlug}' to '{company.Slug}' (taken while archived)");
        company.DeletedAt = null;
        entry.RestoredAt = now;
        entry.RestoredByObjectId = user.ObjectId;

        var children = await db.ArchiveEntries.ForTenant(user)
            .Where(e => e.ParentEntryId == entry.Id)
            .ToListAsync(cancellationToken);
        var restoredDocuments = new List<Document>();
        var restored = 0;
        foreach (var group in children.Where(c => c.RestoredAt == null && c.PermanentlyDeletedAt == null).GroupBy(c => c.ResourceType))
        {
            var entities = await FindManyAsync(db, user, group.Key, group.Select(c => c.ResourceId).ToList(), cancellationToken);
            foreach (var child in group)
            {
                // Gone some other way: the entry stays open for an Admin to close.
                if (!entities.TryGetValue(child.ResourceId, out var item))
                    continue;
                if (item.DeletedAt is null)
                {
                    child.RestoredAt = now;
                    child.RestoredByObjectId = user.ObjectId;
                    continue;
                }

                switch (item)
                {
                    case Document { Slug: { Length: > 0 } documentSlug } document:
                        document.Slug = await FreeSlugAsync(documentSlug, s => db.Documents.IgnoreQueryFilters([DocuEngAIneDbContext.CompanyScopeFilter]).ForTenant(user).AnyAsync(d => d.Slug == s, cancellationToken));
                        break;
                    case Runbook { Slug: { Length: > 0 } runbookSlug } runbook:
                        runbook.Slug = await FreeSlugAsync(runbookSlug, s => db.Runbooks.IgnoreQueryFilters([DocuEngAIneDbContext.CompanyScopeFilter]).ForTenant(user).AnyAsync(r => r.Slug == s, cancellationToken));
                        break;
                }

                item.DeletedAt = null;
                child.RestoredAt = now;
                child.RestoredByObjectId = user.ObjectId;
                if (item is Document restoredDocument)
                    restoredDocuments.Add(restoredDocument);
                restored++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var document in restoredDocuments)
            await DocumentEndpoints.IndexDocumentAsync(search, document, cancellationToken);

        if (audit is not null)
        {
            var details = $"Restored from the Museum with {restored} item(s)";
            await audit.LogAsync(
                new AuditEntry(
                    "Company.Restore",
                    ResourceType.Company,
                    company.Id,
                    notes.Count == 0 ? details : $"{details}; {string.Join("; ", notes)}",
                    Category: AuditCategories.Archive,
                    TargetLabel: company.Name),
                cancellationToken);
        }

        return Results.Ok(Map(entry, children.Count));
    }

    /// <summary>
    /// Destroys an archived company for good with every record it still has — all of them archived,
    /// since nothing can be added to or restored into it — and what hangs off it: folders, runs,
    /// layout activations, security-group grants, sync mappings, flags and links. Refused while
    /// another company names it as parent. Every entry involved stays as a tombstone.
    /// </summary>
    private static async Task<IResult> PermanentlyDeleteCompanyAsync(
        ArchiveEntry entry,
        Company company,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IAuditService? audit,
        CancellationToken cancellationToken)
    {
        // Past every filter, whoever can see the rows: anything left pointing at the company keeps
        // a foreign key and blocks the delete.
        if (await db.Companies.IgnoreQueryFilters().ForTenant(user).AnyAsync(c => c.ParentCompanyId == company.Id, cancellationToken))
            return Results.Conflict(new { error = "Other companies still name this one as their parent. Move or delete them first." });

        // Folders first, flattened, so their self-reference cannot block the delete. A folder from
        // elsewhere filed under one of them moves up to the nearest folder that stays; a document
        // from elsewhere filed in one moves to the top level, as when a folder is deleted.
        var folders = await db.DocumentFolders.IgnoreQueryFilters().ForTenant(user)
            .Where(f => f.CompanyId == company.Id).ToListAsync(cancellationToken);
        var parentOf = folders.ToDictionary(f => f.Id, f => f.ParentId);
        var folderIds = parentOf.Keys.ToList();
        foreach (var child in await db.DocumentFolders.IgnoreQueryFilters().ForTenant(user)
            .Where(f => f.ParentId != null && folderIds.Contains(f.ParentId.Value) && f.CompanyId != company.Id)
            .ToListAsync(cancellationToken))
        {
            child.ParentId = SurvivingAncestor(child.ParentId, parentOf);
        }

        foreach (var folder in folders)
            folder.ParentId = null;
        foreach (var stray in await db.Documents.IgnoreQueryFilters().ForTenant(user)
            .Where(d => d.FolderId != null && folderIds.Contains(d.FolderId.Value) && d.CompanyId != company.Id)
            .ToListAsync(cancellationToken))
        {
            stray.FolderId = null;
        }

        await db.SaveChangesAsync(cancellationToken);

        var assets = await db.Assets.IgnoreQueryFilters().ForTenant(user).Where(x => x.CompanyId == company.Id).ToListAsync(cancellationToken);
        var documents = await db.Documents.IgnoreQueryFilters().ForTenant(user).Where(x => x.CompanyId == company.Id).ToListAsync(cancellationToken);
        var runbooks = await db.Runbooks.IgnoreQueryFilters().ForTenant(user).Where(x => x.CompanyId == company.Id).ToListAsync(cancellationToken);
        var links = await db.KeeperLinks.IgnoreQueryFilters().ForTenant(user).Where(x => x.CompanyId == company.Id).ToListAsync(cancellationToken);

        var records = new List<(ISoftDeletable Entity, string Type, Guid Id)>();
        records.AddRange(assets.Select(x => ((ISoftDeletable)x, ResourceType.Asset, x.Id)));
        records.AddRange(documents.Select(x => ((ISoftDeletable)x, ResourceType.Document, x.Id)));
        records.AddRange(runbooks.Select(x => ((ISoftDeletable)x, ResourceType.Runbook, x.Id)));
        records.AddRange(links.Select(x => ((ISoftDeletable)x, ResourceType.KeeperLink, x.Id)));
        foreach (var (owned, ownedType, ownedId) in records)
        {
            await RemoveDependentsAsync(db, user, ownedType, ownedId, cancellationToken);
            db.Remove((object)owned);
        }

        db.DocumentFolders.RemoveRange(folders);
        db.RunbookRuns.RemoveRange(await db.RunbookRuns.IgnoreQueryFilters().ForTenant(user)
            .Where(r => r.CompanyId == company.Id).ToListAsync(cancellationToken));
        db.AssetTypeCompanyActivations.RemoveRange(await db.AssetTypeCompanyActivations.IgnoreQueryFilters().ForTenant(user)
            .Where(a => a.CompanyId == company.Id).ToListAsync(cancellationToken));
        db.SecurityGroupCompanyGrants.RemoveRange(await db.SecurityGroupCompanyGrants.IgnoreQueryFilters().ForTenant(user)
            .Where(g => g.CompanyId == company.Id).ToListAsync(cancellationToken));
        db.IntegrationMappings.RemoveRange(await db.IntegrationMappings.ForTenant(user)
            .Where(m => m.LocalEntityType == nameof(Company) && m.LocalEntityId == company.Id).ToListAsync(cancellationToken));
        await RemoveDependentsAsync(db, user, ResourceType.Company, company.Id, cancellationToken);
        db.Companies.Remove(company);

        var now = DateTimeOffset.UtcNow;
        var recordIds = records.Select(r => r.Id).ToList();
        foreach (var open in await db.ArchiveEntries.IgnoreQueryFilters().ForTenant(user)
            .Where(e => e.PermanentlyDeletedAt == null && (e.Id == entry.Id || recordIds.Contains(e.ResourceId)))
            .ToListAsync(cancellationToken))
        {
            open.PermanentlyDeletedAt = now;
            open.PermanentlyDeletedByObjectId = user.ObjectId;
        }

        await db.SaveChangesAsync(cancellationToken);

        if (audit is not null)
        {
            await audit.LogAsync(
                new AuditEntry(
                    "Company.PermanentDelete",
                    ResourceType.Company,
                    company.Id,
                    $"Permanently deleted from the Museum with {records.Count} item(s)",
                    Category: AuditCategories.Archive,
                    TargetLabel: entry.ResourceLabel),
                cancellationToken);
        }

        return Results.NoContent();
    }

    /// <summary>
    /// The first folder up the chain from <paramref name="parentId"/> that is not being removed, or
    /// null for the top level.
    /// </summary>
    private static Guid? SurvivingAncestor(Guid? parentId, Dictionary<Guid, Guid?> removed)
    {
        var seen = new HashSet<Guid>();
        while (parentId is Guid id && removed.TryGetValue(id, out var next))
        {
            if (!seen.Add(id))
                return null;
            parentId = next;
        }

        return parentId;
    }

    private static async Task<Dictionary<Guid, ISoftDeletable>> FindManyAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        string resourceType,
        List<Guid> ids,
        CancellationToken cancellationToken)
    {
        string[] softDelete = [ModelBuilderExtensions.SoftDeleteFilter];
        return resourceType switch
        {
            ResourceType.Asset => (await db.Assets.IgnoreQueryFilters(softDelete).ForTenant(user)
                .Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken)).ToDictionary(x => x.Id, x => (ISoftDeletable)x),
            ResourceType.Document => (await db.Documents.IgnoreQueryFilters(softDelete).ForTenant(user)
                .Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken)).ToDictionary(x => x.Id, x => (ISoftDeletable)x),
            ResourceType.Runbook => (await db.Runbooks.IgnoreQueryFilters(softDelete).ForTenant(user)
                .Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken)).ToDictionary(x => x.Id, x => (ISoftDeletable)x),
            ResourceType.KeeperLink => (await db.KeeperLinks.IgnoreQueryFilters(softDelete).ForTenant(user)
                .Where(x => ids.Contains(x.Id)).ToListAsync(cancellationToken)).ToDictionary(x => x.Id, x => (ISoftDeletable)x),
            _ => new Dictionary<Guid, ISoftDeletable>(),
        };
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
            ResourceType.Company => (ISoftDeletable?)await db.Companies.IgnoreQueryFilters([ModelBuilderExtensions.SoftDeleteFilter]).ForTenant(user)
                .FirstOrDefaultAsync(x => x.Id == resourceId, cancellationToken),
            _ => null,
        };

    /// <summary>The company a Museum-capable record belongs to, when it has one.</summary>
    private static Guid? CompanyOf(ISoftDeletable entity) => entity switch
    {
        Asset a => a.CompanyId,
        Document d => d.CompanyId,
        Runbook r => r.CompanyId,
        KeeperLink k => k.CompanyId,
        _ => null,
    };

    private static Task<bool> CompanyArchivedAsync(DocuEngAIneDbContext db, ICurrentUser user, Guid? companyId, CancellationToken cancellationToken)
        => companyId is Guid id
            ? db.Companies.IgnoreQueryFilters([ModelBuilderExtensions.SoftDeleteFilter, DocuEngAIneDbContext.CompanyScopeFilter]).ForTenant(user)
                .AnyAsync(c => c.Id == id && c.DeletedAt != null, cancellationToken)
            : Task.FromResult(false);

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

    private static ArchiveEntryItem Map(ArchiveEntry e, int items = 0) => new(
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
        e.PermanentlyDeletedAt,
        e.ParentEntryId,
        items);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
