using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

public class RbacTests
{
    private static (DocuEngAIneDbContext Db, Guid TenantId, Guid UserId, ResourceAuthorizationService Auth) CreateContext(UserRole tenantRole)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var currentUser = new FakeCurrentUser
        {
            TenantId = tenantId,
            ObjectId = userId.ToString(),
            Role = tenantRole,
        };

        var db = new DocuEngAIneDbContext(options, currentUser);
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Test", Slug = "test" });
        db.Users.Add(new User
        {
            Id = userId,
            TenantId = tenantId,
            EntraObjectId = userId.ToString(),
            Email = "test@example.com",
            Role = tenantRole,
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();

        var auth = new ResourceAuthorizationService(db, currentUser);
        return (db, tenantId, userId, auth);
    }

    [Fact]
    public async Task No_Assignment_Falls_Back_To_Tenant_Role()
    {
        var (_, _, _, auth) = CreateContext(UserRole.Admin);
        var resourceId = Guid.NewGuid();

        var role = await auth.GetEffectiveRoleAsync(resourceId, ResourceType.Asset);

        Assert.Equal(UserRole.Admin, role);
        Assert.True(await auth.CanAdminAsync(resourceId, ResourceType.Asset));
    }

    [Fact]
    public async Task Resource_Assignment_Overrides_Tenant_Role()
    {
        var (db, tenantId, userId, auth) = CreateContext(UserRole.Owner);
        var resourceId = Guid.NewGuid();

        db.ResourceRoleAssignments.Add(new ResourceRoleAssignment
        {
            TenantId = tenantId,
            UserId = userId,
            ResourceType = ResourceType.Asset,
            ResourceId = resourceId,
            Role = UserRole.Reader,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var role = await auth.GetEffectiveRoleAsync(resourceId, ResourceType.Asset);

        Assert.Equal(UserRole.Reader, role);
        Assert.True(await auth.CanReadAsync(resourceId, ResourceType.Asset));
        Assert.False(await auth.CanWriteAsync(resourceId, ResourceType.Asset));
    }

    [Fact]
    public async Task Enforce_Throws_When_Role_Insufficient()
    {
        var (db, tenantId, userId, auth) = CreateContext(UserRole.Reader);
        var resourceId = Guid.NewGuid();

        db.ResourceRoleAssignments.Add(new ResourceRoleAssignment
        {
            TenantId = tenantId,
            UserId = userId,
            ResourceType = ResourceType.Document,
            ResourceId = resourceId,
            Role = UserRole.Reader,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            auth.EnforceAsync(resourceId, ResourceType.Document, UserRole.Contributor));
    }
}
