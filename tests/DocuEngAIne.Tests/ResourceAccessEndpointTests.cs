using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

/// <summary>
/// The grants that <c>ResourceWriteGuard</c> enforces have to be administrable, or enforcement means
/// one writable user per tenant and a SQL console.
/// </summary>
public class ResourceAccessEndpointTests
{
    private sealed class RecordingAudit : IAuditService
    {
        public List<string> Actions { get; } = [];

        public Task LogAsync(string action, string entityType, Guid? entityId = null, string? details = null, CancellationToken cancellationToken = default)
        {
            Actions.Add(action);
            return Task.CompletedTask;
        }
    }

    private static (DocuEngAIneDbContext Db, FakeCurrentUser User, RecordingAudit Audit) Create(Guid? tenantId = null, string? dbName = null)
    {
        var user = new FakeCurrentUser
        {
            TenantId = tenantId ?? Guid.NewGuid(),
            ObjectId = Guid.NewGuid().ToString(),
            Role = UserRole.Owner,
        };
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString())
            .Options;
        return (new DocuEngAIneDbContext(options, user), user, new RecordingAudit());
    }

    private static async Task<User> AddUserAsync(DocuEngAIneDbContext db, FakeCurrentUser current, UserRole role = UserRole.Reader)
    {
        var u = new User
        {
            TenantId = current.TenantId!.Value,
            EntraObjectId = Guid.NewGuid().ToString(),
            Email = $"{Guid.NewGuid():N}@example.com",
            Role = role,
        };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u;
    }

    private static int StatusOf(IResult result)
        => result is IStatusCodeHttpResult s && s.StatusCode is int code ? code : 0;

    [Fact]
    public async Task Granting_Creates_An_Assignment_The_Guard_Can_Read()
    {
        var (db, user, audit) = Create();
        var target = await AddUserAsync(db, user);
        var documentId = Guid.NewGuid();

        var result = await ResourceAccessEndpoints.GrantAsync(
            new GrantResourceAccessRequest(target.Id, ResourceType.Document, documentId, UserRole.Contributor),
            db, user, audit);

        Assert.Equal(StatusCodes.Status201Created, StatusOf(result));

        var stored = await db.ResourceRoleAssignments.SingleAsync();
        Assert.Equal(target.Id, stored.UserId);
        Assert.Equal(ResourceType.Document, stored.ResourceType);
        Assert.Equal(documentId, stored.ResourceId);
        Assert.Equal(UserRole.Contributor, stored.Role);
        Assert.Equal(user.TenantId, stored.TenantId);
        Assert.Contains("ResourceAccess.Grant", audit.Actions);
    }

    [Fact]
    public async Task Re_Granting_The_Same_Pair_Updates_Instead_Of_Stacking_A_Second_Row()
    {
        var (db, user, audit) = Create();
        var target = await AddUserAsync(db, user);
        var assetId = Guid.NewGuid();

        await ResourceAccessEndpoints.GrantAsync(
            new GrantResourceAccessRequest(target.Id, ResourceType.Asset, assetId, UserRole.Reader), db, user, audit);
        await ResourceAccessEndpoints.GrantAsync(
            new GrantResourceAccessRequest(target.Id, ResourceType.Asset, assetId, UserRole.Contributor), db, user, audit);

        // A duplicate pair would make the guard's effective role depend on row order.
        var stored = await db.ResourceRoleAssignments.SingleAsync();
        Assert.Equal(UserRole.Contributor, stored.Role);
    }

    [Fact]
    public async Task Resource_Type_Is_Matched_Case_Insensitively_Onto_The_Exact_Constant()
    {
        var (db, user, audit) = Create();
        var target = await AddUserAsync(db, user);

        await ResourceAccessEndpoints.GrantAsync(
            new GrantResourceAccessRequest(target.Id, "document", Guid.NewGuid(), UserRole.Contributor), db, user, audit);

        // Stored as "Document", not "document": the guard compares the string, so a near-miss would
        // persist a grant that silently never matches.
        Assert.Equal(ResourceType.Document, (await db.ResourceRoleAssignments.SingleAsync()).ResourceType);
    }

    [Fact]
    public async Task An_Unknown_Resource_Type_Is_Refused_Rather_Than_Stored()
    {
        var (db, user, audit) = Create();
        var target = await AddUserAsync(db, user);

        var result = await ResourceAccessEndpoints.GrantAsync(
            new GrantResourceAccessRequest(target.Id, "Company", Guid.NewGuid(), UserRole.Contributor), db, user, audit);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Empty(await db.ResourceRoleAssignments.ToListAsync());
    }

    [Fact]
    public async Task A_Missing_Body_Or_Role_Is_Refused_Rather_Than_Granting_None_To_Nobody()
    {
        var (db, user, audit) = Create();

        Assert.Equal(StatusCodes.Status400BadRequest,
            StatusOf(await ResourceAccessEndpoints.GrantAsync(null, db, user, audit)));

        var target = await AddUserAsync(db, user);
        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(await ResourceAccessEndpoints.GrantAsync(
            new GrantResourceAccessRequest(target.Id, ResourceType.Document, Guid.NewGuid()), db, user, audit)));

        Assert.Empty(await db.ResourceRoleAssignments.ToListAsync());
    }

    [Fact]
    public async Task An_Undefined_Role_Value_Is_Refused()
    {
        var (db, user, audit) = Create();
        var target = await AddUserAsync(db, user);

        var result = await ResourceAccessEndpoints.GrantAsync(
            new GrantResourceAccessRequest(target.Id, ResourceType.Runbook, Guid.NewGuid(), (UserRole)99), db, user, audit);

        Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
        Assert.Empty(await db.ResourceRoleAssignments.ToListAsync());
    }

    [Fact]
    public async Task Granting_To_Another_Tenants_User_Is_Not_Found_And_Stores_Nothing()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var (dbB, userB, auditB) = Create(tenantB, dbName);
        var foreignUser = await AddUserAsync(dbB, userB);

        var (dbA, userA, auditA) = Create(tenantA, dbName);
        var result = await ResourceAccessEndpoints.GrantAsync(
            new GrantResourceAccessRequest(foreignUser.Id, ResourceType.KeeperLink, Guid.NewGuid(), UserRole.Contributor),
            dbA, userA, auditA);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));
        Assert.Empty(await dbA.ResourceRoleAssignments.ToListAsync());
        Assert.DoesNotContain("ResourceAccess.Grant", auditA.Actions);
    }

    [Fact]
    public async Task Revoking_Removes_The_Grant_And_Audits_It()
    {
        var (db, user, audit) = Create();
        var target = await AddUserAsync(db, user);

        await ResourceAccessEndpoints.GrantAsync(
            new GrantResourceAccessRequest(target.Id, ResourceType.Document, Guid.NewGuid(), UserRole.Contributor), db, user, audit);
        var grantId = (await db.ResourceRoleAssignments.SingleAsync()).Id;

        var result = await ResourceAccessEndpoints.RevokeAsync(grantId, db, user, audit);

        Assert.Equal(StatusCodes.Status204NoContent, StatusOf(result));
        Assert.Empty(await db.ResourceRoleAssignments.ToListAsync());
        Assert.Contains("ResourceAccess.Revoke", audit.Actions);
    }

    [Fact]
    public async Task Revoking_Another_Tenants_Grant_Is_Not_Found_And_Leaves_It_Standing()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var (dbB, userB, auditB) = Create(tenantB, dbName);
        var targetB = await AddUserAsync(dbB, userB);
        await ResourceAccessEndpoints.GrantAsync(
            new GrantResourceAccessRequest(targetB.Id, ResourceType.Asset, Guid.NewGuid(), UserRole.Contributor), dbB, userB, auditB);
        var foreignGrantId = (await dbB.ResourceRoleAssignments.SingleAsync()).Id;

        var (dbA, userA, auditA) = Create(tenantA, dbName);
        var result = await ResourceAccessEndpoints.RevokeAsync(foreignGrantId, dbA, userA, auditA);

        Assert.Equal(StatusCodes.Status404NotFound, StatusOf(result));

        var (dbB2, _, _) = Create(tenantB, dbName);
        Assert.Single(await dbB2.ResourceRoleAssignments.ToListAsync());
    }

    [Fact]
    public async Task Listing_Is_Tenant_Scoped_And_Filterable()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var (dbB, userB, auditB) = Create(tenantB, dbName);
        var targetB = await AddUserAsync(dbB, userB);
        await ResourceAccessEndpoints.GrantAsync(
            new GrantResourceAccessRequest(targetB.Id, ResourceType.Document, Guid.NewGuid(), UserRole.Contributor), dbB, userB, auditB);

        var (db, user, audit) = Create(tenantA, dbName);
        var target = await AddUserAsync(db, user);
        var docId = Guid.NewGuid();
        await ResourceAccessEndpoints.GrantAsync(
            new GrantResourceAccessRequest(target.Id, ResourceType.Document, docId, UserRole.Contributor), db, user, audit);
        await ResourceAccessEndpoints.GrantAsync(
            new GrantResourceAccessRequest(target.Id, ResourceType.Asset, Guid.NewGuid(), UserRole.Reader), db, user, audit);

        var all = Assert.IsAssignableFrom<IEnumerable<ResourceAccessItem>>(
            await ValueOf(ResourceAccessEndpoints.ListAsync(db, user, null, null, null)));
        Assert.Equal(2, all.Count());

        var docsOnly = Assert.IsAssignableFrom<IEnumerable<ResourceAccessItem>>(
            await ValueOf(ResourceAccessEndpoints.ListAsync(db, user, "Document", null, null)));
        var single = Assert.Single(docsOnly);
        Assert.Equal(docId, single.ResourceId);
        Assert.Equal(target.Email, single.Email);
    }

    private static async Task<object?> ValueOf(Task<IResult> task)
    {
        var result = await task;
        return (result as IValueHttpResult)?.Value;
    }
}
