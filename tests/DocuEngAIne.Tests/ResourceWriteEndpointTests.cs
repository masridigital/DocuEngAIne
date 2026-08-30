using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

/// <summary>
/// Endpoint-level proof that a <see cref="ResourceRoleAssignment"/> is consulted on the
/// asset / document / runbook / Keeper write methods — the gap
/// <c>ResourceAuthorizationEnforcementTests</c> named and left open. Those tests exercise
/// <see cref="ResourceWriteGuard"/> in isolation; these call the same <c>PostAsync</c> /
/// <c>PutAsync</c> / <c>DeleteAsync</c> methods the routes bind, and assert the row actually
/// changed (or did not) plus the 403.
/// </summary>
/// <remarks>
/// <see cref="FakeCurrentUser.Role"/> is the Entra claim. It stays <see cref="UserRole.Reader"/>
/// so the stored role and grant path is what runs, matching production callers provisioned by
/// <c>GET /api/me</c>.
/// </remarks>
public class ResourceWriteEndpointTests
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
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId))
        {
            db.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "Tenant",
                Slug = $"tenant-{tenantId:N}",
            });
        }

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

    private static int StatusOf(IResult result)
        => result is IStatusCodeHttpResult s && s.StatusCode is int code ? code : 0;

    private static async Task<(Guid TenantId, Guid ObjectId, Guid UserId, Guid ResourceId, Guid AssetTypeId)> SeedResourceAsync(
        string dbName,
        string resourceType,
        UserRole storedRole,
        UserRole? grantRole = null)
    {
        var tenantId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var assetTypeId = Guid.NewGuid();

        var (db, _, _) = Open(dbName, tenantId, objectId);
        await using (db)
        {
            var userId = await SeedTenantUserAsync(db, tenantId, objectId, storedRole);

            db.AssetTypes.Add(new AssetType
            {
                Id = assetTypeId,
                TenantId = tenantId,
                Name = "Servers",
            });

            switch (resourceType)
            {
                case ResourceType.Document:
                    db.Documents.Add(new Document
                    {
                        Id = resourceId,
                        TenantId = tenantId,
                        Title = "Original",
                        Slug = "original",
                        Content = "original-body",
                    });
                    break;
                case ResourceType.Asset:
                    db.Assets.Add(new Asset
                    {
                        Id = resourceId,
                        TenantId = tenantId,
                        Name = "Original",
                        AssetTypeId = assetTypeId,
                    });
                    break;
                case ResourceType.Runbook:
                    db.Runbooks.Add(new Runbook
                    {
                        Id = resourceId,
                        TenantId = tenantId,
                        Title = "Original",
                        Slug = "original",
                    });
                    break;
                case ResourceType.KeeperLink:
                    db.KeeperLinks.Add(new KeeperLink
                    {
                        Id = resourceId,
                        TenantId = tenantId,
                        Name = "Original",
                        KeeperRecordUrl = "https://keeper.example/record",
                    });
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(resourceType), resourceType, null);
            }

            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            if (grantRole is UserRole granted)
                await GrantAsync(db, userId, resourceId, resourceType, granted);

            return (tenantId, objectId, userId, resourceId, assetTypeId);
        }
    }

    private static Task<IResult> PutAsync(
        string resourceType,
        Guid id,
        DocuEngAIneDbContext db,
        FakeCurrentUser user,
        ResourceAuthorizationService auth) =>
        resourceType switch
        {
            ResourceType.Document => DocumentEndpoints.PutAsync(
                id, new UpdateDocumentRequest("Renamed", null, null, null, null, null, null), db, user, auth),
            ResourceType.Asset => AssetEndpoints.PutAsync(
                id, new UpdateAssetRequest("Renamed", null, null, null, null), db, user, auth),
            ResourceType.Runbook => RunbookEndpoints.PutAsync(
                id, new UpdateRunbookRequest("Renamed", null, null, null, null, null), db, user, auth),
            ResourceType.KeeperLink => KeeperLinkEndpoints.PutAsync(
                id, new UpdateKeeperLinkRequest("Renamed", null, null, null, null, null, null), db, user, auth),
            _ => throw new ArgumentOutOfRangeException(nameof(resourceType), resourceType, null),
        };

    private static Task<IResult> DeleteAsync(
        string resourceType,
        Guid id,
        DocuEngAIneDbContext db,
        FakeCurrentUser user,
        ResourceAuthorizationService auth) =>
        resourceType switch
        {
            ResourceType.Document => DocumentEndpoints.DeleteAsync(id, db, user, auth),
            ResourceType.Asset => AssetEndpoints.DeleteAsync(id, db, user, auth),
            ResourceType.Runbook => RunbookEndpoints.DeleteAsync(id, db, user, auth),
            ResourceType.KeeperLink => KeeperLinkEndpoints.DeleteAsync(id, db, user, auth),
            _ => throw new ArgumentOutOfRangeException(nameof(resourceType), resourceType, null),
        };

    private static Task<IResult> PostAsync(
        string resourceType,
        Guid assetTypeId,
        DocuEngAIneDbContext db,
        FakeCurrentUser user,
        ResourceAuthorizationService auth) =>
        resourceType switch
        {
            ResourceType.Document => DocumentEndpoints.PostAsync(
                new CreateDocumentRequest("New", "new", null, null, null), db, user, auth),
            ResourceType.Asset => AssetEndpoints.PostAsync(
                new CreateAssetRequest("New", assetTypeId, null, null, null), db, user, auth),
            ResourceType.Runbook => RunbookEndpoints.PostAsync(
                new CreateRunbookRequest("New", "new", null, null), db, user, auth),
            ResourceType.KeeperLink => KeeperLinkEndpoints.PostAsync(
                new CreateKeeperLinkRequest("New", null, "https://keeper.example/new", null, null, null, null), db, user, auth),
            _ => throw new ArgumentOutOfRangeException(nameof(resourceType), resourceType, null),
        };

    private static async Task<string> ReadNameAsync(DocuEngAIneDbContext db, string resourceType, Guid id) =>
        resourceType switch
        {
            ResourceType.Document => (await db.Documents.AsNoTracking().SingleAsync(d => d.Id == id)).Title,
            ResourceType.Asset => (await db.Assets.AsNoTracking().SingleAsync(a => a.Id == id)).Name,
            ResourceType.Runbook => (await db.Runbooks.AsNoTracking().SingleAsync(r => r.Id == id)).Title,
            ResourceType.KeeperLink => (await db.KeeperLinks.AsNoTracking().SingleAsync(k => k.Id == id)).Name,
            _ => throw new ArgumentOutOfRangeException(nameof(resourceType), resourceType, null),
        };

    private static async Task<bool> ExistsAsync(DocuEngAIneDbContext db, string resourceType, Guid id) =>
        resourceType switch
        {
            ResourceType.Document => await db.Documents.AsNoTracking().AnyAsync(d => d.Id == id),
            ResourceType.Asset => await db.Assets.AsNoTracking().AnyAsync(a => a.Id == id),
            ResourceType.Runbook => await db.Runbooks.AsNoTracking().AnyAsync(r => r.Id == id),
            ResourceType.KeeperLink => await db.KeeperLinks.AsNoTracking().AnyAsync(k => k.Id == id),
            _ => throw new ArgumentOutOfRangeException(nameof(resourceType), resourceType, null),
        };

    [Theory]
    [InlineData(ResourceType.Document)]
    [InlineData(ResourceType.Asset)]
    [InlineData(ResourceType.Runbook)]
    [InlineData(ResourceType.KeeperLink)]
    public async Task Reader_Without_Grant_Put_Is_403_And_Leaves_The_Row(string resourceType)
    {
        var dbName = Guid.NewGuid().ToString();
        var seeded = await SeedResourceAsync(dbName, resourceType, UserRole.Reader);
        var (db, user, auth) = Open(dbName, seeded.TenantId, seeded.ObjectId);
        await using (db)
        {
            var result = await PutAsync(resourceType, seeded.ResourceId, db, user, auth);

            Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(result));
            db.ChangeTracker.Clear();
            Assert.Equal("Original", await ReadNameAsync(db, resourceType, seeded.ResourceId));
        }
    }

    [Theory]
    [InlineData(ResourceType.Document)]
    [InlineData(ResourceType.Asset)]
    [InlineData(ResourceType.Runbook)]
    [InlineData(ResourceType.KeeperLink)]
    public async Task Reader_With_Contributor_Grant_Put_Writes_The_Granted_Row(string resourceType)
    {
        var dbName = Guid.NewGuid().ToString();
        var seeded = await SeedResourceAsync(dbName, resourceType, UserRole.Reader, UserRole.Contributor);
        var (db, user, auth) = Open(dbName, seeded.TenantId, seeded.ObjectId);
        await using (db)
        {
            var result = await PutAsync(resourceType, seeded.ResourceId, db, user, auth);

            Assert.Equal(StatusCodes.Status204NoContent, StatusOf(result));
            db.ChangeTracker.Clear();
            Assert.Equal("Renamed", await ReadNameAsync(db, resourceType, seeded.ResourceId));
        }
    }

    [Theory]
    [InlineData(ResourceType.Document)]
    [InlineData(ResourceType.Asset)]
    [InlineData(ResourceType.Runbook)]
    [InlineData(ResourceType.KeeperLink)]
    public async Task Reader_Without_Grant_Delete_Is_403_And_Leaves_The_Row(string resourceType)
    {
        var dbName = Guid.NewGuid().ToString();
        var seeded = await SeedResourceAsync(dbName, resourceType, UserRole.Reader);
        var (db, user, auth) = Open(dbName, seeded.TenantId, seeded.ObjectId);
        await using (db)
        {
            var result = await DeleteAsync(resourceType, seeded.ResourceId, db, user, auth);

            Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(result));
            db.ChangeTracker.Clear();
            Assert.True(await ExistsAsync(db, resourceType, seeded.ResourceId));
        }
    }

    [Theory]
    [InlineData(ResourceType.Document)]
    [InlineData(ResourceType.Asset)]
    [InlineData(ResourceType.Runbook)]
    [InlineData(ResourceType.KeeperLink)]
    public async Task Reader_With_Contributor_Grant_Delete_Removes_The_Granted_Row(string resourceType)
    {
        var dbName = Guid.NewGuid().ToString();
        var seeded = await SeedResourceAsync(dbName, resourceType, UserRole.Reader, UserRole.Contributor);
        var (db, user, auth) = Open(dbName, seeded.TenantId, seeded.ObjectId);
        await using (db)
        {
            var result = await DeleteAsync(resourceType, seeded.ResourceId, db, user, auth);

            Assert.Equal(StatusCodes.Status204NoContent, StatusOf(result));
            db.ChangeTracker.Clear();
            Assert.False(await ExistsAsync(db, resourceType, seeded.ResourceId));
        }
    }

    [Theory]
    [InlineData(ResourceType.Document)]
    [InlineData(ResourceType.Asset)]
    [InlineData(ResourceType.Runbook)]
    [InlineData(ResourceType.KeeperLink)]
    public async Task Reader_Post_Is_403_Even_With_A_Grant_On_Another_Row(string resourceType)
    {
        var dbName = Guid.NewGuid().ToString();
        var seeded = await SeedResourceAsync(dbName, resourceType, UserRole.Reader, UserRole.Contributor);
        var (db, user, auth) = Open(dbName, seeded.TenantId, seeded.ObjectId);
        await using (db)
        {
            var result = await PostAsync(resourceType, seeded.AssetTypeId, db, user, auth);

            Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(result));
        }
    }

    [Theory]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.Owner)]
    public async Task Tenant_Admin_Or_Owner_Puts_A_Document_Without_A_Grant(UserRole storedRole)
    {
        var dbName = Guid.NewGuid().ToString();
        var seeded = await SeedResourceAsync(dbName, ResourceType.Document, storedRole);
        var (db, user, auth) = Open(dbName, seeded.TenantId, seeded.ObjectId);
        await using (db)
        {
            var result = await DocumentEndpoints.PutAsync(
                seeded.ResourceId,
                new UpdateDocumentRequest("Renamed", null, null, null, null, null, null),
                db,
                user,
                auth);

            Assert.Equal(StatusCodes.Status204NoContent, StatusOf(result));
            db.ChangeTracker.Clear();
            Assert.Equal("Renamed", (await db.Documents.AsNoTracking().SingleAsync(d => d.Id == seeded.ResourceId)).Title);
        }
    }

    [Fact]
    public async Task Contributor_Grant_On_One_Document_Does_Not_Unlock_Another()
    {
        var dbName = Guid.NewGuid().ToString();
        var seeded = await SeedResourceAsync(dbName, ResourceType.Document, UserRole.Reader, UserRole.Contributor);
        var otherId = Guid.NewGuid();

        var (seed, _, _) = Open(dbName, seeded.TenantId, seeded.ObjectId);
        await using (seed)
        {
            seed.Documents.Add(new Document
            {
                Id = otherId,
                TenantId = seeded.TenantId,
                Title = "Other",
                Slug = "other",
            });
            await seed.SaveChangesAsync();
        }

        var (db, user, auth) = Open(dbName, seeded.TenantId, seeded.ObjectId);
        await using (db)
        {
            var granted = await DocumentEndpoints.PutAsync(
                seeded.ResourceId,
                new UpdateDocumentRequest("Renamed", null, null, null, null, null, null),
                db,
                user,
                auth);
            var locked = await DocumentEndpoints.PutAsync(
                otherId,
                new UpdateDocumentRequest("Leaked", null, null, null, null, null, null),
                db,
                user,
                auth);

            Assert.Equal(StatusCodes.Status204NoContent, StatusOf(granted));
            Assert.Equal(StatusCodes.Status403Forbidden, StatusOf(locked));
            db.ChangeTracker.Clear();
            Assert.Equal("Renamed", (await db.Documents.AsNoTracking().SingleAsync(d => d.Id == seeded.ResourceId)).Title);
            Assert.Equal("Other", (await db.Documents.AsNoTracking().SingleAsync(d => d.Id == otherId)).Title);
        }
    }
}
