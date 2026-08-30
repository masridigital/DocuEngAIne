using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

public class RecentsTests
{
    private static (DocuEngAIneDbContext Db, FakeCurrentUser User) Open(string dbName, Guid tenantId)
    {
        var user = new FakeCurrentUser
        {
            TenantId = tenantId,
            ObjectId = Guid.NewGuid().ToString(),
            Role = UserRole.Owner,
        };
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return (new DocuEngAIneDbContext(options, user), user);
    }

    private static async Task<(Guid TenantA, Guid TenantB, Guid CompanyA, Guid CompanyB, string DbName)> SeedAsync()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var companyA = new Company { TenantId = tenantA, Name = "ExampleCo", Slug = "exampleco" };
        var companyB = new Company { TenantId = tenantB, Name = "PoisonCo", Slug = "poisonco" };
        var typeA = new AssetType { TenantId = tenantA, Name = "Servers" };
        var typeB = new AssetType { TenantId = tenantB, Name = "PoisonType" };

        var (dbA, _) = Open(dbName, tenantA);
        await using (dbA)
        {
            dbA.Companies.Add(companyA);
            dbA.AssetTypes.Add(typeA);
            await dbA.SaveChangesAsync();

            dbA.Assets.Add(new Asset
            {
                TenantId = tenantA,
                Name = "Firewall",
                AssetTypeId = typeA.Id,
                CompanyId = companyA.Id,
            });
            dbA.Documents.Add(new Document
            {
                TenantId = tenantA,
                Title = "VPN SOP",
                CompanyId = companyA.Id,
            });
            dbA.Runbooks.Add(new Runbook
            {
                TenantId = tenantA,
                Title = "Onboard ExampleCo",
                CompanyId = companyA.Id,
            });
            await dbA.SaveChangesAsync();
        }

        var (dbB, _) = Open(dbName, tenantB);
        await using (dbB)
        {
            dbB.Companies.Add(companyB);
            dbB.AssetTypes.Add(typeB);
            await dbB.SaveChangesAsync();

            dbB.Assets.Add(new Asset
            {
                TenantId = tenantB,
                Name = "Poison Host",
                AssetTypeId = typeB.Id,
                CompanyId = companyB.Id,
            });
            dbB.Documents.Add(new Document
            {
                TenantId = tenantB,
                Title = "Poison Playbook",
                CompanyId = companyB.Id,
            });
            dbB.Runbooks.Add(new Runbook
            {
                TenantId = tenantB,
                Title = "Poison SOP",
                CompanyId = companyB.Id,
            });
            await dbB.SaveChangesAsync();
        }

        return (tenantA, tenantB, companyA.Id, companyB.Id, dbName);
    }

    [Fact]
    public async Task ListRecents_Returns_Tenant_Assets_Docs_And_Runbooks()
    {
        var (tenantA, _, companyA, _, dbName) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var items = await ProfileEndpoints.ListRecentsAsync(db, user);
            Assert.Equal(3, items.Count);
            Assert.Contains(items, i => i.EntityType == FlagEntityType.Asset && i.Name == "Firewall" && i.CompanyName == "ExampleCo");
            Assert.Contains(items, i => i.EntityType == FlagEntityType.Document && i.Name == "VPN SOP" && i.CompanyId == companyA);
            Assert.Contains(items, i => i.EntityType == FlagEntityType.Runbook && i.Name == "Onboard ExampleCo");
            Assert.DoesNotContain(items, i => i.Name.Contains("Poison", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(items, i => i.CompanyName == "PoisonCo");
        }
    }

    [Fact]
    public async Task ListRecents_Does_Not_Leak_Other_Tenant()
    {
        var (_, tenantB, _, companyB, dbName) = await SeedAsync();
        var (db, user) = Open(dbName, tenantB);
        await using (db)
        {
            var items = await ProfileEndpoints.ListRecentsAsync(db, user);
            Assert.Equal(3, items.Count);
            Assert.All(items, i => Assert.Equal(companyB, i.CompanyId));
            Assert.All(items, i => Assert.Equal("PoisonCo", i.CompanyName));
            Assert.DoesNotContain(items, i => i.Name is "Firewall" or "VPN SOP" or "Onboard ExampleCo");
        }
    }

    [Fact]
    public async Task ListRecents_Other_Tenant_Without_Rows_Is_Empty()
    {
        var (tenantA, _, _, _, dbName) = await SeedAsync();
        var emptyTenant = Guid.NewGuid();
        var (db, user) = Open(dbName, emptyTenant);
        await using (db)
        {
            var items = await ProfileEndpoints.ListRecentsAsync(db, user);
            Assert.Empty(items);

            var (dbA, userA) = Open(dbName, tenantA);
            await using (dbA)
            {
                var tenantAItems = await ProfileEndpoints.ListRecentsAsync(dbA, userA);
                Assert.NotEmpty(tenantAItems);
            }
        }
    }

    [Fact]
    public async Task ListRecents_Caps_At_Ten_Most_Recently_Updated()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantId = Guid.NewGuid();
        var (db, user) = Open(dbName, tenantId);
        await using (db)
        {
            var type = new AssetType { TenantId = tenantId, Name = "Hosts" };
            db.AssetTypes.Add(type);
            await db.SaveChangesAsync();

            var names = new List<string>();
            for (var i = 0; i < 13; i++)
            {
                var name = $"Host {i:D2}";
                db.Assets.Add(new Asset { TenantId = tenantId, Name = name, AssetTypeId = type.Id });
                await db.SaveChangesAsync();
                names.Add(name);
                await Task.Delay(20);
            }

            var items = await ProfileEndpoints.ListRecentsAsync(db, user);
            Assert.Equal(ProfileEndpoints.RecentTake, items.Count);
            Assert.Equal(names[12], items[0].Name);
            Assert.DoesNotContain(items, i => i.Name == names[0] || i.Name == names[1] || i.Name == names[2]);
            Assert.True(items.Zip(items.Skip(1), (a, b) => a.UpdatedAt >= b.UpdatedAt).All(ordered => ordered));
        }
    }
}
