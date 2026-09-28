using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

public class MuseumTests
{
    private sealed class RecordingAudit : IAuditService
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task LogAsync(string action, string entityType, Guid? entityId = null, string? details = null, CancellationToken cancellationToken = default)
        {
            Entries.Add(new AuditEntry(action, entityType, entityId, details));
            return Task.CompletedTask;
        }

        public Task LogAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private static (DocuEngAIneDbContext Db, FakeCurrentUser User, ResourceAuthorizationService Auth) Open(
        string dbName, Guid tenantId, UserRole role = UserRole.Owner)
    {
        var user = new FakeCurrentUser
        {
            TenantId = tenantId,
            ObjectId = Guid.NewGuid().ToString(),
            DisplayName = "Tech One",
            Role = role,
        };
        var db = new DocuEngAIneDbContext(
            new DbContextOptionsBuilder<DocuEngAIneDbContext>().UseInMemoryDatabase(dbName).Options, user);
        return (db, user, new ResourceAuthorizationService(db, user));
    }

    private static T ValueOf<T>(IResult result)
    {
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result);
        return Assert.IsType<T>(value.Value);
    }

    private static int StatusOf(IResult result)
        => result is IStatusCodeHttpResult s && s.StatusCode is int code ? code : 0;

    private static async Task<AssetType> AddAssetTypeAsync(DocuEngAIneDbContext db, Guid tenantId)
    {
        var type = new AssetType { TenantId = tenantId, Name = "Servers" };
        db.AssetTypes.Add(type);
        await db.SaveChangesAsync();
        return type;
    }

    [Fact]
    public async Task Delete_Archives_Every_Resource_Type_Hides_It_And_Registers_An_Entry()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var (db, user, auth) = Open(dbName, tenantId);
        await using (db)
        {
            var type = await AddAssetTypeAsync(db, tenantId);
            var asset = new Asset { TenantId = tenantId, Name = "SRV-01", AssetTypeId = type.Id };
            var doc = new Document { TenantId = tenantId, Title = "Onboarding", Slug = "onboarding" };
            var runbook = new Runbook { TenantId = tenantId, Title = "Offboarding SOP", Slug = "offboarding" };
            var link = new KeeperLink { TenantId = tenantId, Name = "Domain admin" };
            db.AddRange(asset, doc, runbook, link);
            await db.SaveChangesAsync();

            var audit = new RecordingAudit();
            Assert.Equal(StatusCodes.Status204NoContent, StatusOf(await AssetEndpoints.DeleteAsync(asset.Id, db, user, auth, audit, "decommissioned")));
            Assert.Equal(StatusCodes.Status204NoContent, StatusOf(await DocumentEndpoints.DeleteAsync(doc.Id, db, user, auth, null, audit)));
            Assert.Equal(StatusCodes.Status204NoContent, StatusOf(await RunbookEndpoints.DeleteAsync(runbook.Id, db, user, auth, audit)));
            Assert.Equal(StatusCodes.Status204NoContent, StatusOf(await KeeperLinkEndpoints.DeleteAsync(link.Id, db, user, auth, audit)));

            db.ChangeTracker.Clear();
            // Hidden from every normal query…
            Assert.Empty(await db.Assets.ToListAsync());
            Assert.Empty(await db.Documents.ToListAsync());
            Assert.Empty(await db.Runbooks.ToListAsync());
            Assert.Empty(await db.KeeperLinks.ToListAsync());
            // …but not destroyed.
            Assert.NotNull((await db.Assets.IgnoreQueryFilters().SingleAsync(a => a.Id == asset.Id)).DeletedAt);

            var entries = await db.ArchiveEntries.AsNoTracking().ToListAsync();
            Assert.Equal(4, entries.Count);
            var assetEntry = Assert.Single(entries, e => e.ResourceType == ResourceType.Asset);
            Assert.Equal("SRV-01", assetEntry.ResourceLabel);
            Assert.Equal("decommissioned", assetEntry.Reason);
            Assert.Equal("Tech One", assetEntry.ArchivedByName);

            Assert.Contains(audit.Entries, e => e.Action == "Asset.Archive" && e.Category == AuditCategories.Archive && e.TargetLabel == "SRV-01");
            Assert.Contains(audit.Entries, e => e.Action == "Document.Archive");
            Assert.Contains(audit.Entries, e => e.Action == "Runbook.Archive");
            Assert.Contains(audit.Entries, e => e.Action == "KeeperLink.Archive");
        }
    }

    [Fact]
    public async Task List_Shows_Archived_Entries_By_State_And_Never_Another_Tenants()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var (dbB, userB, authB) = Open(dbName, tenantB);
        await using (dbB)
        {
            var poison = new KeeperLink { TenantId = tenantB, Name = "Poison vault" };
            dbB.KeeperLinks.Add(poison);
            await dbB.SaveChangesAsync();
            await KeeperLinkEndpoints.DeleteAsync(poison.Id, dbB, userB, authB);
        }

        var (db, user, auth) = Open(dbName, tenantA);
        await using (db)
        {
            var link = new KeeperLink { TenantId = tenantA, Name = "Own vault" };
            db.KeeperLinks.Add(link);
            await db.SaveChangesAsync();
            await KeeperLinkEndpoints.DeleteAsync(link.Id, db, user, auth);

            var page = ValueOf<ArchiveEndpoints.ArchivePage>(
                await ArchiveEndpoints.ListAsync(null, null, null, null, db, user));
            var item = Assert.Single(page.Items);
            Assert.Equal("Own vault", item.ResourceLabel);
            Assert.Equal(ArchiveEndpoints.StateArchived, item.State);

            var restoredOnly = ValueOf<ArchiveEndpoints.ArchivePage>(
                await ArchiveEndpoints.ListAsync(null, ArchiveEndpoints.StateRestored, null, null, db, user));
            Assert.Empty(restoredOnly.Items);

            // A foreign entry id is not found, never a cross-tenant row.
            var foreignId = (await db.ArchiveEntries.AsNoTracking().SingleAsync(e => e.TenantId == tenantB)).Id;
            Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await ArchiveEndpoints.GetAsync(foreignId, db, user)));
            Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await ArchiveEndpoints.RestoreAsync(foreignId, db, user, auth)));
        }
    }

    [Fact]
    public async Task Restore_Brings_The_Row_Back_And_Marks_The_Entry()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var (db, user, auth) = Open(dbName, tenantId);
        await using (db)
        {
            var runbook = new Runbook { TenantId = tenantId, Title = "Patch night", Slug = "patch-night" };
            db.Runbooks.Add(runbook);
            db.RunbookSteps.Add(new RunbookStep { RunbookId = runbook.Id, Order = 1, Title = "Snapshot VMs" });
            await db.SaveChangesAsync();
            await RunbookEndpoints.DeleteAsync(runbook.Id, db, user, auth);

            // Steps vanish with their archived runbook.
            db.ChangeTracker.Clear();
            Assert.Empty(await db.RunbookSteps.ToListAsync());

            var entry = await db.ArchiveEntries.AsNoTracking().SingleAsync();
            var audit = new RecordingAudit();
            var result = await ArchiveEndpoints.RestoreAsync(entry.Id, db, user, auth, audit);
            Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
            Assert.Equal(ArchiveEndpoints.StateRestored, ValueOf<ArchiveEndpoints.ArchiveEntryItem>(result).State);

            db.ChangeTracker.Clear();
            Assert.Equal("patch-night", (await db.Runbooks.SingleAsync()).Slug);
            Assert.Single(await db.RunbookSteps.ToListAsync());
            Assert.NotNull((await db.ArchiveEntries.AsNoTracking().SingleAsync()).RestoredAt);
            Assert.Contains(audit.Entries, e => e.Action == "Runbook.Restore");

            // Restoring twice is a conflict, not a silent no-op.
            Assert.Equal(StatusCodes.Status409Conflict, StatusOf(await ArchiveEndpoints.RestoreAsync(entry.Id, db, user, auth)));
        }
    }

    [Fact]
    public async Task Restore_Suffixes_A_Slug_A_Live_Document_Took_While_Archived()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var (db, user, auth) = Open(dbName, tenantId);
        await using (db)
        {
            var original = new Document { TenantId = tenantId, Title = "Onboarding v1", Slug = "onboarding" };
            db.Documents.Add(original);
            await db.SaveChangesAsync();
            await DocumentEndpoints.DeleteAsync(original.Id, db, user, auth);

            db.Documents.Add(new Document { TenantId = tenantId, Title = "Onboarding v2", Slug = "onboarding" });
            await db.SaveChangesAsync();

            var entry = await db.ArchiveEntries.AsNoTracking().SingleAsync();
            var audit = new RecordingAudit();
            Assert.Equal(StatusCodes.Status200OK, StatusOf(await ArchiveEndpoints.RestoreAsync(entry.Id, db, user, auth, audit)));

            db.ChangeTracker.Clear();
            var slugs = (await db.Documents.Select(d => d.Slug ?? "").ToListAsync()).Order().ToArray();
            Assert.Equal(new[] { "onboarding", "onboarding-restored" }, slugs);
            Assert.Contains(audit.Entries, e => e.Action == "Document.Restore" && e.Details!.Contains("onboarding-restored"));
        }
    }

    [Fact]
    public async Task Permanent_Delete_Destroys_The_Row_Cleans_Polymorphic_Rows_And_Keeps_A_Tombstone()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var (db, user, auth) = Open(dbName, tenantId);
        await using (db)
        {
            var type = await AddAssetTypeAsync(db, tenantId);
            var asset = new Asset { TenantId = tenantId, Name = "SRV-02", AssetTypeId = type.Id };
            var doc = new Document { TenantId = tenantId, Title = "Runbook doc", Slug = "doc" };
            var flag = new FlagDefinition { TenantId = tenantId, Name = "Review", Color = "#f00" };
            db.AddRange(asset, doc, flag);
            await db.SaveChangesAsync();
            db.AssetDocumentLinks.Add(new AssetDocumentLink { AssetId = asset.Id, DocumentId = doc.Id });
            db.FlagAssignments.Add(new FlagAssignment
            {
                TenantId = tenantId,
                FlagDefinitionId = flag.Id,
                EntityType = FlagEntityType.Document,
                EntityId = doc.Id,
            });
            db.ResourceLinks.Add(new ResourceLink
            {
                TenantId = tenantId,
                FromType = LinkEntityType.Asset,
                FromId = asset.Id,
                ToType = LinkEntityType.Document,
                ToId = doc.Id,
            });
            await db.SaveChangesAsync();

            await DocumentEndpoints.DeleteAsync(doc.Id, db, user, auth);
            var entry = await db.ArchiveEntries.AsNoTracking().SingleAsync();

            var audit = new RecordingAudit();
            Assert.Equal(StatusCodes.Status204NoContent, StatusOf(await ArchiveEndpoints.PermanentDeleteAsync(entry.Id, db, user, audit)));

            db.ChangeTracker.Clear();
            Assert.False(await db.Documents.IgnoreQueryFilters().AnyAsync(d => d.Id == doc.Id));
            Assert.False(await db.AssetDocumentLinks.IgnoreQueryFilters().AnyAsync(l => l.DocumentId == doc.Id));
            Assert.Empty(await db.FlagAssignments.ToListAsync());
            Assert.Empty(await db.ResourceLinks.ToListAsync());
            // The asset on the other end of the link is untouched.
            Assert.True(await db.Assets.AnyAsync(a => a.Id == asset.Id));

            var tombstone = await db.ArchiveEntries.AsNoTracking().SingleAsync();
            Assert.NotNull(tombstone.PermanentlyDeletedAt);
            Assert.Equal(user.ObjectId, tombstone.PermanentlyDeletedByObjectId);
            Assert.Contains(audit.Entries, e => e.Action == "Document.PermanentDelete");

            // Tombstones cannot be restored or deleted again.
            Assert.Equal(StatusCodes.Status409Conflict, StatusOf(await ArchiveEndpoints.RestoreAsync(entry.Id, db, user, auth)));
            Assert.Equal(StatusCodes.Status409Conflict, StatusOf(await ArchiveEndpoints.PermanentDeleteAsync(entry.Id, db, user)));
        }
    }

    [Fact]
    public async Task Permanent_Delete_Refuses_A_Restored_Entry()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var (db, user, auth) = Open(dbName, tenantId);
        await using (db)
        {
            var link = new KeeperLink { TenantId = tenantId, Name = "Firewall admin" };
            db.KeeperLinks.Add(link);
            await db.SaveChangesAsync();
            await KeeperLinkEndpoints.DeleteAsync(link.Id, db, user, auth);
            var entry = await db.ArchiveEntries.AsNoTracking().SingleAsync();
            await ArchiveEndpoints.RestoreAsync(entry.Id, db, user, auth);

            Assert.Equal(StatusCodes.Status409Conflict, StatusOf(await ArchiveEndpoints.PermanentDeleteAsync(entry.Id, db, user)));
            db.ChangeTracker.Clear();
            Assert.True(await db.KeeperLinks.AnyAsync(k => k.Id == link.Id));
        }
    }

    [Fact]
    public async Task Reader_Without_Grant_Cannot_Restore()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        Guid entryId;
        var (owner, ownerUser, ownerAuth) = Open(dbName, tenantId);
        await using (owner)
        {
            var link = new KeeperLink { TenantId = tenantId, Name = "Backup console" };
            owner.KeeperLinks.Add(link);
            await owner.SaveChangesAsync();
            await KeeperLinkEndpoints.DeleteAsync(link.Id, owner, ownerUser, ownerAuth);
            entryId = (await owner.ArchiveEntries.AsNoTracking().SingleAsync()).Id;
        }

        var (db, reader, auth) = Open(dbName, tenantId, UserRole.Reader);
        await using (db)
        {
            Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await ArchiveEndpoints.RestoreAsync(entryId, db, reader, auth)));
            Assert.Null((await db.ArchiveEntries.AsNoTracking().SingleAsync()).RestoredAt);
        }
    }

    [Fact]
    public async Task Archived_Asset_Custom_Fields_Leave_The_Expiration_Rollup()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var (db, user, auth) = Open(dbName, tenantId);
        await using (db)
        {
            var type = await AddAssetTypeAsync(db, tenantId);
            var field = new FieldDefinition { AssetTypeId = type.Id, Name = "Warranty", FieldType = "Date", IsExpiration = true };
            db.FieldDefinitions.Add(field);
            var asset = new Asset
            {
                TenantId = tenantId,
                Name = "SRV-03",
                AssetTypeId = type.Id,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(10),
            };
            db.Assets.Add(asset);
            await db.SaveChangesAsync();
            db.CustomFieldValues.Add(new CustomFieldValue
            {
                AssetId = asset.Id,
                FieldDefinitionId = field.Id,
                Value = DateTimeOffset.UtcNow.AddDays(20).ToString("O"),
            });
            await db.SaveChangesAsync();

            Assert.NotEmpty(await ExpirationEndpoints.QueryAsync(db, user));

            await AssetEndpoints.DeleteAsync(asset.Id, db, user, auth);
            db.ChangeTracker.Clear();

            Assert.Empty(await db.CustomFieldValues.ToListAsync());
            Assert.Empty(await ExpirationEndpoints.QueryAsync(db, user));
        }
    }
}
