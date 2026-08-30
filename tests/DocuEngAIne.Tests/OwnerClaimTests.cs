using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

/// <summary>
/// Covers <see cref="TenantEndpoints.ClaimOwnerAsync"/>, the backfill for tenants created before
/// onboard granted Owner. The cases that matter are the grant and the refusal: an empty tenant
/// gets exactly one Owner, and a second person cannot steal it.
/// </summary>
public class OwnerClaimTests
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
        UserRole claimRole = UserRole.None)
    {
        var user = new FakeCurrentUser
        {
            TenantId = tenantId,
            ObjectId = callerObjectId,
            Email = $"{callerObjectId}@example.com",
            DisplayName = callerObjectId,
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

    private static async Task SeedTenantAsync(string dbName, Guid tenantId, params User[] users)
    {
        var (db, _) = Open(dbName, tenantId, "seeder");
        await using (db)
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "ExampleCo", Slug = "exampleco" });
            if (users.Length > 0)
                db.Users.AddRange(users);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Empty_Tenant_Claims_Owner()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        await SeedTenantAsync(dbName, tenantId);

        var (db, user) = Open(dbName, tenantId, "first-caller");
        await using var _ = db;
        var audit = new RecordingAudit();

        var result = await TenantEndpoints.ClaimOwnerAsync(db, user, audit);

        AssertStatus(result, StatusCodes.Status200OK);

        db.ChangeTracker.Clear();
        var stored = await db.Users.ForTenant(user).AsNoTracking().SingleAsync();
        Assert.Equal("first-caller", stored.EntraObjectId);
        Assert.Equal(UserRole.Owner, stored.Role);
        Assert.True(stored.IsActive);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal("User.ClaimOwner", entry.Action);
        Assert.Equal(nameof(User), entry.EntityType);
        Assert.Equal(stored.Id, entry.EntityId);
    }

    [Fact]
    public async Task Empty_Tenant_With_Reader_Rows_Promotes_The_Caller()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        await SeedTenantAsync(
            dbName,
            tenantId,
            NewUser(tenantId, "first-caller", UserRole.Reader),
            NewUser(tenantId, "other-reader", UserRole.Reader));

        var (db, user) = Open(dbName, tenantId, "first-caller");
        await using var _ = db;

        var result = await TenantEndpoints.ClaimOwnerAsync(db, user, new RecordingAudit());

        AssertStatus(result, StatusCodes.Status200OK);

        db.ChangeTracker.Clear();
        var caller = await db.Users.ForTenant(user).AsNoTracking().SingleAsync(u => u.EntraObjectId == "first-caller");
        var other = await db.Users.ForTenant(user).AsNoTracking().SingleAsync(u => u.EntraObjectId == "other-reader");
        Assert.Equal(UserRole.Owner, caller.Role);
        Assert.Equal(UserRole.Reader, other.Role);
    }

    [Fact]
    public async Task Tenant_With_An_Owner_Returns_409()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        await SeedTenantAsync(dbName, tenantId, NewUser(tenantId, "existing-owner", UserRole.Owner));

        var (db, user) = Open(dbName, tenantId, "second-caller");
        await using var _ = db;
        var audit = new RecordingAudit();

        var result = await TenantEndpoints.ClaimOwnerAsync(db, user, audit);

        AssertMessage(result, StatusCodes.Status409Conflict, TenantEndpoints.OwnerAlreadyExistsMessage);
        Assert.Empty(audit.Entries);

        db.ChangeTracker.Clear();
        Assert.False(await db.Users.ForTenant(user).AnyAsync(u => u.EntraObjectId == "second-caller"));
        var owner = await db.Users.ForTenant(user).AsNoTracking().SingleAsync();
        Assert.Equal("existing-owner", owner.EntraObjectId);
        Assert.Equal(UserRole.Owner, owner.Role);
    }

    [Fact]
    public async Task Second_Caller_Cannot_Steal_Owner_After_A_Successful_Claim()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        await SeedTenantAsync(dbName, tenantId);

        var (firstDb, firstUser) = Open(dbName, tenantId, "first-caller");
        await using (firstDb)
        {
            var granted = await TenantEndpoints.ClaimOwnerAsync(firstDb, firstUser, new RecordingAudit());
            AssertStatus(granted, StatusCodes.Status200OK);
        }

        var (secondDb, secondUser) = Open(dbName, tenantId, "second-caller");
        await using var _ = secondDb;
        var stolen = await TenantEndpoints.ClaimOwnerAsync(secondDb, secondUser, new RecordingAudit());

        AssertMessage(stolen, StatusCodes.Status409Conflict, TenantEndpoints.OwnerAlreadyExistsMessage);

        secondDb.ChangeTracker.Clear();
        var owners = await secondDb.Users.ForTenant(secondUser).AsNoTracking()
            .Where(u => u.Role == UserRole.Owner)
            .ToListAsync();
        var owner = Assert.Single(owners);
        Assert.Equal("first-caller", owner.EntraObjectId);
    }

    [Fact]
    public async Task An_Active_Admin_Blocks_The_Claim()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        await SeedTenantAsync(dbName, tenantId, NewUser(tenantId, "existing-admin", UserRole.Admin));

        var (db, user) = Open(dbName, tenantId, "reader");
        await using var _ = db;

        var result = await TenantEndpoints.ClaimOwnerAsync(db, user, new RecordingAudit());

        AssertMessage(result, StatusCodes.Status409Conflict, TenantEndpoints.OwnerAlreadyExistsMessage);
        db.ChangeTracker.Clear();
        Assert.False(await db.Users.ForTenant(user).AnyAsync(u => u.EntraObjectId == "reader"));
    }

    [Fact]
    public async Task Another_Tenants_Owner_Does_Not_Block_This_Tenant()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await SeedTenantAsync(dbName, tenantA);
        await SeedTenantAsync(dbName, tenantB, NewUser(tenantB, "tenant-b-owner", UserRole.Owner));

        var (db, user) = Open(dbName, tenantA, "tenant-a-caller");
        await using var _ = db;

        var result = await TenantEndpoints.ClaimOwnerAsync(db, user, new RecordingAudit());

        AssertStatus(result, StatusCodes.Status200OK);
        db.ChangeTracker.Clear();
        var stored = await db.Users.ForTenant(user).AsNoTracking().SingleAsync();
        Assert.Equal(UserRole.Owner, stored.Role);
        Assert.Equal(tenantA, stored.TenantId);
        Assert.False(await db.Users.ForTenant(user).AnyAsync(u => u.EntraObjectId == "tenant-b-owner"));
    }

    [Fact]
    public async Task A_Tenant_That_Has_Not_Been_Onboarded_Is_Not_Found()
    {
        var tenantId = Guid.NewGuid();
        var (db, user) = Open(Guid.NewGuid().ToString(), tenantId, "caller");
        await using var _ = db;

        var result = await TenantEndpoints.ClaimOwnerAsync(db, user, new RecordingAudit());

        AssertMessage(result, StatusCodes.Status404NotFound, TenantEndpoints.TenantNotOnboardedMessage);
        Assert.False(await db.Users.AnyAsync());
    }
}
