using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
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

    private static async Task AssertCompanyNotFound(IResult? result)
    {
        Assert.NotNull(result);
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, status.StatusCode);
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result);
        Assert.Equal("Company not found.", value.Value);
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

    [Fact]
    public async Task Cannot_Attach_CompanyId_From_Another_Tenant_To_Document_Runbook_Or_KeeperLink()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var companyA = new Company { TenantId = tenantA, Name = "A Co", Slug = "a-co" };
        var companyB = new Company { TenantId = tenantB, Name = "B Co", Slug = "b-co" };

        var (seedA, _) = Open(dbName, tenantA);
        await using (seedA)
        {
            seedA.Companies.Add(companyA);
            await seedA.SaveChangesAsync();
        }

        var (seedB, _) = Open(dbName, tenantB);
        await using (seedB)
        {
            seedB.Companies.Add(companyB);
            await seedB.SaveChangesAsync();
        }

        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            await AssertCompanyNotFound(await CompanyEndpoints.EnsureCompanyInTenantAsync(db, user, companyB.Id));
            await AssertCompanyNotFound(await CompanyEndpoints.EnsureCompanyInTenantAsync(db, user, Guid.NewGuid()));

            var own = await CompanyEndpoints.EnsureCompanyInTenantAsync(db, user, companyA.Id);
            Assert.Null(own);
            var omitted = await CompanyEndpoints.EnsureCompanyInTenantAsync(db, user, null);
            Assert.Null(omitted);

            var createDoc = new CreateDocumentRequest("Doc", "doc", null, null, null, true, companyB.Id);
            await AssertCompanyNotFound(await CompanyEndpoints.EnsureCompanyInTenantAsync(db, user, createDoc.CompanyId));

            var createRunbook = new CreateRunbookRequest("SOP", "sop", null, null, true, null, companyB.Id);
            await AssertCompanyNotFound(await CompanyEndpoints.EnsureCompanyInTenantAsync(db, user, createRunbook.CompanyId));

            var createKeeper = new CreateKeeperLinkRequest("Vault", null, "https://keeper.example/x", null, null, null, null, companyB.Id);
            await AssertCompanyNotFound(await CompanyEndpoints.EnsureCompanyInTenantAsync(db, user, createKeeper.CompanyId));

            var doc = new Document { TenantId = tenantA, Title = "Existing", Slug = "existing" };
            var runbook = new Runbook { TenantId = tenantA, Title = "Existing SOP", Slug = "existing-sop" };
            var keeper = new KeeperLink { TenantId = tenantA, Name = "Existing vault", KeeperRecordUrl = "https://keeper.example/y" };
            db.Documents.Add(doc);
            db.Runbooks.Add(runbook);
            db.KeeperLinks.Add(keeper);
            await db.SaveChangesAsync();

            var updateDoc = new UpdateDocumentRequest(null, null, null, null, null, null, null, companyB.Id);
            await AssertCompanyNotFound(await CompanyEndpoints.EnsureCompanyInTenantAsync(db, user, updateDoc.CompanyId));
            var updateRunbook = new UpdateRunbookRequest(null, null, null, null, null, null, companyB.Id);
            await AssertCompanyNotFound(await CompanyEndpoints.EnsureCompanyInTenantAsync(db, user, updateRunbook.CompanyId));
            var updateKeeper = new UpdateKeeperLinkRequest(null, null, null, null, null, null, null, companyB.Id);
            await AssertCompanyNotFound(await CompanyEndpoints.EnsureCompanyInTenantAsync(db, user, updateKeeper.CompanyId));

            Assert.Null(doc.CompanyId);
            Assert.Null(runbook.CompanyId);
            Assert.Null(keeper.CompanyId);
        }
    }

    [Fact]
    public async Task Own_Tenant_CompanyId_Attaches_To_Document_Runbook_And_KeeperLink()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var companyA = new Company { TenantId = tenantA, Name = "A Co", Slug = "a-co" };

        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            db.Companies.Add(companyA);
            await db.SaveChangesAsync();

            Assert.Null(await CompanyEndpoints.EnsureCompanyInTenantAsync(db, user, companyA.Id));

            db.Documents.Add(new Document { TenantId = tenantA, Title = "Doc", Slug = "doc", CompanyId = companyA.Id });
            db.Runbooks.Add(new Runbook { TenantId = tenantA, Title = "SOP", Slug = "sop", CompanyId = companyA.Id });
            db.KeeperLinks.Add(new KeeperLink { TenantId = tenantA, Name = "Vault", CompanyId = companyA.Id, KeeperRecordUrl = "https://keeper.example/a" });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var doc = await db.Documents.ForTenant(user).AsNoTracking().SingleAsync();
            var runbook = await db.Runbooks.ForTenant(user).AsNoTracking().SingleAsync();
            var keeper = await db.KeeperLinks.ForTenant(user).AsNoTracking().SingleAsync();
            Assert.Equal(companyA.Id, doc.CompanyId);
            Assert.Equal(companyA.Id, runbook.CompanyId);
            Assert.Equal(companyA.Id, keeper.CompanyId);
        }
    }
}
