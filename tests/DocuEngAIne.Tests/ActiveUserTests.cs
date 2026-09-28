using System.Security.Claims;
using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

/// <summary>
/// Deactivation has to mean something: a deactivated user is refused on every authenticated route,
/// its stored role and grants confer nothing, and deactivating / reactivating is guarded by the
/// same Owner invariants as role changes.
/// </summary>
public class ActiveUserTests
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

    private static (DocuEngAIneDbContext Db, FakeCurrentUser User) Open(
        string dbName, Guid tenantId, string objectId, UserRole claimRole = UserRole.None)
    {
        var user = new FakeCurrentUser
        {
            TenantId = tenantId,
            ObjectId = objectId,
            Email = $"{objectId}@example.com",
            Role = claimRole,
        };
        var db = new DocuEngAIneDbContext(
            new DbContextOptionsBuilder<DocuEngAIneDbContext>().UseInMemoryDatabase(dbName).Options, user);
        return (db, user);
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

    private static async Task<bool> ActiveGateAsync(DocuEngAIneDbContext db, FakeCurrentUser user)
    {
        var handler = new ActiveUserAuthorizationHandler(db, user);
        var requirement = new ActiveUserRequirement();
        var context = new AuthorizationHandlerContext(
            new IAuthorizationRequirement[] { requirement },
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("roles", "Admin")], "TestJwt")),
            resource: null);
        await handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static int StatusOf(IResult result)
        => result is IStatusCodeHttpResult s && s.StatusCode is int code ? code : 0;

    [Fact]
    public async Task Active_Gate_Refuses_Only_A_Deactivated_Row_In_The_Callers_Tenant()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        // Seeded through a tenant-B context: SaveChanges stamps TenantId from the current user.
        var (dbB, _) = Open(dbName, tenantB, "alice");
        await using (dbB)
        {
            // Same object id deactivated in ANOTHER tenant must not lock alice out of tenant A.
            dbB.Users.Add(NewUser(tenantB, "alice", UserRole.Admin, isActive: false));
            await dbB.SaveChangesAsync();
        }

        var (db, user) = Open(dbName, tenantA, "alice");
        await using (db)
        {
            // No row in tenant A yet (first sign-in provisions it): allowed.
            Assert.True(await ActiveGateAsync(db, user));

            db.Users.Add(NewUser(tenantA, "alice", UserRole.Admin));
            await db.SaveChangesAsync();
            Assert.True(await ActiveGateAsync(db, user));

            var row = await db.Users.SingleAsync(u => u.TenantId == tenantA);
            row.IsActive = false;
            await db.SaveChangesAsync();

            // Deactivated — even though the principal carries an Entra Admin app role.
            Assert.False(await ActiveGateAsync(db, user));
        }
    }

    [Fact]
    public async Task Deactivated_Row_Confers_Neither_Role_Nor_Grants()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var (db, user) = Open(dbName, tenantId, "bob");
        await using (db)
        {
            var bob = NewUser(tenantId, "bob", UserRole.Reader);
            db.Users.Add(bob);
            await db.SaveChangesAsync();
            var docId = Guid.NewGuid();
            db.ResourceRoleAssignments.Add(new ResourceRoleAssignment
            {
                TenantId = tenantId,
                UserId = bob.Id,
                ResourceType = ResourceType.Document,
                ResourceId = docId,
                Role = UserRole.Contributor,
            });
            await db.SaveChangesAsync();

            var auth = new ResourceAuthorizationService(db, user);
            Assert.True(await auth.CanWriteAsync(docId, ResourceType.Document));

            bob.IsActive = false;
            await db.SaveChangesAsync();
            Assert.False(await auth.CanWriteAsync(docId, ResourceType.Document));
            Assert.False(await auth.CanReadAsync(docId, ResourceType.Document));
        }
    }

    [Fact]
    public async Task Deactivate_And_Reactivate_Persist_And_Are_Audited()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var (db, user) = Open(dbName, tenantId, "owner", UserRole.Owner);
        await using (db)
        {
            db.Users.Add(NewUser(tenantId, "owner", UserRole.Owner));
            var tech = NewUser(tenantId, "tech", UserRole.Contributor);
            db.Users.Add(tech);
            await db.SaveChangesAsync();

            var audit = new RecordingAudit();
            Assert.Equal(StatusCodes.Status204NoContent, StatusOf(await UserEndpoints.SetActiveAsync(tech.Id, false, db, user, audit)));
            Assert.False((await db.Users.AsNoTracking().SingleAsync(u => u.Id == tech.Id)).IsActive);

            // Idempotent: deactivating again changes nothing and writes no second row.
            Assert.Equal(StatusCodes.Status204NoContent, StatusOf(await UserEndpoints.SetActiveAsync(tech.Id, false, db, user, audit)));

            Assert.Equal(StatusCodes.Status204NoContent, StatusOf(await UserEndpoints.SetActiveAsync(tech.Id, true, db, user, audit)));
            Assert.True((await db.Users.AsNoTracking().SingleAsync(u => u.Id == tech.Id)).IsActive);

            Assert.Equal(new[] { "User.Deactivate", "User.Activate" }, audit.Entries.Select(e => e.Action).ToArray());
            Assert.All(audit.Entries, e => Assert.Equal(AuditCategories.Security, e.Category));
        }
    }

    [Fact]
    public async Task Cannot_Deactivate_Yourself_Or_The_Last_Active_Owner()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var (db, user) = Open(dbName, tenantId, "owner", UserRole.Owner);
        await using (db)
        {
            var owner = NewUser(tenantId, "owner", UserRole.Owner);
            db.Users.Add(owner);
            await db.SaveChangesAsync();

            var self = await UserEndpoints.SetActiveAsync(owner.Id, false, db, user, new RecordingAudit());
            Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(self));
            Assert.Equal(UserEndpoints.CannotDeactivateSelfMessage, Assert.IsAssignableFrom<IValueHttpResult>(self).Value);
        }

        var (db2, admin) = Open(dbName, tenantId, "admin", UserRole.Admin);
        await using (db2)
        {
            db2.Users.Add(NewUser(tenantId, "admin", UserRole.Admin));
            await db2.SaveChangesAsync();
            var owner = await db2.Users.SingleAsync(u => u.EntraObjectId == "owner");

            var lastOwner = await UserEndpoints.SetActiveAsync(owner.Id, false, db2, admin, new RecordingAudit());
            Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(lastOwner));
            Assert.Equal(UserEndpoints.LastOwnerDeactivateMessage, Assert.IsAssignableFrom<IValueHttpResult>(lastOwner).Value);
            Assert.True((await db2.Users.AsNoTracking().SingleAsync(u => u.Id == owner.Id)).IsActive);
        }
    }

    [Fact]
    public async Task Only_An_Owner_Can_Deactivate_Or_Reactivate_An_Owner()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var (db, admin) = Open(dbName, tenantId, "admin", UserRole.Admin);
        await using (db)
        {
            db.Users.Add(NewUser(tenantId, "admin", UserRole.Admin));
            db.Users.Add(NewUser(tenantId, "owner-1", UserRole.Owner));
            var second = NewUser(tenantId, "owner-2", UserRole.Owner);
            db.Users.Add(second);
            await db.SaveChangesAsync();

            var refused = await UserEndpoints.SetActiveAsync(second.Id, false, db, admin, new RecordingAudit());
            Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(refused));
            Assert.True((await db.Users.AsNoTracking().SingleAsync(u => u.Id == second.Id)).IsActive);
        }

        var (db2, owner) = Open(dbName, tenantId, "owner-1", UserRole.Owner);
        await using (db2)
        {
            var second = await db2.Users.SingleAsync(u => u.EntraObjectId == "owner-2");
            Assert.Equal(StatusCodes.Status204NoContent, StatusOf(await UserEndpoints.SetActiveAsync(second.Id, false, db2, owner, new RecordingAudit())));
        }

        var (db3, admin2) = Open(dbName, tenantId, "admin", UserRole.Admin);
        await using (db3)
        {
            var second = await db3.Users.SingleAsync(u => u.EntraObjectId == "owner-2");
            // Reactivating an Owner grants Owner-level access: an Admin cannot do it while an Owner exists.
            Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(await UserEndpoints.SetActiveAsync(second.Id, true, db3, admin2, new RecordingAudit())));
        }
    }

    [Fact]
    public async Task Other_Tenant_User_Is_Not_Found()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        Guid foreignId;
        var (dbB, _) = Open(dbName, tenantB, "b-owner", UserRole.Owner);
        await using (dbB)
        {
            var foreign = NewUser(tenantB, "victim", UserRole.Contributor);
            dbB.Users.Add(foreign);
            await dbB.SaveChangesAsync();
            foreignId = foreign.Id;
        }

        var (db, user) = Open(dbName, tenantA, "owner", UserRole.Owner);
        await using (db)
        {
            Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await UserEndpoints.SetActiveAsync(foreignId, false, db, user, new RecordingAudit())));
            Assert.True((await db.Users.AsNoTracking().SingleAsync(u => u.Id == foreignId)).IsActive);
        }
    }
}
