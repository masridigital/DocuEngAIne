using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Core.Mcp;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using DocuEngAIne.Infrastructure.Integrations;
using DocuEngAIne.Infrastructure.Integrations.Migration;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

/// <summary>
/// Archiving a company takes everything it owns to the Museum with it, restores and destroys it as
/// one bundle, and keeps sync and imports from re-creating what a technician archived.
/// </summary>
public class CompanyArchiveTests
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

    private sealed class NoopMcp : IMcpClient
    {
        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{}}""");
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

    private static async Task<CompanyEndpoints.CompanyArchived> ArchiveCompanyAsync(
        DocuEngAIneDbContext db, FakeCurrentUser user, ResourceAuthorizationService auth, Guid companyId, IAuditService? audit = null, string? reason = null)
    {
        var result = await CompanyEndpoints.ArchiveAsync(companyId, db, user, auth, audit, null, reason);
        Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
        return ValueOf<CompanyEndpoints.CompanyArchived>(result);
    }

    private static async Task<AssetType> AddAssetTypeAsync(DocuEngAIneDbContext db, Guid tenantId)
    {
        var type = new AssetType { TenantId = tenantId, Name = "Servers" };
        db.AssetTypes.Add(type);
        await db.SaveChangesAsync();
        return type;
    }

    [Fact]
    public async Task Archive_Takes_Everything_The_Company_Owns_Into_The_Museum()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var (db, user, auth) = Open(dbName, tenantId);
        await using (db)
        {
            var type = await AddAssetTypeAsync(db, tenantId);
            var acme = new Company { TenantId = tenantId, Name = "Acme", Slug = "acme" };
            var other = new Company { TenantId = tenantId, Name = "Other", Slug = "other" };
            db.Companies.AddRange(acme, other);
            await db.SaveChangesAsync();

            var folder = new DocumentFolder { TenantId = tenantId, Name = "Acme KB", CompanyId = acme.Id };
            var sharedFolder = new DocumentFolder { TenantId = tenantId, Name = "Central KB" };
            var template = new Runbook { TenantId = tenantId, Title = "Onboarding template", Slug = "onboarding-template" };
            db.AddRange(folder, sharedFolder, template);
            await db.SaveChangesAsync();

            var asset = new Asset { TenantId = tenantId, Name = "SRV-01", AssetTypeId = type.Id, CompanyId = acme.Id };
            var earlier = new Asset { TenantId = tenantId, Name = "SRV-OLD", AssetTypeId = type.Id, CompanyId = acme.Id };
            var otherAsset = new Asset { TenantId = tenantId, Name = "OTHER-01", AssetTypeId = type.Id, CompanyId = other.Id };
            var doc = new Document { TenantId = tenantId, Title = "Acme VPN", Slug = "acme-vpn", CompanyId = acme.Id, FolderId = folder.Id };
            var central = new Document { TenantId = tenantId, Title = "Password policy", Slug = "password-policy", FolderId = sharedFolder.Id };
            var runbook = new Runbook { TenantId = tenantId, Title = "Acme patching", Slug = "acme-patching", CompanyId = acme.Id };
            var link = new KeeperLink { TenantId = tenantId, Name = "Acme domain admin", CompanyId = acme.Id };
            db.AddRange(asset, earlier, otherAsset, doc, central, runbook, link);
            db.RunbookRuns.Add(new RunbookRun { TenantId = tenantId, RunbookId = template.Id, CompanyId = acme.Id });
            db.RunbookRuns.Add(new RunbookRun { TenantId = tenantId, RunbookId = template.Id, CompanyId = other.Id });
            await db.SaveChangesAsync();

            // Archived on its own before the company was: it keeps its own entry.
            await AssetEndpoints.DeleteAsync(earlier.Id, db, user, auth);
            db.ChangeTracker.Clear();

            var audit = new RecordingAudit();
            var archived = await ArchiveCompanyAsync(db, user, auth, acme.Id, audit, "Churned");
            Assert.Equal(4, archived.Items);

            db.ChangeTracker.Clear();
            // Hidden everywhere: the company, what it owns, its folders and its runs.
            Assert.Equal("Other", Assert.Single(await db.Companies.ToListAsync()).Name);
            Assert.Equal("OTHER-01", Assert.Single(await db.Assets.ToListAsync()).Name);
            Assert.Equal("Password policy", Assert.Single(await db.Documents.ToListAsync()).Title);
            Assert.Equal("Onboarding template", Assert.Single(await db.Runbooks.ToListAsync()).Title);
            Assert.Empty(await db.KeeperLinks.ToListAsync());
            Assert.Equal("Central KB", Assert.Single(await db.DocumentFolders.ToListAsync()).Name);
            Assert.Equal(other.Id, Assert.Single(await db.RunbookRuns.ToListAsync()).CompanyId);
            Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await CompanyEndpoints.GetAsync(acme.Id, db, user)));
            // Nothing is destroyed.
            Assert.NotNull((await db.Companies.IgnoreQueryFilters().SingleAsync(c => c.Id == acme.Id)).DeletedAt);
            Assert.Equal(3, await db.Assets.IgnoreQueryFilters().CountAsync());
            Assert.Equal(2, await db.DocumentFolders.IgnoreQueryFilters().CountAsync());

            var entries = await db.ArchiveEntries.AsNoTracking().ToListAsync();
            var companyEntry = Assert.Single(entries, e => e.ResourceType == ResourceType.Company);
            Assert.Equal(archived.ArchiveEntryId, companyEntry.Id);
            Assert.Equal(acme.Id, companyEntry.ResourceId);
            Assert.Equal(acme.Id, companyEntry.CompanyId);
            Assert.Equal("Acme", companyEntry.ResourceLabel);
            Assert.Equal("Churned", companyEntry.Reason);
            Assert.Null(companyEntry.ParentEntryId);
            var children = entries.Where(e => e.ParentEntryId == companyEntry.Id).ToList();
            Assert.Equal(
                new[] { ResourceType.Asset, ResourceType.Document, ResourceType.KeeperLink, ResourceType.Runbook },
                children.Select(e => e.ResourceType).Order().ToArray());
            Assert.All(children, e => Assert.Equal("Churned", e.Reason));
            Assert.All(children, e => Assert.Equal(acme.Id, e.CompanyId));
            Assert.Null(Assert.Single(entries, e => e.ResourceId == earlier.Id).ParentEntryId);

            // The Museum lists the company (with its count) beside the earlier asset, not its contents…
            var top = ValueOf<ArchiveEndpoints.ArchivePage>(await ArchiveEndpoints.ListAsync(null, null, null, null, db, user));
            Assert.Equal(2, top.Total);
            Assert.Equal(4, Assert.Single(top.Items, i => i.ResourceType == ResourceType.Company).Items);
            Assert.Contains(top.Items, i => i.ResourceId == earlier.Id);
            // …which are listed under it.
            var contents = ValueOf<ArchiveEndpoints.ArchivePage>(
                await ArchiveEndpoints.ListAsync(null, null, null, null, db, user, parentId: companyEntry.Id));
            Assert.Equal(4, contents.Total);
            Assert.All(contents.Items, i => Assert.Equal(companyEntry.Id, i.ParentEntryId));
            Assert.Equal(4, ValueOf<ArchiveEndpoints.ArchiveEntryItem>(await ArchiveEndpoints.GetAsync(companyEntry.Id, db, user)).Items);

            Assert.Contains(audit.Entries, e => e.Action == "Company.Archive" && e.Category == AuditCategories.Archive
                && e.TargetLabel == "Acme" && (e.Details ?? "").Contains("4 item(s)") && (e.Details ?? "").Contains("Churned"));
        }
    }

    [Fact]
    public async Task Restore_Brings_The_Company_Back_With_What_Was_Archived_With_It()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var (db, user, auth) = Open(dbName, tenantId);
        await using (db)
        {
            var type = await AddAssetTypeAsync(db, tenantId);
            var acme = new Company { TenantId = tenantId, Name = "Acme", Slug = "acme" };
            db.Companies.Add(acme);
            await db.SaveChangesAsync();
            var folder = new DocumentFolder { TenantId = tenantId, Name = "Acme KB", CompanyId = acme.Id };
            db.DocumentFolders.Add(folder);
            await db.SaveChangesAsync();
            var asset = new Asset { TenantId = tenantId, Name = "SRV-01", AssetTypeId = type.Id, CompanyId = acme.Id };
            var doc = new Document { TenantId = tenantId, Title = "Acme onboarding", Slug = "acme-onboarding", CompanyId = acme.Id, FolderId = folder.Id };
            var notes = new Document { TenantId = tenantId, Title = "Old notes", Slug = "acme-old", CompanyId = acme.Id };
            var runbook = new Runbook { TenantId = tenantId, Title = "Acme patching", Slug = "acme-patching", CompanyId = acme.Id };
            var link = new KeeperLink { TenantId = tenantId, Name = "Acme domain admin", CompanyId = acme.Id };
            db.AddRange(asset, doc, notes, runbook, link);
            db.RunbookRuns.Add(new RunbookRun { TenantId = tenantId, RunbookId = runbook.Id, CompanyId = acme.Id });
            await db.SaveChangesAsync();

            await DocumentEndpoints.DeleteAsync(notes.Id, db, user, auth);
            var notesEntryId = (await db.ArchiveEntries.AsNoTracking().SingleAsync(e => e.ResourceId == notes.Id)).Id;
            db.ChangeTracker.Clear();

            var archived = await ArchiveCompanyAsync(db, user, auth, acme.Id);

            // While it sat in the Museum, a new company took its slug and a new document one of its documents'.
            Assert.Equal(StatusCodes.Status201Created, StatusOf(await CompanyEndpoints.CreateAsync(new CreateCompanyRequest("Acme (new)", "acme"), db, user)));
            db.Documents.Add(new Document { TenantId = tenantId, Title = "New onboarding", Slug = "acme-onboarding" });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            // Nothing comes back into a company that is still archived.
            var child = await db.ArchiveEntries.AsNoTracking().FirstAsync(e => e.ParentEntryId == archived.ArchiveEntryId);
            Assert.Equal(StatusCodes.Status409Conflict, StatusOf(await ArchiveEndpoints.RestoreAsync(child.Id, db, user, auth)));
            Assert.Equal(StatusCodes.Status409Conflict, StatusOf(await ArchiveEndpoints.RestoreAsync(notesEntryId, db, user, auth)));

            var audit = new RecordingAudit();
            var result = await ArchiveEndpoints.RestoreAsync(archived.ArchiveEntryId, db, user, auth, audit);
            Assert.Equal(StatusCodes.Status200OK, StatusOf(result));
            var item = ValueOf<ArchiveEndpoints.ArchiveEntryItem>(result);
            Assert.Equal(ArchiveEndpoints.StateRestored, item.State);
            Assert.Equal(4, item.Items);

            db.ChangeTracker.Clear();
            var restored = await db.Companies.SingleAsync(c => c.Id == acme.Id);
            Assert.Equal("acme-restored", restored.Slug);
            Assert.Null(restored.DeletedAt);
            Assert.Equal("SRV-01", Assert.Single(await db.Assets.ToListAsync()).Name);
            Assert.Equal("acme-onboarding-restored", (await db.Documents.SingleAsync(d => d.Id == doc.Id)).Slug);
            Assert.Equal("acme-patching", (await db.Runbooks.SingleAsync()).Slug);
            Assert.Single(await db.KeeperLinks.ToListAsync());
            Assert.Equal(folder.Id, Assert.Single(await db.DocumentFolders.ToListAsync()).Id);
            Assert.Single(await db.RunbookRuns.ToListAsync());
            Assert.All(
                await db.ArchiveEntries.AsNoTracking().Where(e => e.ParentEntryId == archived.ArchiveEntryId).ToListAsync(),
                e => Assert.NotNull(e.RestoredAt));
            Assert.Contains(audit.Entries, e => e.Action == "Company.Restore"
                && (e.Details ?? "").Contains("4 item(s)") && (e.Details ?? "").Contains("acme-restored"));

            // What was archived before the company stays archived, and can now come back on its own.
            Assert.False(await db.Documents.AnyAsync(d => d.Id == notes.Id));
            Assert.Equal(StatusCodes.Status200OK, StatusOf(await ArchiveEndpoints.RestoreAsync(notesEntryId, db, user, auth)));
        }
    }

    [Fact]
    public async Task Sub_Companies_Go_First_And_Come_Back_After_Their_Parent()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var (db, user, auth) = Open(dbName, tenantId);
        await using (db)
        {
            var holding = new Company { TenantId = tenantId, Name = "Holding", Slug = "holding" };
            db.Companies.Add(holding);
            await db.SaveChangesAsync();
            var subsidiary = new Company { TenantId = tenantId, Name = "Subsidiary", Slug = "subsidiary", ParentCompanyId = holding.Id };
            db.Companies.Add(subsidiary);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var refused = await CompanyEndpoints.ArchiveAsync(holding.Id, db, user, auth);
            Assert.Equal(StatusCodes.Status409Conflict, StatusOf(refused));
            Assert.True(await db.Companies.AnyAsync(c => c.Id == holding.Id));

            var subsidiaryEntry = (await ArchiveCompanyAsync(db, user, auth, subsidiary.Id)).ArchiveEntryId;
            var holdingEntry = (await ArchiveCompanyAsync(db, user, auth, holding.Id)).ArchiveEntryId;
            db.ChangeTracker.Clear();

            // The archived subsidiary still names it, so the holding cannot be destroyed…
            Assert.Equal(StatusCodes.Status409Conflict, StatusOf(await ArchiveEndpoints.PermanentDeleteAsync(holdingEntry, db, user)));
            // …and the subsidiary cannot come back under a parent nobody can see.
            Assert.Equal(StatusCodes.Status409Conflict, StatusOf(await ArchiveEndpoints.RestoreAsync(subsidiaryEntry, db, user, auth)));

            Assert.Equal(StatusCodes.Status200OK, StatusOf(await ArchiveEndpoints.RestoreAsync(holdingEntry, db, user, auth)));
            Assert.Equal(StatusCodes.Status200OK, StatusOf(await ArchiveEndpoints.RestoreAsync(subsidiaryEntry, db, user, auth)));
            db.ChangeTracker.Clear();
            Assert.Equal(2, await db.Companies.CountAsync());
        }
    }

    [Fact]
    public async Task Readers_Cannot_Archive_A_Company()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        Guid companyId;
        var (owner, _, _) = Open(dbName, tenantId);
        await using (owner)
        {
            var company = new Company { TenantId = tenantId, Name = "Acme", Slug = "acme" };
            owner.Companies.Add(company);
            await owner.SaveChangesAsync();
            companyId = company.Id;
        }

        var (db, reader, auth) = Open(dbName, tenantId, UserRole.Reader);
        await using (db)
        {
            Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await CompanyEndpoints.ArchiveAsync(companyId, db, reader, auth)));
            Assert.True(await db.Companies.AnyAsync(c => c.Id == companyId));
            Assert.Empty(await db.ArchiveEntries.ToListAsync());
        }
    }

    [Fact]
    public async Task Permanent_Delete_Destroys_The_Company_With_Everything_It_Still_Has()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var (db, user, auth) = Open(dbName, tenantId);
        await using (db)
        {
            var type = await AddAssetTypeAsync(db, tenantId);
            var field = new FieldDefinition { AssetTypeId = type.Id, Name = "Serial", FieldType = "Text" };
            var acme = new Company { TenantId = tenantId, Name = "Acme", Slug = "acme" };
            var other = new Company { TenantId = tenantId, Name = "Other", Slug = "other" };
            var group = new SecurityGroup { TenantId = tenantId, Name = "Acme techs" };
            var flag = new FlagDefinition { TenantId = tenantId, Name = "VIP", Color = "#f00" };
            var connection = new IntegrationConnection
            {
                TenantId = tenantId,
                Provider = IntegrationProvider.Halo,
                DisplayName = "Halo",
                AuthSecretName = "halo-secret",
            };
            var template = new Runbook { TenantId = tenantId, Title = "Onboarding template", Slug = "onboarding-template" };
            db.AddRange(field, acme, other, group, flag, connection, template);
            await db.SaveChangesAsync();

            var kb = new DocumentFolder { TenantId = tenantId, Name = "Acme KB", CompanyId = acme.Id };
            db.DocumentFolders.Add(kb);
            await db.SaveChangesAsync();
            var network = new DocumentFolder { TenantId = tenantId, Name = "Network", CompanyId = acme.Id, ParentId = kb.Id };
            db.DocumentFolders.Add(network);
            await db.SaveChangesAsync();
            // Filed under the company's folders from elsewhere: moved out, not destroyed.
            var borrowed = new DocumentFolder { TenantId = tenantId, Name = "Other's notes", CompanyId = other.Id, ParentId = network.Id };
            var stray = new Document { TenantId = tenantId, Title = "Other's runbook notes", Slug = "other-notes", CompanyId = other.Id, FolderId = kb.Id };

            var asset = new Asset { TenantId = tenantId, Name = "SRV-01", AssetTypeId = type.Id, CompanyId = acme.Id };
            var earlier = new Asset { TenantId = tenantId, Name = "SRV-OLD", AssetTypeId = type.Id, CompanyId = acme.Id };
            var otherAsset = new Asset { TenantId = tenantId, Name = "OTHER-01", AssetTypeId = type.Id, CompanyId = other.Id };
            var doc = new Document { TenantId = tenantId, Title = "Acme VPN", Slug = "acme-vpn", CompanyId = acme.Id, FolderId = network.Id };
            var runbook = new Runbook { TenantId = tenantId, Title = "Acme patching", Slug = "acme-patching", CompanyId = acme.Id };
            var link = new KeeperLink { TenantId = tenantId, Name = "Acme domain admin", CompanyId = acme.Id };
            db.AddRange(borrowed, stray, asset, earlier, otherAsset, doc, runbook, link);
            await db.SaveChangesAsync();

            db.CustomFieldValues.Add(new CustomFieldValue { AssetId = asset.Id, FieldDefinitionId = field.Id, Value = "ABC123" });
            db.DocumentVersions.Add(new DocumentVersion { DocumentId = doc.Id, VersionNumber = 1, Title = "Acme VPN" });
            db.RunbookSteps.Add(new RunbookStep { RunbookId = runbook.Id, Order = 1, Title = "Snapshot" });
            db.RunbookRuns.Add(new RunbookRun { TenantId = tenantId, RunbookId = runbook.Id, CompanyId = acme.Id });
            db.RunbookRuns.Add(new RunbookRun { TenantId = tenantId, RunbookId = template.Id, CompanyId = acme.Id });
            db.AssetTypeCompanyActivations.Add(new AssetTypeCompanyActivation { TenantId = tenantId, AssetTypeId = type.Id, CompanyId = acme.Id });
            db.SecurityGroupCompanyGrants.Add(new SecurityGroupCompanyGrant { TenantId = tenantId, SecurityGroupId = group.Id, CompanyId = acme.Id });
            db.IntegrationMappings.Add(new IntegrationMapping
            {
                TenantId = tenantId,
                IntegrationConnectionId = connection.Id,
                ExternalId = "halo-100",
                ExternalType = "company",
                LocalEntityType = nameof(Company),
                LocalEntityId = acme.Id,
            });
            db.FlagAssignments.Add(new FlagAssignment { TenantId = tenantId, FlagDefinitionId = flag.Id, EntityType = FlagEntityType.Company, EntityId = acme.Id });
            db.ResourceLinks.Add(new ResourceLink
            {
                TenantId = tenantId,
                FromType = LinkEntityType.Company,
                FromId = acme.Id,
                ToType = LinkEntityType.Asset,
                ToId = otherAsset.Id,
            });
            await db.SaveChangesAsync();

            await AssetEndpoints.DeleteAsync(earlier.Id, db, user, auth);
            db.ChangeTracker.Clear();
            var archived = await ArchiveCompanyAsync(db, user, auth, acme.Id);
            db.ChangeTracker.Clear();

            var audit = new RecordingAudit();
            Assert.Equal(StatusCodes.Status204NoContent, StatusOf(await ArchiveEndpoints.PermanentDeleteAsync(archived.ArchiveEntryId, db, user, audit)));

            db.ChangeTracker.Clear();
            Assert.False(await db.Companies.IgnoreQueryFilters().AnyAsync(c => c.Id == acme.Id));
            Assert.Equal("OTHER-01", Assert.Single(await db.Assets.IgnoreQueryFilters().ToListAsync()).Name);
            Assert.Empty(await db.CustomFieldValues.IgnoreQueryFilters().ToListAsync());
            Assert.Equal(stray.Id, Assert.Single(await db.Documents.IgnoreQueryFilters().ToListAsync()).Id);
            Assert.Null((await db.Documents.SingleAsync(d => d.Id == stray.Id)).FolderId);
            Assert.Empty(await db.DocumentVersions.IgnoreQueryFilters().ToListAsync());
            var folders = await db.DocumentFolders.IgnoreQueryFilters().ToListAsync();
            Assert.Equal(borrowed.Id, Assert.Single(folders).Id);
            Assert.Null(folders[0].ParentId);
            Assert.Equal(template.Id, Assert.Single(await db.Runbooks.IgnoreQueryFilters().ToListAsync()).Id);
            Assert.Empty(await db.RunbookSteps.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await db.RunbookRuns.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await db.KeeperLinks.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await db.AssetTypeCompanyActivations.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await db.SecurityGroupCompanyGrants.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await db.IntegrationMappings.ToListAsync());
            Assert.Empty(await db.FlagAssignments.ToListAsync());
            Assert.Empty(await db.ResourceLinks.ToListAsync());
            Assert.True(await db.Companies.AnyAsync(c => c.Id == other.Id));

            // Every entry involved stays, as a tombstone: the company's, its contents', and the
            // asset archived on its own before.
            var entries = await db.ArchiveEntries.AsNoTracking().ToListAsync();
            Assert.Equal(6, entries.Count);
            Assert.All(entries, e => Assert.NotNull(e.PermanentlyDeletedAt));
            Assert.All(entries, e => Assert.Equal(user.ObjectId, e.PermanentlyDeletedByObjectId));
            Assert.Contains(audit.Entries, e => e.Action == "Company.PermanentDelete" && (e.Details ?? "").Contains("5 item(s)"));

            Assert.Equal(StatusCodes.Status409Conflict, StatusOf(await ArchiveEndpoints.RestoreAsync(archived.ArchiveEntryId, db, user, auth)));
        }
    }

    [Fact]
    public async Task Platform_Counts_Only_Live_Companies()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var (db, user, auth) = Open(dbName, tenantId);
        await using (db)
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Customer", Slug = $"c-{tenantId:N}" });
            var kept = new Company { TenantId = tenantId, Name = "Kept", Slug = "kept" };
            var gone = new Company { TenantId = tenantId, Name = "Gone", Slug = "gone" };
            db.Companies.AddRange(kept, gone);
            await db.SaveChangesAsync();
            await ArchiveCompanyAsync(db, user, auth, gone.Id);

            var tenants = ValueOf<List<PlatformEndpoints.TenantSummary>>(await PlatformEndpoints.ListTenantsAsync(db));
            Assert.Equal(1, Assert.Single(tenants).Companies);
        }
    }

    private static async Task<IntegrationConnection> AddHaloConnectionAsync(DocuEngAIneDbContext db, Guid tenantId)
    {
        var connection = new IntegrationConnection
        {
            TenantId = tenantId,
            Provider = IntegrationProvider.Halo,
            DisplayName = "Halo",
            AuthSecretName = "halo-secret",
        };
        db.IntegrationConnections.Add(connection);
        await db.SaveChangesAsync();
        return connection;
    }

    [Fact]
    public async Task Sync_Leaves_A_Mapped_Company_In_The_Museum_Alone()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var (db, user, auth) = Open(dbName, tenantId);
        await using (db)
        {
            var connection = await AddHaloConnectionAsync(db, tenantId);
            connection.UpdateCompanyDetails = true;
            var company = new Company { TenantId = tenantId, Name = "Local Name", Slug = "local-name" };
            db.Companies.Add(company);
            await db.SaveChangesAsync();
            db.IntegrationMappings.Add(new IntegrationMapping
            {
                TenantId = tenantId,
                IntegrationConnectionId = connection.Id,
                ExternalId = "halo-100",
                ExternalType = "company",
                LocalEntityType = nameof(Company),
                LocalEntityId = company.Id,
            });
            await db.SaveChangesAsync();
            await ArchiveCompanyAsync(db, user, auth, company.Id);

            var sync = new IntegrationSyncService(db, user, new NoopMcp(), new RecordingAudit());
            var run = await sync.SyncFromPayloadAsync(connection.Id, [new ExternalCompanyDto("halo-100", "Remote Name")]);

            Assert.Equal(SyncRunStatus.Succeeded, run.Status);
            Assert.Equal(0, run.ItemsCreated);
            Assert.Equal(0, run.ItemsUpdated);
            Assert.Equal(1, run.ItemsSkipped);
            db.ChangeTracker.Clear();
            var stored = await db.Companies.IgnoreQueryFilters().SingleAsync();
            Assert.Equal("Local Name", stored.Name);
            Assert.NotNull(stored.DeletedAt);
            Assert.Null(stored.HaloClientId);
        }
    }

    [Fact]
    public async Task Sync_Does_Not_Recreate_An_Archived_Company_It_Recognises()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var (db, user, auth) = Open(dbName, tenantId);
        await using (db)
        {
            var connection = await AddHaloConnectionAsync(db, tenantId);
            var company = new Company { TenantId = tenantId, Name = "ExampleCo", Slug = "exampleco" };
            db.Companies.Add(company);
            await db.SaveChangesAsync();
            await ArchiveCompanyAsync(db, user, auth, company.Id);

            var sync = new IntegrationSyncService(db, user, new NoopMcp(), new RecordingAudit());
            var run = await sync.SyncFromPayloadAsync(connection.Id, [new ExternalCompanyDto("halo-7", "ExampleCo")]);

            Assert.Equal(SyncRunStatus.Succeeded, run.Status);
            Assert.Equal(0, run.ItemsCreated);
            Assert.Equal(1, run.ItemsSkipped);
            Assert.Equal(1, await db.Companies.IgnoreQueryFilters().CountAsync());
            Assert.Empty(await db.IntegrationMappings.ToListAsync());
        }
    }

    [Fact]
    public async Task Sync_Adopts_A_Live_Company_Over_An_Archived_Namesake()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var (db, user, auth) = Open(dbName, tenantId);
        await using (db)
        {
            var connection = await AddHaloConnectionAsync(db, tenantId);
            var old = new Company { TenantId = tenantId, Name = "ExampleCo", Slug = "exampleco-old" };
            db.Companies.Add(old);
            await db.SaveChangesAsync();
            await ArchiveCompanyAsync(db, user, auth, old.Id);
            var live = new Company { TenantId = tenantId, Name = "ExampleCo", Slug = "exampleco" };
            db.Companies.Add(live);
            await db.SaveChangesAsync();

            var sync = new IntegrationSyncService(db, user, new NoopMcp(), new RecordingAudit());
            var run = await sync.SyncFromPayloadAsync(connection.Id, [new ExternalCompanyDto("halo-7", "ExampleCo")]);

            Assert.Equal(SyncRunStatus.Succeeded, run.Status);
            Assert.Equal(1, run.ItemsUpdated);
            Assert.Equal(0, run.ItemsCreated);
            Assert.Equal(live.Id, (await db.IntegrationMappings.SingleAsync()).LocalEntityId);
            db.ChangeTracker.Clear();
            Assert.Equal("halo-7", (await db.Companies.SingleAsync(c => c.Id == live.Id)).HaloClientId);
            Assert.Null((await db.Companies.IgnoreQueryFilters().SingleAsync(c => c.Id == old.Id)).HaloClientId);
        }
    }

    [Fact]
    public async Task Sync_Treats_A_Mapping_To_A_Deleted_Company_As_A_New_Record()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var (db, user, _) = Open(dbName, tenantId);
        await using (db)
        {
            var connection = await AddHaloConnectionAsync(db, tenantId);
            db.IntegrationMappings.Add(new IntegrationMapping
            {
                TenantId = tenantId,
                IntegrationConnectionId = connection.Id,
                ExternalId = "halo-100",
                ExternalType = "company",
                LocalEntityType = nameof(Company),
                LocalEntityId = Guid.NewGuid(),
            });
            await db.SaveChangesAsync();

            var sync = new IntegrationSyncService(db, user, new NoopMcp(), new RecordingAudit());
            var run = await sync.SyncFromPayloadAsync(connection.Id, [new ExternalCompanyDto("halo-100", "Fresh Co")]);

            Assert.Equal(SyncRunStatus.Succeeded, run.Status);
            Assert.Equal(1, run.ItemsCreated);
            var company = await db.Companies.SingleAsync();
            Assert.Equal("Fresh Co", company.Name);
            var mapping = await db.IntegrationMappings.SingleAsync();
            Assert.Equal("halo-100", mapping.ExternalId);
            Assert.Equal(company.Id, mapping.LocalEntityId);
        }
    }

    [Fact]
    public async Task ItGlue_Import_Skips_An_Archived_Company_And_What_Belongs_To_It()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var (db, user, auth) = Open(dbName, tenantId);
        await using (db)
        {
            var company = new Company
            {
                TenantId = tenantId,
                Name = "ExampleCo",
                Slug = "exampleco",
                ExternalIdsJson = CompanyIdentity.UpsertExternalId(null, CompanyIdentity.ItGlueKey, "1"),
            };
            db.Companies.Add(company);
            await db.SaveChangesAsync();
            await ArchiveCompanyAsync(db, user, auth, company.Id);

            var service = new ItGlueMigrationService(db, user, new NoopMcp(), new RecordingAudit());
            var result = await service.ImportAsync(null, ItGlueMigrationTests.MixedSliceFixture);

            Assert.Equal(nameof(SyncRunStatus.Succeeded), result.Status);
            Assert.Equal(0, result.CompaniesCreated);
            Assert.Equal(0, result.CompaniesUpdated);
            Assert.Equal(0, result.DocumentsCreated);
            // The organization-less flexible asset is still imported, tenant-wide.
            Assert.Equal(1, result.AssetsCreated);
            // The password, the organization and its document.
            Assert.Equal(3, result.ItemsSkipped);
            Assert.Equal(1, await db.Companies.IgnoreQueryFilters().CountAsync());
            Assert.Empty(await db.Documents.IgnoreQueryFilters().ToListAsync());
        }
    }

    [Fact]
    public async Task ItGlue_Reimport_Does_Not_Recreate_Archived_Documents_Or_Assets()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var (db, user, auth) = Open(dbName, tenantId);
        await using (db)
        {
            var service = new ItGlueMigrationService(db, user, new NoopMcp(), new RecordingAudit());
            var first = await service.ImportAsync(null, ItGlueMigrationTests.MixedSliceFixture);
            Assert.Equal(1, first.DocumentsCreated);
            Assert.Equal(1, first.AssetsCreated);

            await DocumentEndpoints.DeleteAsync((await db.Documents.SingleAsync()).Id, db, user, auth);
            await AssetEndpoints.DeleteAsync((await db.Assets.SingleAsync()).Id, db, user, auth);

            var second = await service.ImportAsync(null, ItGlueMigrationTests.MixedSliceFixture);

            Assert.Equal(nameof(SyncRunStatus.Succeeded), second.Status);
            Assert.Equal(1, second.CompaniesUpdated);
            Assert.Equal(0, second.DocumentsCreated);
            Assert.Equal(0, second.DocumentsUpdated);
            Assert.Equal(0, second.AssetsCreated);
            Assert.Equal(0, second.AssetsUpdated);
            // The password, the archived document and the archived asset.
            Assert.Equal(3, second.ItemsSkipped);
            Assert.Equal(1, await db.Documents.IgnoreQueryFilters().CountAsync());
            Assert.Equal(1, await db.Assets.IgnoreQueryFilters().CountAsync());
        }
    }

    [Fact]
    public async Task Hudu_Import_Skips_An_Archived_Company_And_Its_Articles()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var (db, user, auth) = Open(dbName, tenantId);
        await using (db)
        {
            var server = new McpServer
            {
                TenantId = tenantId,
                Name = McpServerDefaults.StackJackCompactName,
                Kind = McpServerKind.StackJackCompact,
                EndpointUrl = McpServerDefaults.StackJackCompactEndpoint,
                AuthSecretName = "kv-stackjack-compact",
            };
            var company = new Company
            {
                TenantId = tenantId,
                Name = "ExampleCo",
                Slug = "exampleco",
                ExternalIdsJson = CompanyIdentity.UpsertExternalId(null, CompanyIdentity.HuduKey, "42"),
            };
            db.AddRange(server, company);
            await db.SaveChangesAsync();
            await ArchiveCompanyAsync(db, user, auth, company.Id);

            var import = new HuduMigrationService(db, user, new NoopMcp(), new RecordingAudit());
            var result = await import.ImportAsync(server.Id, new HuduImportPayload(
                Companies: [new ExternalCompanyDto("42", "ExampleCo", "exampleco", PrimaryDomain: "example.com")],
                Articles: [new HuduArticleRecord("7", "VPN Setup", "<p>Use the company gateway.</p>", "vpn-setup", "42", FolderName: "Networking")]));

            Assert.NotNull(result);
            Assert.Equal(0, result.CompaniesCreated);
            Assert.Equal(0, result.CompaniesUpdated);
            Assert.Equal(1, result.CompaniesSkipped);
            Assert.Equal(0, result.ArticlesCreated);
            Assert.Equal(1, result.ArticlesSkipped);
            Assert.Equal(1, await db.Companies.IgnoreQueryFilters().CountAsync());
            Assert.Empty(await db.Documents.IgnoreQueryFilters().ToListAsync());
            Assert.Empty(await db.DocumentFolders.IgnoreQueryFilters().ToListAsync());
        }
    }
}
