using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

/// <summary>
/// Covers <see cref="ResourceWriteGuard"/> — the piece that finally makes a
/// <see cref="ResourceRoleAssignment"/> change what an endpoint does.
/// </summary>
/// <remarks>
/// <para>
/// These exercise the guard against a real <see cref="ResourceAuthorizationService"/> over an
/// in-memory database, the level <c>RbacTests</c> works at. The test project carries no
/// <c>Microsoft.AspNetCore.Mvc.Testing</c> reference, so nothing here dispatches an HTTP request:
/// what is verified is the decision and the result object the route handlers return, not the wire
/// status code or the route-to-guard wiring. Getting the guard onto the right routes is checked by
/// reading the endpoint files, not by these tests.
/// </para>
/// <para>
/// <see cref="FakeCurrentUser.Role"/> stands for the caller's <em>Entra app-role claim</em> here,
/// because that is what <see cref="FakeCurrentUser.HasRole"/> answers from. It is deliberately
/// pinned to <see cref="UserRole.Reader"/> in every test but the two that name claims, so the
/// assertions exercise the stored-role and grant path rather than short-circuiting on the claim.
/// </para>
/// </remarks>
public class ResourceAuthorizationEnforcementTests
{
    private static (DocuEngAIneDbContext Db, FakeCurrentUser User, ResourceAuthorizationService Auth) Open(
        string dbName,
        Guid tenantId,
        Guid objectId,
        UserRole claimRole = UserRole.Reader)
    {
        var user = new FakeCurrentUser
        {
            TenantId = tenantId,
            ObjectId = objectId.ToString(),
            Role = claimRole,
        };

        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;

        var db = new DocuEngAIneDbContext(options, user);
        return (db, user, new ResourceAuthorizationService(db, user));
    }

    private static async Task<Guid> SeedTenantUserAsync(
        DocuEngAIneDbContext db,
        Guid tenantId,
        Guid objectId,
        UserRole storedRole)
    {
        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = "Tenant",
            Slug = $"tenant-{tenantId:N}",
        });

        var row = new User
        {
            TenantId = tenantId,
            EntraObjectId = objectId.ToString(),
            Email = "member@example.com",
            Role = storedRole,
        };

        db.Users.Add(row);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return row.Id;
    }

    private static async Task GrantAsync(
        DocuEngAIneDbContext db,
        Guid userId,
        Guid resourceId,
        string resourceType,
        UserRole role)
    {
        // TenantId is stamped by DocuEngAIneDbContext.SaveChangesAsync from the context's current
        // user, so a grant lands in whichever tenant the seeding context belongs to.
        db.ResourceRoleAssignments.Add(new ResourceRoleAssignment
        {
            UserId = userId,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Role = role,
        });

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static void AssertForbidden(IResult? result)
    {
        Assert.NotNull(result);
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result!);
        Assert.Equal(StatusCodes.Status403Forbidden, status.StatusCode);
    }

    [Fact]
    public async Task Reader_With_Contributor_Grant_Writes_The_Granted_Resource()
    {
        var tenantId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        var (db, user, auth) = Open(Guid.NewGuid().ToString(), tenantId, objectId);
        await using (db)
        {
            var userId = await SeedTenantUserAsync(db, tenantId, objectId, UserRole.Reader);
            await GrantAsync(db, userId, documentId, ResourceType.Document, UserRole.Contributor);

            var denied = await ResourceWriteGuard.RequireWriteAsync(auth, user, documentId, ResourceType.Document);

            Assert.Null(denied);
        }
    }

    [Fact]
    public async Task Reader_With_Contributor_Grant_Cannot_Write_A_Different_Resource()
    {
        var tenantId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var grantedId = Guid.NewGuid();
        var otherId = Guid.NewGuid();

        var (db, user, auth) = Open(Guid.NewGuid().ToString(), tenantId, objectId);
        await using (db)
        {
            var userId = await SeedTenantUserAsync(db, tenantId, objectId, UserRole.Reader);
            await GrantAsync(db, userId, grantedId, ResourceType.Document, UserRole.Contributor);

            AssertForbidden(await ResourceWriteGuard.RequireWriteAsync(auth, user, otherId, ResourceType.Document));
        }
    }

    [Fact]
    public async Task Grant_On_A_Different_Resource_Type_Does_Not_Carry_Over()
    {
        // The service matches on the ResourceType string as well as the id, so a grant written with
        // one of the ResourceType constants must not unlock the same GUID under another. This is the
        // regression guard for a typo'd resourceType string at a call site.
        var tenantId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();

        var (db, user, auth) = Open(Guid.NewGuid().ToString(), tenantId, objectId);
        await using (db)
        {
            var userId = await SeedTenantUserAsync(db, tenantId, objectId, UserRole.Reader);
            await GrantAsync(db, userId, resourceId, ResourceType.Document, UserRole.Contributor);

            Assert.Null(await ResourceWriteGuard.RequireWriteAsync(auth, user, resourceId, ResourceType.Document));
            AssertForbidden(await ResourceWriteGuard.RequireWriteAsync(auth, user, resourceId, ResourceType.Runbook));
            AssertForbidden(await ResourceWriteGuard.RequireWriteAsync(auth, user, resourceId, ResourceType.Asset));
            AssertForbidden(await ResourceWriteGuard.RequireWriteAsync(auth, user, resourceId, ResourceType.KeeperLink));
        }
    }

    [Fact]
    public async Task Grant_Belonging_To_Another_Tenant_Is_Ignored()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var keeperLinkId = Guid.NewGuid();

        Guid userId;

        var (dbA, _, _) = Open(dbName, tenantA, objectId);
        await using (dbA)
        {
            userId = await SeedTenantUserAsync(dbA, tenantA, objectId, UserRole.Reader);
        }

        // Same user id, same resource id, same role — only the tenant differs.
        var (dbB, _, _) = Open(dbName, tenantB, objectId);
        await using (dbB)
        {
            await GrantAsync(dbB, userId, keeperLinkId, ResourceType.KeeperLink, UserRole.Contributor);
        }

        var (db, user, auth) = Open(dbName, tenantA, objectId);
        await using (db)
        {
            var foreignGrants = await db.ResourceRoleAssignments.CountAsync(r => r.TenantId == tenantB);
            Assert.Equal(1, foreignGrants);

            AssertForbidden(await ResourceWriteGuard.RequireWriteAsync(auth, user, keeperLinkId, ResourceType.KeeperLink));
        }
    }

    [Fact]
    public async Task Tenant_Contributor_Writes_Without_Any_Grant()
    {
        var tenantId = Guid.NewGuid();
        var objectId = Guid.NewGuid();

        var (db, user, auth) = Open(Guid.NewGuid().ToString(), tenantId, objectId);
        await using (db)
        {
            await SeedTenantUserAsync(db, tenantId, objectId, UserRole.Contributor);

            Assert.Null(await ResourceWriteGuard.RequireWriteAsync(auth, user, Guid.NewGuid(), ResourceType.Asset));
            Assert.Null(await ResourceWriteGuard.RequireWriteAsync(auth, user, Guid.NewGuid(), ResourceType.Runbook));
            Assert.Null(await ResourceWriteGuard.RequireTenantWriteAsync(auth, user, ResourceType.Document));
        }
    }

    [Fact]
    public async Task Grant_Can_Lower_A_Stored_Owner_On_One_Resource()
    {
        // The inverse of the headline case, and the reason the guard consults the service per
        // resource rather than caching a single tenant-wide answer.
        var tenantId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var lockedId = Guid.NewGuid();

        var (db, user, auth) = Open(Guid.NewGuid().ToString(), tenantId, objectId);
        await using (db)
        {
            var userId = await SeedTenantUserAsync(db, tenantId, objectId, UserRole.Owner);
            await GrantAsync(db, userId, lockedId, ResourceType.Runbook, UserRole.Reader);

            AssertForbidden(await ResourceWriteGuard.RequireWriteAsync(auth, user, lockedId, ResourceType.Runbook));
            Assert.Null(await ResourceWriteGuard.RequireWriteAsync(auth, user, Guid.NewGuid(), ResourceType.Runbook));
        }
    }

    [Fact]
    public async Task Create_Falls_Back_To_The_Tenant_Role_And_Ignores_Grants()
    {
        // Nothing exists to hold a grant at creation time. A Reader who has been granted Contributor
        // on one document still may not create new ones.
        var tenantId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        var (db, user, auth) = Open(Guid.NewGuid().ToString(), tenantId, objectId);
        await using (db)
        {
            var userId = await SeedTenantUserAsync(db, tenantId, objectId, UserRole.Reader);
            await GrantAsync(db, userId, documentId, ResourceType.Document, UserRole.Contributor);

            Assert.Null(await ResourceWriteGuard.RequireWriteAsync(auth, user, documentId, ResourceType.Document));
            AssertForbidden(await ResourceWriteGuard.RequireTenantWriteAsync(auth, user, ResourceType.Document));
        }
    }

    [Fact]
    public async Task Entra_Claim_Writes_Even_Without_A_Provisioned_User_Row()
    {
        // Regression guard, not a feature: a caller whose rank comes from an Entra app role has no
        // stored role for the resource service to read, and could write before these gates existed.
        var tenantId = Guid.NewGuid();
        var objectId = Guid.NewGuid();

        var (db, user, auth) = Open(Guid.NewGuid().ToString(), tenantId, objectId, claimRole: UserRole.Contributor);
        await using (db)
        {
            Assert.False(await db.Users.AnyAsync());

            Assert.Null(await ResourceWriteGuard.RequireWriteAsync(auth, user, Guid.NewGuid(), ResourceType.Document));
            Assert.Null(await ResourceWriteGuard.RequireTenantWriteAsync(auth, user, ResourceType.Document));
        }
    }

    [Fact]
    public async Task Caller_With_Neither_A_User_Row_Nor_A_Claim_Is_Denied()
    {
        // The one behaviour change that is not about Readers: an unprovisioned caller (a token that
        // has never hit GET /api/me) now gets a 403 on writes instead of succeeding.
        var tenantId = Guid.NewGuid();
        var objectId = Guid.NewGuid();

        var (db, user, auth) = Open(Guid.NewGuid().ToString(), tenantId, objectId);
        await using (db)
        {
            AssertForbidden(await ResourceWriteGuard.RequireWriteAsync(auth, user, Guid.NewGuid(), ResourceType.Asset));
            AssertForbidden(await ResourceWriteGuard.RequireTenantWriteAsync(auth, user, ResourceType.Asset));
        }
    }
}
