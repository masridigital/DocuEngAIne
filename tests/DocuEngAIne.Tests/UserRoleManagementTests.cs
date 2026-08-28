using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

/// <summary>
/// Covers <see cref="UserEndpoints.SetRoleAsync"/>, the only code path in the system that writes
/// <see cref="User.Role"/>. The cases that matter are the refusals: this endpoint is the one that
/// can leave a tenant with no administrator at all.
/// </summary>
public class UserRoleManagementTests
{
    private sealed class RecordingAudit : IAuditService
    {
        public List<(string Action, string EntityType, Guid? EntityId, string? Details)> Entries { get; } = [];

        public Task LogAsync(string action, string entityType, Guid? entityId = null, string? details = null, CancellationToken cancellationToken = default)
        {
            Entries.Add((action, entityType, entityId, details));
            return Task.CompletedTask;
        }
    }

    private static (DocuEngAIneDbContext Db, FakeCurrentUser User) Open(
        string dbName,
        Guid tenantId,
        string callerObjectId,
        UserRole claimRole)
    {
        var user = new FakeCurrentUser
        {
            TenantId = tenantId,
            ObjectId = callerObjectId,
            Email = $"{callerObjectId}@example.com",
            // FakeCurrentUser.HasRole is Role >= role, which is what the Entra app-role claims give
            // the real CurrentUser. Setting None models a tenant that never configured app roles.
            Role = claimRole,
        };
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return (new DocuEngAIneDbContext(options, user), user);
    }

    private static User NewUser(Guid tenantId, string objectId, UserRole role, bool isActive = true) => new()
    {
        TenantId = tenantId,
        EntraObjectId = objectId,
        Email = $"{objectId}@example.com",
        DisplayName = objectId,
        Role = role,
        IsActive = isActive,
    };

    private static void AssertStatus(IResult result, int expected)
    {
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(expected, status.StatusCode);
    }

    private static void AssertMessage(IResult result, int expectedStatus, string expectedMessage)
    {
        AssertStatus(result, expectedStatus);
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result);
        Assert.Equal(expectedMessage, value.Value);
    }

    [Fact]
    public async Task Promoting_A_Reader_To_Contributor_Persists_And_Is_Audited()
    {
        var tenantId = Guid.NewGuid();
        var (db, user) = Open(Guid.NewGuid().ToString(), tenantId, "owner", UserRole.Owner);
        await using var _ = db;

        var owner = NewUser(tenantId, "owner", UserRole.Owner);
        var target = NewUser(tenantId, "reader", UserRole.Reader);
        db.Users.AddRange(owner, target);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var audit = new RecordingAudit();
        var result = await UserEndpoints.SetRoleAsync(
            target.Id, new UpdateUserRoleRequest(UserRole.Contributor), db, user, audit);

        AssertStatus(result, StatusCodes.Status204NoContent);

        var stored = await db.Users.AsNoTracking().FirstAsync(u => u.Id == target.Id);
        Assert.Equal(UserRole.Contributor, stored.Role);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal("User.ChangeRole", entry.Action);
        Assert.Equal(nameof(User), entry.EntityType);
        Assert.Equal(target.Id, entry.EntityId);

        var details = entry.Details ?? string.Empty;
        Assert.Contains("Reader", details);
        Assert.Contains("Contributor", details);
        Assert.Contains("owner@example.com", details);
    }

    [Fact]
    public async Task Demoting_The_Only_Owner_Is_Refused()
    {
        var tenantId = Guid.NewGuid();
        var (db, user) = Open(Guid.NewGuid().ToString(), tenantId, "owner", UserRole.Owner);
        await using var _ = db;

        // The sole Owner demoting themselves is the same case as demoting anyone else's last Owner
        // row, and it is the one a real admin is most likely to trigger by accident.
        var owner = NewUser(tenantId, "owner", UserRole.Owner);
        db.Users.AddRange(owner, NewUser(tenantId, "reader", UserRole.Reader));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var audit = new RecordingAudit();
        var result = await UserEndpoints.SetRoleAsync(
            owner.Id, new UpdateUserRoleRequest(UserRole.Reader), db, user, audit);

        AssertMessage(result, StatusCodes.Status400BadRequest, UserEndpoints.LastOwnerMessage);

        var stored = await db.Users.AsNoTracking().FirstAsync(u => u.Id == owner.Id);
        Assert.Equal(UserRole.Owner, stored.Role);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Demoting_One_Of_Two_Owners_Succeeds()
    {
        var tenantId = Guid.NewGuid();
        var (db, user) = Open(Guid.NewGuid().ToString(), tenantId, "owner-a", UserRole.Owner);
        await using var _ = db;

        var second = NewUser(tenantId, "owner-b", UserRole.Owner);
        db.Users.AddRange(NewUser(tenantId, "owner-a", UserRole.Owner), second);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await UserEndpoints.SetRoleAsync(
            second.Id, new UpdateUserRoleRequest(UserRole.Admin), db, user, new RecordingAudit());

        AssertStatus(result, StatusCodes.Status204NoContent);
        var stored = await db.Users.AsNoTracking().FirstAsync(u => u.Id == second.Id);
        Assert.Equal(UserRole.Admin, stored.Role);
    }

    [Fact]
    public async Task An_Inactive_Owner_Does_Not_Keep_The_Last_Active_Owner_Demotable()
    {
        var tenantId = Guid.NewGuid();
        var (db, user) = Open(Guid.NewGuid().ToString(), tenantId, "owner-a", UserRole.Owner);
        await using var _ = db;

        // An inactive user cannot satisfy the admin policy, so an inactive Owner row is not a
        // second administrator and must not license demoting the only one who can still sign in.
        var active = NewUser(tenantId, "owner-a", UserRole.Owner);
        db.Users.AddRange(active, NewUser(tenantId, "owner-b", UserRole.Owner, isActive: false));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await UserEndpoints.SetRoleAsync(
            active.Id, new UpdateUserRoleRequest(UserRole.Admin), db, user, new RecordingAudit());

        AssertMessage(result, StatusCodes.Status400BadRequest, UserEndpoints.LastOwnerMessage);
        var stored = await db.Users.AsNoTracking().FirstAsync(u => u.Id == active.Id);
        Assert.Equal(UserRole.Owner, stored.Role);
    }

    [Fact]
    public async Task A_User_In_Another_Tenant_Is_Not_Found_And_Not_Mutated()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var victim = NewUser(tenantB, "tenant-b-reader", UserRole.Reader);

        var (dbB, _) = Open(dbName, tenantB, "tenant-b-owner", UserRole.Owner);
        await using (dbB)
        {
            dbB.Users.AddRange(NewUser(tenantB, "tenant-b-owner", UserRole.Owner), victim);
            await dbB.SaveChangesAsync();
        }

        var (dbA, userA) = Open(dbName, tenantA, "tenant-a-owner", UserRole.Owner);
        await using (dbA)
        {
            dbA.Users.Add(NewUser(tenantA, "tenant-a-owner", UserRole.Owner));
            await dbA.SaveChangesAsync();
            dbA.ChangeTracker.Clear();

            var audit = new RecordingAudit();
            var result = await UserEndpoints.SetRoleAsync(
                victim.Id, new UpdateUserRoleRequest(UserRole.Owner), dbA, userA, audit);

            AssertStatus(result, StatusCodes.Status404NotFound);
            Assert.Empty(audit.Entries);
        }

        var (dbCheck, _) = Open(dbName, tenantB, "tenant-b-owner", UserRole.Owner);
        await using (dbCheck)
        {
            var stored = await dbCheck.Users.AsNoTracking().FirstAsync(u => u.Id == victim.Id);
            Assert.Equal(UserRole.Reader, stored.Role);
            Assert.Equal(tenantB, stored.TenantId);
        }
    }

    [Fact]
    public async Task An_Undefined_Role_Value_Is_Rejected()
    {
        var tenantId = Guid.NewGuid();
        var (db, user) = Open(Guid.NewGuid().ToString(), tenantId, "owner", UserRole.Owner);
        await using var _ = db;

        var target = NewUser(tenantId, "reader", UserRole.Reader);
        db.Users.AddRange(NewUser(tenantId, "owner", UserRole.Owner), target);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await UserEndpoints.SetRoleAsync(
            target.Id, new UpdateUserRoleRequest((UserRole)99), db, user, new RecordingAudit());

        AssertMessage(result, StatusCodes.Status400BadRequest, UserEndpoints.UnknownRoleMessage);
        var stored = await db.Users.AsNoTracking().FirstAsync(u => u.Id == target.Id);
        Assert.Equal(UserRole.Reader, stored.Role);
    }

    [Fact]
    public async Task A_Missing_Role_Is_Rejected_Rather_Than_Treated_As_None()
    {
        var tenantId = Guid.NewGuid();
        var (db, user) = Open(Guid.NewGuid().ToString(), tenantId, "owner", UserRole.Owner);
        await using var _ = db;

        var target = NewUser(tenantId, "reader", UserRole.Reader);
        db.Users.AddRange(NewUser(tenantId, "owner", UserRole.Owner), target);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var missingBody = await UserEndpoints.SetRoleAsync(target.Id, null, db, user, new RecordingAudit());
        var emptyBody = await UserEndpoints.SetRoleAsync(
            target.Id, new UpdateUserRoleRequest(), db, user, new RecordingAudit());

        AssertMessage(missingBody, StatusCodes.Status400BadRequest, UserEndpoints.RoleRequiredMessage);
        AssertMessage(emptyBody, StatusCodes.Status400BadRequest, UserEndpoints.RoleRequiredMessage);

        var stored = await db.Users.AsNoTracking().FirstAsync(u => u.Id == target.Id);
        Assert.Equal(UserRole.Reader, stored.Role);
    }

    [Fact]
    public async Task An_Admin_Cannot_Grant_Owner_While_The_Tenant_Has_One()
    {
        var tenantId = Guid.NewGuid();
        var (db, user) = Open(Guid.NewGuid().ToString(), tenantId, "admin", UserRole.Admin);
        await using var _ = db;

        var target = NewUser(tenantId, "reader", UserRole.Reader);
        db.Users.AddRange(
            NewUser(tenantId, "owner", UserRole.Owner),
            NewUser(tenantId, "admin", UserRole.Admin),
            target);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await UserEndpoints.SetRoleAsync(
            target.Id, new UpdateUserRoleRequest(UserRole.Owner), db, user, new RecordingAudit());

        AssertMessage(result, StatusCodes.Status403Forbidden, UserEndpoints.OwnerRoleRequiresOwnerMessage);
        var stored = await db.Users.AsNoTracking().FirstAsync(u => u.Id == target.Id);
        Assert.Equal(UserRole.Reader, stored.Role);
    }

    [Fact]
    public async Task An_Admin_Cannot_Demote_An_Owner_While_Another_Owner_Remains()
    {
        var tenantId = Guid.NewGuid();
        var (db, user) = Open(Guid.NewGuid().ToString(), tenantId, "admin", UserRole.Admin);
        await using var _ = db;

        var target = NewUser(tenantId, "owner-b", UserRole.Owner);
        db.Users.AddRange(
            NewUser(tenantId, "owner-a", UserRole.Owner),
            target,
            NewUser(tenantId, "admin", UserRole.Admin));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await UserEndpoints.SetRoleAsync(
            target.Id, new UpdateUserRoleRequest(UserRole.Reader), db, user, new RecordingAudit());

        AssertMessage(result, StatusCodes.Status403Forbidden, UserEndpoints.OwnerRoleRequiresOwnerMessage);
        var stored = await db.Users.AsNoTracking().FirstAsync(u => u.Id == target.Id);
        Assert.Equal(UserRole.Owner, stored.Role);
    }

    [Fact]
    public async Task An_Admin_May_Grant_Owner_When_No_Active_Owner_Remains()
    {
        var tenantId = Guid.NewGuid();
        var (db, user) = Open(Guid.NewGuid().ToString(), tenantId, "admin", UserRole.Admin);
        await using var _ = db;

        // The recovery case: the tenant's Owner was removed out-of-band in Entra. Without this hatch
        // "only an Owner may grant Owner" would be a closed loop and the tier unreachable forever.
        var target = NewUser(tenantId, "admin-b", UserRole.Admin);
        db.Users.AddRange(
            NewUser(tenantId, "admin", UserRole.Admin),
            target,
            NewUser(tenantId, "ex-owner", UserRole.Owner, isActive: false));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await UserEndpoints.SetRoleAsync(
            target.Id, new UpdateUserRoleRequest(UserRole.Owner), db, user, new RecordingAudit());

        AssertStatus(result, StatusCodes.Status204NoContent);
        var stored = await db.Users.AsNoTracking().FirstAsync(u => u.Id == target.Id);
        Assert.Equal(UserRole.Owner, stored.Role);
    }

    [Fact]
    public async Task An_Owner_By_Database_Row_Alone_May_Grant_Owner()
    {
        var tenantId = Guid.NewGuid();
        // No Entra app roles configured at all — the stored User.Row is the only signal, exactly the
        // fallback the admin policy itself relies on.
        var (db, user) = Open(Guid.NewGuid().ToString(), tenantId, "owner", UserRole.None);
        await using var _ = db;

        var target = NewUser(tenantId, "admin", UserRole.Admin);
        db.Users.AddRange(NewUser(tenantId, "owner", UserRole.Owner), target);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await UserEndpoints.SetRoleAsync(
            target.Id, new UpdateUserRoleRequest(UserRole.Owner), db, user, new RecordingAudit());

        AssertStatus(result, StatusCodes.Status204NoContent);
        var stored = await db.Users.AsNoTracking().FirstAsync(u => u.Id == target.Id);
        Assert.Equal(UserRole.Owner, stored.Role);
    }

    [Fact]
    public async Task Setting_The_Role_A_User_Already_Has_Changes_And_Audits_Nothing()
    {
        var tenantId = Guid.NewGuid();
        var (db, user) = Open(Guid.NewGuid().ToString(), tenantId, "owner", UserRole.Owner);
        await using var _ = db;

        var target = NewUser(tenantId, "reader", UserRole.Reader);
        db.Users.AddRange(NewUser(tenantId, "owner", UserRole.Owner), target);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var audit = new RecordingAudit();
        var result = await UserEndpoints.SetRoleAsync(
            target.Id, new UpdateUserRoleRequest(UserRole.Reader), db, user, audit);

        AssertStatus(result, StatusCodes.Status204NoContent);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Listing_Returns_Only_The_Current_Tenants_Users()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var (dbB, _) = Open(dbName, tenantB, "tenant-b-owner", UserRole.Owner);
        await using (dbB)
        {
            dbB.Users.Add(NewUser(tenantB, "tenant-b-owner", UserRole.Owner));
            await dbB.SaveChangesAsync();
        }

        var (dbA, userA) = Open(dbName, tenantA, "tenant-a-owner", UserRole.Owner);
        await using (dbA)
        {
            dbA.Users.AddRange(
                NewUser(tenantA, "tenant-a-owner", UserRole.Owner),
                NewUser(tenantA, "tenant-a-reader", UserRole.Reader));
            await dbA.SaveChangesAsync();
            dbA.ChangeTracker.Clear();

            var items = await UserEndpoints.ListAsync(dbA, userA);

            Assert.Equal(2, items.Count);
            Assert.DoesNotContain(items, i => i.EntraObjectId == "tenant-b-owner");
            Assert.Equal(UserRole.Owner, items[0].Role);
            Assert.Equal("tenant-a-reader@example.com", items[1].Email);
            Assert.True(items[1].IsActive);
        }
    }
}
