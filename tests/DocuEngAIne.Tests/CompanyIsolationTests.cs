using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

public class CompanyIsolationTests
{
    private static DocuEngAIneDbContext CreateContext(Guid tenantId)
    {
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new DocuEngAIneDbContext(options, new FakeCurrentUser { TenantId = tenantId, ObjectId = Guid.NewGuid().ToString(), Role = UserRole.Owner });
    }

    private static (DocuEngAIneDbContext Db, FakeCurrentUser User) Open(string dbName, Guid tenantId)
    {
        var user = new FakeCurrentUser { TenantId = tenantId, ObjectId = Guid.NewGuid().ToString(), Role = UserRole.Owner };
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return (new DocuEngAIneDbContext(options, user), user);
    }

    [Fact]
    public async Task ForTenant_Returns_Only_Current_Tenant_Companies()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await using var a = CreateContext(tenantA);
        await using var b = CreateContext(tenantB);

        a.Companies.Add(new Company { TenantId = tenantA, Name = "A Co", Slug = "a-co" });
        b.Companies.Add(new Company { TenantId = tenantB, Name = "B Co", Slug = "b-co" });
        await a.SaveChangesAsync();
        await b.SaveChangesAsync();

        var forA = await a.Companies.ForTenant(new FakeCurrentUser { TenantId = tenantA }).ToListAsync();
        Assert.Single(forA);
        Assert.Equal("A Co", forA[0].Name);
    }

    [Fact]
    public async Task Company_Summary_Does_Not_Leak_Other_Tenant_Related_Rows()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var companyA = new Company { TenantId = tenantA, Name = "A Co", Slug = "a-co" };
        var companyB = new Company { TenantId = tenantB, Name = "B Co", Slug = "b-co" };
        var typeId = Guid.NewGuid();

        var (dbA, userA) = Open(dbName, tenantA);
        await using (dbA)
        {
            dbA.Companies.Add(companyA);
            dbA.Assets.Add(new Asset { TenantId = tenantA, Name = "A-Server", AssetTypeId = typeId, CompanyId = companyA.Id });
            dbA.Documents.Add(new Document { TenantId = tenantA, Title = "A-Runbook-Doc", Slug = "a-doc", CompanyId = companyA.Id });
            dbA.Runbooks.Add(new Runbook { TenantId = tenantA, Title = "A-SOP", Slug = "a-sop", CompanyId = companyA.Id });
            dbA.KeeperLinks.Add(new KeeperLink { TenantId = tenantA, Name = "A-Vault", CompanyId = companyA.Id, KeeperRecordUrl = "https://keeper.example/a" });
            await dbA.SaveChangesAsync();
        }

        var (dbB, userB) = Open(dbName, tenantB);
        await using (dbB)
        {
            dbB.Companies.Add(companyB);
            dbB.Assets.Add(new Asset { TenantId = tenantB, Name = "B-Server", AssetTypeId = typeId, CompanyId = companyB.Id });
            dbB.Assets.Add(new Asset { TenantId = tenantB, Name = "Poison-Asset", AssetTypeId = typeId, CompanyId = companyA.Id });
            dbB.Documents.Add(new Document { TenantId = tenantB, Title = "B-Doc", Slug = "b-doc", CompanyId = companyB.Id });
            dbB.Documents.Add(new Document { TenantId = tenantB, Title = "Poison-Doc", Slug = "poison-doc", CompanyId = companyA.Id });
            dbB.Runbooks.Add(new Runbook { TenantId = tenantB, Title = "B-SOP", Slug = "b-sop", CompanyId = companyB.Id });
            dbB.KeeperLinks.Add(new KeeperLink { TenantId = tenantB, Name = "B-Vault", CompanyId = companyB.Id });
            dbB.KeeperLinks.Add(new KeeperLink { TenantId = tenantB, Name = "Poison-Vault", CompanyId = companyA.Id });
            await dbB.SaveChangesAsync();
        }

        var (queryA, queryUserA) = Open(dbName, tenantA);
        await using (queryA)
        {
            var hidden = await queryA.Companies.ForTenant(queryUserA).FirstOrDefaultAsync(c => c.Id == companyB.Id);
            Assert.Null(hidden);

            var related = await CompanyEndpoints.LoadRelatedAsync(queryA, queryUserA, companyA.Id);

            Assert.Equal(1, related.AssetCount);
            Assert.Equal(["A-Server"], related.Assets.Select(i => i.Name).ToArray());
            Assert.Equal(1, related.DocumentCount);
            Assert.Equal(["A-Runbook-Doc"], related.Documents.Select(i => i.Name).ToArray());
            Assert.Equal(1, related.RunbookCount);
            Assert.Equal(["A-SOP"], related.Runbooks.Select(i => i.Name).ToArray());
            Assert.Equal(1, related.KeeperLinkCount);
            Assert.Equal(["A-Vault"], related.KeeperLinks.Select(i => i.Name).ToArray());

            var asB = await CompanyEndpoints.LoadRelatedAsync(queryA, queryUserA, companyB.Id);
            Assert.Equal(0, asB.AssetCount);
            Assert.Equal(0, asB.DocumentCount);
            Assert.Equal(0, asB.RunbookCount);
            Assert.Equal(0, asB.KeeperLinkCount);
        }

        var (queryB, queryUserB) = Open(dbName, tenantB);
        await using (queryB)
        {
            var relatedB = await CompanyEndpoints.LoadRelatedAsync(queryB, queryUserB, companyB.Id);
            Assert.Equal(1, relatedB.AssetCount);
            Assert.Equal("B-Server", relatedB.Assets[0].Name);
            Assert.DoesNotContain(relatedB.Assets, i => i.Name.StartsWith("A-"));
            Assert.DoesNotContain(relatedB.Documents, i => i.Name.StartsWith("A-"));
        }
    }
}
