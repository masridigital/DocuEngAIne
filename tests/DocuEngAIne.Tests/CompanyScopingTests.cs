using System.Net;
using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using DocuEngAIne.Infrastructure.Search;
using DocuEngAIne.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocuEngAIne.Tests;

/// <summary>
/// Security-group company scoping: who is restricted (and who never is), what a restricted caller
/// can read through the EF company filters, and what each access level lets them write.
/// </summary>
public class CompanyScopingTests
{
    private const string TechObjectId = "tech-oid";

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

    /// <summary>One tenant: companies Alpha and Bravo, one record of each kind per company, and tenant-wide ones.</summary>
    private sealed record World(
        string DbName,
        Guid TenantId,
        Guid CompanyA,
        Guid CompanyB,
        Guid AssetA,
        Guid AssetB,
        Guid AssetTenantWide,
        Guid DocA,
        Guid DocB,
        Guid DocTenantWide,
        Guid RunbookTemplate,
        Guid RunbookB,
        Guid KeeperA,
        Guid KeeperB,
        Guid FolderA,
        Guid FolderTenantWide,
        Guid TechUserId);

    private static DocuEngAIneDbContext Context(string dbName, ICurrentUser user, ICompanyScopeAccessor? accessor = null)
        => new(new DbContextOptionsBuilder<DocuEngAIneDbContext>().UseInMemoryDatabase(dbName).Options, user, accessor);

    private static FakeCurrentUser Owner(Guid tenantId) => new() { TenantId = tenantId, ObjectId = "owner-oid", Role = UserRole.Owner };

    private static FakeCurrentUser Tech(Guid tenantId, UserRole claimRole) => new()
    {
        TenantId = tenantId,
        ObjectId = TechObjectId,
        Email = "tech@example.com",
        Role = claimRole,
    };

    private static int StatusOf(IResult? result)
        => result is IStatusCodeHttpResult s && s.StatusCode is int code ? code : 0;

    private static T ValueOf<T>(IResult result) => Assert.IsAssignableFrom<T>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);

    private static async Task<World> SeedAsync(string dbName)
    {
        var tenantId = Guid.NewGuid();
        await using var db = Context(dbName, Owner(tenantId));
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "MSP", Slug = $"msp-{tenantId:N}" });

        var a = new Company { TenantId = tenantId, Name = "Alpha", Slug = "alpha" };
        var b = new Company { TenantId = tenantId, Name = "Bravo", Slug = "bravo" };
        var type = new AssetType { TenantId = tenantId, Name = "Server" };
        var assetA = new Asset { TenantId = tenantId, Name = "alpha-srv", AssetTypeId = type.Id, CompanyId = a.Id };
        var assetB = new Asset { TenantId = tenantId, Name = "bravo-srv", AssetTypeId = type.Id, CompanyId = b.Id };
        var assetTenantWide = new Asset { TenantId = tenantId, Name = "msp-srv", AssetTypeId = type.Id };
        var docA = new Document { TenantId = tenantId, Title = "Alpha VPN", Slug = "alpha-vpn", CompanyId = a.Id, Content = "alpha" };
        var docB = new Document { TenantId = tenantId, Title = "Bravo VPN", Slug = "bravo-vpn", CompanyId = b.Id, Content = "bravo" };
        var docTenantWide = new Document { TenantId = tenantId, Title = "MSP SOP", Slug = "msp-sop", Content = "central" };
        var template = new Runbook { TenantId = tenantId, Title = "Patch Tuesday", Slug = "patch-tuesday" };
        var runbookB = new Runbook { TenantId = tenantId, Title = "Bravo onboarding", Slug = "bravo-onboarding", CompanyId = b.Id };
        var keeperA = new KeeperLink { TenantId = tenantId, Name = "Alpha admin", CompanyId = a.Id };
        var keeperB = new KeeperLink { TenantId = tenantId, Name = "Bravo admin", CompanyId = b.Id };
        var folderA = new DocumentFolder { TenantId = tenantId, Name = "Alpha KB", CompanyId = a.Id };
        var folderTenantWide = new DocumentFolder { TenantId = tenantId, Name = "Central KB" };
        var tech = new User { TenantId = tenantId, EntraObjectId = TechObjectId, Email = "tech@example.com", Role = UserRole.Contributor };

        db.AddRange(a, b, type, assetA, assetB, assetTenantWide, docA, docB, docTenantWide, template, runbookB, keeperA, keeperB, folderA, folderTenantWide, tech);
        db.DocumentVersions.Add(new DocumentVersion { DocumentId = docB.Id, VersionNumber = 1, Title = "Bravo VPN v1" });
        var archivedAt = DateTimeOffset.UtcNow;
        db.ArchiveEntries.AddRange(
            new ArchiveEntry { TenantId = tenantId, ResourceType = ResourceType.Asset, ResourceId = Guid.NewGuid(), ResourceLabel = "old-alpha", CompanyId = a.Id, ArchivedAt = archivedAt },
            new ArchiveEntry { TenantId = tenantId, ResourceType = ResourceType.Asset, ResourceId = Guid.NewGuid(), ResourceLabel = "old-bravo", CompanyId = b.Id, ArchivedAt = archivedAt },
            new ArchiveEntry { TenantId = tenantId, ResourceType = ResourceType.Document, ResourceId = Guid.NewGuid(), ResourceLabel = "old-central", ArchivedAt = archivedAt });
        await db.SaveChangesAsync();

        return new World(dbName, tenantId, a.Id, b.Id, assetA.Id, assetB.Id, assetTenantWide.Id, docA.Id, docB.Id, docTenantWide.Id,
            template.Id, runbookB.Id, keeperA.Id, keeperB.Id, folderA.Id, folderTenantWide.Id, tech.Id);
    }

    private static async Task GrantAsync(World w, bool includeTenantWide, params (Guid CompanyId, CompanyAccessLevel Level)[] grants)
    {
        await using var db = Context(w.DbName, Owner(w.TenantId));
        var group = new SecurityGroup { TenantId = w.TenantId, Name = $"group-{Guid.NewGuid():N}", IncludeTenantWide = includeTenantWide };
        db.SecurityGroups.Add(group);
        db.SecurityGroupMembers.Add(new SecurityGroupMember { TenantId = w.TenantId, SecurityGroupId = group.Id, UserId = w.TechUserId });
        foreach (var (companyId, level) in grants)
        {
            db.SecurityGroupCompanyGrants.Add(new SecurityGroupCompanyGrant
            {
                TenantId = w.TenantId,
                SecurityGroupId = group.Id,
                CompanyId = companyId,
                Level = level,
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>The tech's context with their scope resolved, as the middleware would for a request.</summary>
    private static async Task<(DocuEngAIneDbContext Db, FakeCurrentUser User, CompanyAccessScope Scope)> OpenAsTechAsync(
        World w, UserRole claimRole = UserRole.Contributor)
    {
        var user = Tech(w.TenantId, claimRole);
        var accessor = new CompanyScopeAccessor();
        var db = Context(w.DbName, user, accessor);
        accessor.Current = await new CompanyScopeResolver(db).ResolveAsync(user);
        return (db, user, accessor.Current);
    }

    [Fact]
    public async Task No_Group_Or_Only_Groups_Without_Grants_Is_Unrestricted()
    {
        var w = await SeedAsync(nameof(No_Group_Or_Only_Groups_Without_Grants_Is_Unrestricted));

        var (_, _, scope) = await OpenAsTechAsync(w);
        Assert.True(scope.IsUnrestricted);

        await GrantAsync(w, includeTenantWide: false);
        (_, _, scope) = await OpenAsTechAsync(w);
        Assert.True(scope.IsUnrestricted);
    }

    [Fact]
    public async Task Grants_Confine_To_The_Granted_Companies_At_The_Highest_Level()
    {
        var w = await SeedAsync(nameof(Grants_Confine_To_The_Granted_Companies_At_The_Highest_Level));
        await GrantAsync(w, includeTenantWide: false, (w.CompanyA, CompanyAccessLevel.View));
        await GrantAsync(w, includeTenantWide: true, (w.CompanyA, CompanyAccessLevel.Manage), (w.CompanyB, CompanyAccessLevel.Edit));

        var (_, _, scope) = await OpenAsTechAsync(w);

        Assert.False(scope.IsUnrestricted);
        Assert.True(scope.IncludesTenantWide);
        Assert.Equal(CompanyAccessLevel.Manage, scope.LevelFor(w.CompanyA));
        Assert.Equal(CompanyAccessLevel.Edit, scope.LevelFor(w.CompanyB));
        Assert.Equal(CompanyAccessLevel.None, scope.LevelFor(Guid.NewGuid()));
        Assert.Equal(CompanyAccessLevel.View, scope.LevelFor(null));
    }

    [Fact]
    public async Task Admins_And_Owners_Are_Never_Restricted()
    {
        var w = await SeedAsync(nameof(Admins_And_Owners_Are_Never_Restricted));
        await GrantAsync(w, includeTenantWide: false, (w.CompanyA, CompanyAccessLevel.View));

        var (_, _, byAppRole) = await OpenAsTechAsync(w, UserRole.Admin);
        Assert.True(byAppRole.IsUnrestricted);

        await using (var db = Context(w.DbName, Owner(w.TenantId)))
        {
            var tech = await db.Users.SingleAsync(u => u.Id == w.TechUserId);
            tech.Role = UserRole.Admin;
            await db.SaveChangesAsync();
        }

        var (_, _, byStoredRole) = await OpenAsTechAsync(w);
        Assert.True(byStoredRole.IsUnrestricted);
    }

    [Fact]
    public async Task Another_Tenants_Groups_Do_Not_Apply()
    {
        var dbName = nameof(Another_Tenants_Groups_Do_Not_Apply);
        var w = await SeedAsync(dbName);
        var other = await SeedAsync(dbName);
        await GrantAsync(other, includeTenantWide: false, (other.CompanyA, CompanyAccessLevel.View));

        var (_, _, scope) = await OpenAsTechAsync(w);

        Assert.True(scope.IsUnrestricted);
    }

    [Fact]
    public async Task Restricted_Reads_See_Only_Granted_Companies()
    {
        var w = await SeedAsync(nameof(Restricted_Reads_See_Only_Granted_Companies));
        await GrantAsync(w, includeTenantWide: false, (w.CompanyA, CompanyAccessLevel.View));
        var (db, user, _) = await OpenAsTechAsync(w);

        Assert.Equal(new[] { w.CompanyA }, await db.Companies.ForTenant(user).Select(c => c.Id).ToArrayAsync());
        Assert.Equal(new[] { w.AssetA }, await db.Assets.ForTenant(user).Select(a => a.Id).ToArrayAsync());
        Assert.Equal(new[] { w.DocA }, await db.Documents.ForTenant(user).Select(d => d.Id).ToArrayAsync());
        Assert.Empty(await db.Runbooks.ForTenant(user).ToListAsync());
        Assert.Equal(new[] { w.KeeperA }, await db.KeeperLinks.ForTenant(user).Select(k => k.Id).ToArrayAsync());
        Assert.Equal(new[] { w.FolderA }, await db.DocumentFolders.ForTenant(user).Select(f => f.Id).ToArrayAsync());
        Assert.Equal(new[] { "old-alpha" }, await db.ArchiveEntries.ForTenant(user).Select(e => e.ResourceLabel).ToArrayAsync());
        // A dependent queried directly cannot reach around its owner's filter.
        Assert.Empty(await db.DocumentVersions.Where(v => v.DocumentId == w.DocB).ToListAsync());
    }

    [Fact]
    public async Task Tenant_Wide_Records_Need_A_Group_That_Includes_Them()
    {
        var w = await SeedAsync(nameof(Tenant_Wide_Records_Need_A_Group_That_Includes_Them));
        await GrantAsync(w, includeTenantWide: true, (w.CompanyA, CompanyAccessLevel.View));
        var (db, user, _) = await OpenAsTechAsync(w);

        var assets = await db.Assets.ForTenant(user).Select(a => a.Id).ToListAsync();
        Assert.Equal(2, assets.Count);
        Assert.Contains(w.AssetA, assets);
        Assert.Contains(w.AssetTenantWide, assets);
        Assert.Equal(new[] { w.RunbookTemplate }, await db.Runbooks.ForTenant(user).Select(r => r.Id).ToArrayAsync());
        Assert.Contains(w.FolderTenantWide, await db.DocumentFolders.ForTenant(user).Select(f => f.Id).ToListAsync());
        Assert.DoesNotContain(w.CompanyB, await db.Companies.ForTenant(user).Select(c => c.Id).ToListAsync());
    }

    [Fact]
    public async Task A_Context_Without_A_Resolved_Scope_Sees_Everything()
    {
        var w = await SeedAsync(nameof(A_Context_Without_A_Resolved_Scope_Sees_Everything));
        await GrantAsync(w, includeTenantWide: false, (w.CompanyA, CompanyAccessLevel.View));

        // Background work and API tokens run with no scope resolved.
        await using var db = Context(w.DbName, Tech(w.TenantId, UserRole.Contributor), new CompanyScopeAccessor());

        Assert.Equal(2, await db.Companies.CountAsync());
        Assert.Equal(3, await db.Assets.CountAsync());
    }

    [Fact]
    public async Task Edit_Allows_Changes_And_Manage_Is_Needed_To_Archive()
    {
        var w = await SeedAsync(nameof(Edit_Allows_Changes_And_Manage_Is_Needed_To_Archive));
        await GrantAsync(w, includeTenantWide: true, (w.CompanyA, CompanyAccessLevel.Edit));
        var (db, user, _) = await OpenAsTechAsync(w);
        var authorization = new ResourceAuthorizationService(db, user);

        Assert.Null(await ResourceWriteGuard.RequireWriteAsync(authorization, user, w.AssetA, ResourceType.Asset));
        Assert.Equal(403, StatusOf(await ResourceWriteGuard.RequireWriteAsync(
            authorization, user, w.AssetA, ResourceType.Asset, CancellationToken.None, CompanyAccessLevel.Manage)));
        // Out of scope, and tenant-wide (readable, not writable), are refused alike.
        Assert.Equal(403, StatusOf(await ResourceWriteGuard.RequireWriteAsync(authorization, user, w.AssetB, ResourceType.Asset)));
        Assert.Equal(403, StatusOf(await ResourceWriteGuard.RequireWriteAsync(authorization, user, w.AssetTenantWide, ResourceType.Asset)));
        Assert.Equal(403, StatusOf(await ResourceWriteGuard.RequireWriteAsync(authorization, user, Guid.NewGuid(), ResourceType.Asset)));
        Assert.Null(await ResourceWriteGuard.RequireWriteAsync(authorization, user, w.FolderA, ResourceType.DocumentFolder));
    }

    [Fact]
    public async Task Records_Can_Only_Be_Written_Into_Companies_With_Edit()
    {
        var w = await SeedAsync(nameof(Records_Can_Only_Be_Written_Into_Companies_With_Edit));
        await GrantAsync(w, includeTenantWide: true, (w.CompanyA, CompanyAccessLevel.View), (w.CompanyB, CompanyAccessLevel.Edit));
        var (db, user, _) = await OpenAsTechAsync(w);

        Assert.Equal(403, StatusOf(await CompanyEndpoints.EnsureCompanyInTenantAsync(db, user, w.CompanyA)));
        Assert.Null(await CompanyEndpoints.EnsureCompanyInTenantAsync(db, user, w.CompanyB));
        Assert.Equal(400, StatusOf(await CompanyEndpoints.EnsureCompanyInTenantAsync(db, user, Guid.NewGuid())));
        // No company means tenant-wide, which a restricted caller can read but not write.
        Assert.Equal(403, StatusOf(await CompanyEndpoints.EnsureCompanyInTenantAsync(db, user, null)));
        Assert.Equal(403, StatusOf(await CompanyEndpoints.ApplyCompanyIdOnUpdateAsync(db, user, null, companyIdClear: true, _ => { })));
    }

    [Fact]
    public async Task Company_Details_Need_Edit_And_Status_Or_Portal_Changes_Need_Manage()
    {
        var w = await SeedAsync(nameof(Company_Details_Need_Edit_And_Status_Or_Portal_Changes_Need_Manage));
        await GrantAsync(w, includeTenantWide: false, (w.CompanyA, CompanyAccessLevel.Edit));
        var (db, user, _) = await OpenAsTechAsync(w);

        Assert.Equal(204, StatusOf(await CompanyEndpoints.UpdateAsync(w.CompanyA, new UpdateCompanyRequest(Notes: "edited"), db, user)));
        Assert.Equal(403, StatusOf(await CompanyEndpoints.UpdateAsync(w.CompanyA, new UpdateCompanyRequest(PortalEnabled: true), db, user)));
        Assert.Equal(403, StatusOf(await CompanyEndpoints.UpdateAsync(w.CompanyA, new UpdateCompanyRequest(IsActive: false), db, user)));
        Assert.Equal(404, StatusOf(await CompanyEndpoints.UpdateAsync(w.CompanyB, new UpdateCompanyRequest(Notes: "x"), db, user)));
        // A restricted user would lose sight of a company they created.
        Assert.Equal(403, StatusOf(await CompanyEndpoints.CreateAsync(new CreateCompanyRequest("Charlie", "charlie"), db, user)));

        await GrantAsync(w, includeTenantWide: false, (w.CompanyA, CompanyAccessLevel.Manage));
        (db, user, _) = await OpenAsTechAsync(w);
        Assert.Equal(204, StatusOf(await CompanyEndpoints.UpdateAsync(w.CompanyA, new UpdateCompanyRequest(PortalEnabled: true), db, user)));
    }

    [Fact]
    public async Task A_Run_Needs_Edit_On_The_Company_It_Is_For()
    {
        var w = await SeedAsync(nameof(A_Run_Needs_Edit_On_The_Company_It_Is_For));
        await GrantAsync(w, includeTenantWide: true, (w.CompanyA, CompanyAccessLevel.Edit));
        var (db, user, _) = await OpenAsTechAsync(w);

        // A tenant-wide template, run for the company the tech can edit.
        var started = await RunbookEndpoints.StartRunAsync(w.RunbookTemplate, new StartRunbookRunRequest(w.CompanyA), db, user);
        Assert.Equal(201, StatusOf(started));
        // Without a company the run would be tenant-wide.
        Assert.Equal(403, StatusOf(await RunbookEndpoints.StartRunAsync(w.RunbookTemplate, new StartRunbookRunRequest(), db, user)));
        Assert.Equal(400, StatusOf(await RunbookEndpoints.StartRunAsync(w.RunbookTemplate, new StartRunbookRunRequest(w.CompanyB), db, user)));
        Assert.Equal(404, StatusOf(await RunbookEndpoints.StartRunAsync(w.RunbookB, new StartRunbookRunRequest(w.CompanyA), db, user)));
    }

    [Fact]
    public async Task Links_Need_Edit_On_Their_Source_And_A_Visible_Target()
    {
        var w = await SeedAsync(nameof(Links_Need_Edit_On_Their_Source_And_A_Visible_Target));
        await GrantAsync(w, includeTenantWide: true, (w.CompanyA, CompanyAccessLevel.View), (w.CompanyB, CompanyAccessLevel.Edit));
        var (db, user, _) = await OpenAsTechAsync(w);

        var toSop = new CreateResourceLinkRequest(LinkEntityType.Asset, w.AssetB, LinkEntityType.Document, w.DocTenantWide);
        Assert.Equal(201, StatusOf(await LinkEndpoints.CreateAsync(toSop, db, user)));
        var fromViewOnly = new CreateResourceLinkRequest(LinkEntityType.Asset, w.AssetA, LinkEntityType.Document, w.DocTenantWide);
        Assert.Equal(403, StatusOf(await LinkEndpoints.CreateAsync(fromViewOnly, db, user)));
    }

    [Fact]
    public async Task Flags_Need_Edit_On_The_Flagged_Record_And_Definitions_Are_Tenant_Wide()
    {
        var w = await SeedAsync(nameof(Flags_Need_Edit_On_The_Flagged_Record_And_Definitions_Are_Tenant_Wide));
        var flag = new FlagDefinition { TenantId = w.TenantId, Name = "Review", Color = "#DC2626" };
        await using (var owner = Context(w.DbName, Owner(w.TenantId)))
        {
            owner.FlagDefinitions.Add(flag);
            await owner.SaveChangesAsync();
        }

        await GrantAsync(w, includeTenantWide: true, (w.CompanyA, CompanyAccessLevel.View), (w.CompanyB, CompanyAccessLevel.Edit));
        var (db, user, _) = await OpenAsTechAsync(w);

        Assert.Equal(403, StatusOf(await FlagEndpoints.AssignAsync(flag.Id, new AssignFlagRequest(FlagEntityType.Asset, w.AssetA), db, user)));
        Assert.Equal(201, StatusOf(await FlagEndpoints.AssignAsync(flag.Id, new AssignFlagRequest(FlagEntityType.Asset, w.AssetB), db, user)));
        Assert.Equal(403, StatusOf(await FlagEndpoints.CreateDefinitionAsync(new CreateFlagDefinitionRequest("Stale", "#2563EB"), db, user)));
    }

    [Fact]
    public async Task Search_Hits_Are_Filtered_To_Readable_Companies()
    {
        var tenantId = Guid.NewGuid();
        var alpha = Guid.NewGuid();
        var search = new InMemorySearchService();
        await search.IndexDocumentAsync(new SearchDocument(Guid.NewGuid(), "VPN alpha", "vpn", alpha, tenantId));
        await search.IndexDocumentAsync(new SearchDocument(Guid.NewGuid(), "VPN bravo", "vpn", Guid.NewGuid(), tenantId));
        await search.IndexDocumentAsync(new SearchDocument(Guid.NewGuid(), "VPN central", "vpn", null, tenantId));
        var user = new FakeCurrentUser { TenantId = tenantId };
        var levels = new Dictionary<Guid, CompanyAccessLevel> { [alpha] = CompanyAccessLevel.View };

        var restricted = new CompanyScopeAccessor { Current = CompanyAccessScope.Restricted(false, levels) };
        var hits = ValueOf<IReadOnlyList<SearchHit>>(await SearchEndpoints.SearchAsync("vpn", search, user, restricted));
        Assert.Equal(new[] { "VPN alpha" }, hits.Select(h => h.Title).ToArray());

        var withTenantWide = new CompanyScopeAccessor { Current = CompanyAccessScope.Restricted(true, levels) };
        hits = ValueOf<IReadOnlyList<SearchHit>>(await SearchEndpoints.SearchAsync("vpn", search, user, withTenantWide));
        Assert.Equal(new[] { "VPN alpha", "VPN central" }, hits.Select(h => h.Title).ToArray());

        hits = ValueOf<IReadOnlyList<SearchHit>>(await SearchEndpoints.SearchAsync("vpn", search, user));
        Assert.Equal(3, hits.Count);
    }

    [Fact]
    public async Task The_Museum_Records_The_Company_And_Restore_Needs_Manage()
    {
        var w = await SeedAsync(nameof(The_Museum_Records_The_Company_And_Restore_Needs_Manage));
        Guid entryId;
        await using (var owner = Context(w.DbName, Owner(w.TenantId)))
        {
            var asset = await owner.Assets.SingleAsync(a => a.Id == w.AssetA);
            await ArchiveEndpoints.ArchiveAsync(owner, Owner(w.TenantId), null, asset, ResourceType.Asset, asset.Id, asset.Name, null, CancellationToken.None);
            var entry = await owner.ArchiveEntries.SingleAsync(e => e.ResourceId == w.AssetA);
            Assert.Equal(w.CompanyA, entry.CompanyId);
            entryId = entry.Id;
        }

        await GrantAsync(w, includeTenantWide: false, (w.CompanyA, CompanyAccessLevel.Edit));
        var (db, user, _) = await OpenAsTechAsync(w);
        Assert.Equal(403, StatusOf(await ArchiveEndpoints.RestoreAsync(entryId, db, user, new ResourceAuthorizationService(db, user))));

        await GrantAsync(w, includeTenantWide: false, (w.CompanyA, CompanyAccessLevel.Manage));
        (db, user, _) = await OpenAsTechAsync(w);
        Assert.Equal(200, StatusOf(await ArchiveEndpoints.RestoreAsync(entryId, db, user, new ResourceAuthorizationService(db, user))));
    }

    [Fact]
    public async Task Group_Admin_Api_Validates_Audits_And_Stays_In_Tenant()
    {
        var dbName = nameof(Group_Admin_Api_Validates_Audits_And_Stays_In_Tenant);
        var w = await SeedAsync(dbName);
        var other = await SeedAsync(dbName);
        var admin = new FakeCurrentUser { TenantId = w.TenantId, ObjectId = "admin-oid", Role = UserRole.Admin };
        await using var db = Context(dbName, admin);
        var audit = new RecordingAudit();

        Assert.Equal(400, StatusOf(await SecurityGroupEndpoints.CreateAsync(new("  "), db, admin, audit)));
        var created = await SecurityGroupEndpoints.CreateAsync(new("Field techs", "Alpha only"), db, admin, audit);
        Assert.Equal(201, StatusOf(created));
        var groupId = ValueOf<SecurityGroupEndpoints.GroupDetail>(created).Group.Id;
        Assert.Equal(409, StatusOf(await SecurityGroupEndpoints.CreateAsync(new("Field techs"), db, admin, audit)));

        Assert.Equal(404, StatusOf(await SecurityGroupEndpoints.AddMemberAsync(groupId, other.TechUserId, db, admin, audit)));
        Assert.Equal(200, StatusOf(await SecurityGroupEndpoints.AddMemberAsync(groupId, w.TechUserId, db, admin, audit)));
        Assert.Equal(200, StatusOf(await SecurityGroupEndpoints.AddMemberAsync(groupId, w.TechUserId, db, admin, audit)));

        Assert.Equal(400, StatusOf(await SecurityGroupEndpoints.SetCompanyAsync(groupId, w.CompanyA, new(CompanyAccessLevel.None), db, admin, audit)));
        Assert.Equal(404, StatusOf(await SecurityGroupEndpoints.SetCompanyAsync(groupId, other.CompanyA, new(CompanyAccessLevel.View), db, admin, audit)));
        Assert.Equal(200, StatusOf(await SecurityGroupEndpoints.SetCompanyAsync(groupId, w.CompanyA, new(CompanyAccessLevel.View), db, admin, audit)));
        var detail = ValueOf<SecurityGroupEndpoints.GroupDetail>(
            await SecurityGroupEndpoints.SetCompanyAsync(groupId, w.CompanyA, new(CompanyAccessLevel.Edit), db, admin, audit));
        Assert.Equal(w.TechUserId, Assert.Single(detail.Members).UserId);
        Assert.Equal(CompanyAccessLevel.Edit, Assert.Single(detail.Companies).Level);

        var effective = ValueOf<SecurityGroupEndpoints.AccessView>(
            await SecurityGroupEndpoints.EffectiveAsync(w.TechUserId, db, admin, new CompanyScopeResolver(db)));
        Assert.True(effective.Restricted);
        Assert.Equal("Alpha", Assert.Single(effective.Companies).CompanyName);

        Assert.Equal(
            new[] { "SecurityGroup.Create", "SecurityGroup.AddMember", "SecurityGroup.SetCompany", "SecurityGroup.SetCompany" },
            audit.Entries.Select(e => e.Action).ToArray());
        Assert.All(audit.Entries, e => Assert.Equal(AuditCategories.Access, e.Category));

        Assert.Equal(204, StatusOf(await SecurityGroupEndpoints.DeleteAsync(groupId, db, admin, audit)));
        Assert.Empty(await db.SecurityGroupMembers.ForTenant(admin).ToListAsync());
        Assert.Empty(await db.SecurityGroupCompanyGrants.ForTenant(admin).ToListAsync());
        Assert.Equal(404, StatusOf(await SecurityGroupEndpoints.GetAsync(groupId, db, admin)));
    }
}

/// <summary>Company scoping on the real request pipeline: the middleware resolves the scope per request.</summary>
public class CompanyScopingPipelineTests : IClassFixture<TestHost>
{
    private readonly TestHost _host;

    public CompanyScopingPipelineTests(TestHost host) => _host = host;

    [Fact]
    public async Task A_Restricted_User_Sees_Only_Granted_Companies_Over_Http()
    {
        var tenantId = Guid.NewGuid();
        var techObjectId = $"tech-{Guid.NewGuid():N}";
        Guid hidden;
        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocuEngAIneDbContext>();
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "Scoped MSP", Slug = $"scoped-{tenantId:N}" });
            var granted = new Company { TenantId = tenantId, Name = "Xray Co", Slug = $"xray-{tenantId:N}" };
            var other = new Company { TenantId = tenantId, Name = "Yankee Co", Slug = $"yankee-{tenantId:N}" };
            var tech = new User { TenantId = tenantId, EntraObjectId = techObjectId, Email = "tech@scoped.test", Role = UserRole.Contributor };
            var group = new SecurityGroup { TenantId = tenantId, Name = "Xray only" };
            db.AddRange(granted, other, tech, group);
            db.SecurityGroupMembers.Add(new SecurityGroupMember { TenantId = tenantId, SecurityGroupId = group.Id, UserId = tech.Id });
            db.SecurityGroupCompanyGrants.Add(new SecurityGroupCompanyGrant
            {
                TenantId = tenantId,
                SecurityGroupId = group.Id,
                CompanyId = granted.Id,
                Level = CompanyAccessLevel.View,
            });
            db.SaveChanges();
            hidden = other.Id;
        }

        using var client = _host.CreateAuthenticatedClient(techObjectId, tenantId);
        var companies = await client.GetStringAsync("/api/companies");
        Assert.Contains("Xray Co", companies);
        Assert.DoesNotContain("Yankee Co", companies);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/companies/{hidden}")).StatusCode);

        var access = await client.GetStringAsync("/api/me/company-access");
        Assert.Contains("\"restricted\":true", access);
        Assert.Contains("Xray Co", access);

        using var admin = _host.CreateAuthenticatedClient($"admin-{Guid.NewGuid():N}", tenantId, nameof(UserRole.Admin));
        Assert.Contains("Yankee Co", await admin.GetStringAsync("/api/companies"));
    }
}
