using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

public class TenantIsolationTests
{
    private static DocuEngAIneDbContext CreateContext(Guid tenantId, UserRole role = UserRole.Owner, string? objectId = null)
    {
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var user = new FakeCurrentUser { TenantId = tenantId, ObjectId = objectId ?? Guid.NewGuid().ToString(), Role = role };
        return new DocuEngAIneDbContext(options, user);
    }

    [Fact]
    public async Task ForTenant_Returns_Only_Current_Tenant_Assets()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await using var contextA = CreateContext(tenantA);
        await using var contextB = CreateContext(tenantB);

        contextA.Assets.Add(new Asset
        {
            TenantId = tenantA,
            Name = "Tenant A Server",
            AssetTypeId = Guid.NewGuid(),
        });

        contextB.Assets.Add(new Asset
        {
            TenantId = tenantB,
            Name = "Tenant B Server",
            AssetTypeId = Guid.NewGuid(),
        });

        await contextA.SaveChangesAsync();
        await contextB.SaveChangesAsync();

        var assetsForA = await contextA.Assets.ForTenant(new FakeCurrentUser { TenantId = tenantA }).ToListAsync();

        Assert.Single(assetsForA);
        Assert.Equal("Tenant A Server", assetsForA[0].Name);
    }

    [Fact]
    public async Task SaveChanges_Sets_TenantId_For_New_Entity()
    {
        var tenantId = Guid.NewGuid();
        await using var context = CreateContext(tenantId);

        var asset = new Asset
        {
            Name = "Switch",
            AssetTypeId = Guid.NewGuid(),
        };

        context.Assets.Add(asset);
        await context.SaveChangesAsync();

        Assert.Equal(tenantId, asset.TenantId);
    }
}
