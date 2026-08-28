using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

public class LinkTests
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

    private static void AssertBadRequest(IResult result, string message)
    {
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, status.StatusCode);
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result);
        Assert.Equal(message, value.Value);
    }

    private static async Task<(
        Guid TenantA,
        Guid TenantB,
        Guid CompanyA,
        Guid CompanyB,
        Guid AssetA,
        Guid AssetB,
        Guid DocumentA,
        Guid DocumentB,
        Guid RunbookA,
        Guid RunbookB,
        Guid KeeperA,
        Guid KeeperB,
        string DbName)> SeedAsync()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var companyA = new Company { TenantId = tenantA, Name = "ExampleCo", Slug = "exampleco" };
        var companyB = new Company { TenantId = tenantB, Name = "PoisonCo", Slug = "poisonco" };
        var typeA = new AssetType { TenantId = tenantA, Name = "Servers" };
        var typeB = new AssetType { TenantId = tenantB, Name = "PoisonType" };

        Asset assetA;
        Asset assetB;
        Document documentA;
        Document documentB;
        Runbook runbookA;
        Runbook runbookB;
        KeeperLink keeperA;
        KeeperLink keeperB;

        var (dbA, _) = Open(dbName, tenantA);
        await using (dbA)
        {
            dbA.Companies.Add(companyA);
            dbA.AssetTypes.Add(typeA);
            await dbA.SaveChangesAsync();

            assetA = new Asset { TenantId = tenantA, Name = "Firewall", AssetTypeId = typeA.Id, CompanyId = companyA.Id };
            documentA = new Document { TenantId = tenantA, Title = "A-Doc", Slug = "a-doc", CompanyId = companyA.Id };
            runbookA = new Runbook { TenantId = tenantA, Title = "A-SOP", Slug = "a-sop", CompanyId = companyA.Id };
            keeperA = new KeeperLink { TenantId = tenantA, Name = "A-Vault", CompanyId = companyA.Id, KeeperRecordUrl = "https://keeper.example/a" };
            dbA.Assets.Add(assetA);
            dbA.Documents.Add(documentA);
            dbA.Runbooks.Add(runbookA);
            dbA.KeeperLinks.Add(keeperA);
            await dbA.SaveChangesAsync();
        }

        var (dbB, _) = Open(dbName, tenantB);
        await using (dbB)
        {
            dbB.Companies.Add(companyB);
            dbB.AssetTypes.Add(typeB);
            await dbB.SaveChangesAsync();

            assetB = new Asset { TenantId = tenantB, Name = "Poison SSL", AssetTypeId = typeB.Id, CompanyId = companyB.Id };
            documentB = new Document { TenantId = tenantB, Title = "Poison-Doc", Slug = "poison-doc", CompanyId = companyB.Id };
            runbookB = new Runbook { TenantId = tenantB, Title = "Poison-SOP", Slug = "poison-sop", CompanyId = companyB.Id };
            keeperB = new KeeperLink { TenantId = tenantB, Name = "Poison-Vault", CompanyId = companyB.Id, KeeperRecordUrl = "https://keeper.example/b" };
            dbB.Assets.Add(assetB);
            dbB.Documents.Add(documentB);
            dbB.Runbooks.Add(runbookB);
            dbB.KeeperLinks.Add(keeperB);
            await dbB.SaveChangesAsync();
        }

        return (tenantA, tenantB, companyA.Id, companyB.Id, assetA.Id, assetB.Id, documentA.Id, documentB.Id, runbookA.Id, runbookB.Id, keeperA.Id, keeperB.Id, dbName);
    }

    [Fact]
    public async Task ForTenant_Does_Not_Leak_Other_Tenant_Links()
    {
        var (tenantA, tenantB, companyA, companyB, assetA, assetB, _, _, _, _, _, _, dbName) = await SeedAsync();

        var (dbA, userA) = Open(dbName, tenantA);
        await using (dbA)
        {
            var created = await LinkEndpoints.CreateAsync(
                new CreateResourceLinkRequest(LinkEntityType.Company, companyA, LinkEntityType.Asset, assetA, "edge"),
                dbA,
                userA);
            Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<IStatusCodeHttpResult>(created).StatusCode);
        }

        var (dbB, userB) = Open(dbName, tenantB);
        await using (dbB)
        {
            var created = await LinkEndpoints.CreateAsync(
                new CreateResourceLinkRequest(LinkEntityType.Company, companyB, LinkEntityType.Asset, assetB, "poison-edge"),
                dbB,
                userB);
            Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<IStatusCodeHttpResult>(created).StatusCode);
        }

        var (queryA, queryUserA) = Open(dbName, tenantA);
        await using (queryA)
        {
            var listed = await LinkEndpoints.ListForEntityAsync(queryA, queryUserA, LinkEntityType.Company, companyA);
            Assert.Single(listed);
            Assert.Equal("Firewall", listed[0].ToName);
            Assert.Equal("edge", listed[0].Label);
            Assert.DoesNotContain(listed, l => l.ToName.Contains("Poison", StringComparison.OrdinalIgnoreCase));

            var hidden = await queryA.ResourceLinks.ForTenant(queryUserA)
                .FirstOrDefaultAsync(l => l.FromId == companyB || l.ToId == assetB);
            Assert.Null(hidden);

            var fromB = await LinkEndpoints.ListForEntityAsync(queryA, queryUserA, LinkEntityType.Company, companyB);
            Assert.Empty(fromB);
        }

        var (queryB, queryUserB) = Open(dbName, tenantB);
        await using (queryB)
        {
            var listed = await LinkEndpoints.ListForEntityAsync(queryB, queryUserB, LinkEntityType.Asset, assetB);
            Assert.Single(listed);
            Assert.Equal("PoisonCo", listed[0].FromName);
            Assert.DoesNotContain(listed, l => l.FromName == "ExampleCo");
        }
    }

    [Fact]
    public async Task Cannot_Link_Other_Tenant_Entities()
    {
        var (tenantA, _, companyA, companyB, _, assetB, _, documentB, _, runbookB, _, keeperB, dbName) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            AssertBadRequest(
                await LinkEndpoints.CreateAsync(
                    new CreateResourceLinkRequest(LinkEntityType.Company, companyA, LinkEntityType.Company, companyB),
                    db, user),
                LinkEndpoints.EntityNotFoundMessage);
            AssertBadRequest(
                await LinkEndpoints.CreateAsync(
                    new CreateResourceLinkRequest(LinkEntityType.Company, companyA, LinkEntityType.Asset, assetB),
                    db, user),
                LinkEndpoints.EntityNotFoundMessage);
            AssertBadRequest(
                await LinkEndpoints.CreateAsync(
                    new CreateResourceLinkRequest(LinkEntityType.Company, companyA, LinkEntityType.Document, documentB),
                    db, user),
                LinkEndpoints.EntityNotFoundMessage);
            AssertBadRequest(
                await LinkEndpoints.CreateAsync(
                    new CreateResourceLinkRequest(LinkEntityType.Company, companyA, LinkEntityType.Runbook, runbookB),
                    db, user),
                LinkEndpoints.EntityNotFoundMessage);
            AssertBadRequest(
                await LinkEndpoints.CreateAsync(
                    new CreateResourceLinkRequest(LinkEntityType.Company, companyA, LinkEntityType.KeeperLink, keeperB),
                    db, user),
                LinkEndpoints.EntityNotFoundMessage);
            AssertBadRequest(
                await LinkEndpoints.CreateAsync(
                    new CreateResourceLinkRequest(LinkEntityType.Company, companyB, LinkEntityType.Asset, assetB),
                    db, user),
                LinkEndpoints.EntityNotFoundMessage);
            AssertBadRequest(
                await LinkEndpoints.CreateAsync(
                    new CreateResourceLinkRequest(LinkEntityType.Company, companyA, LinkEntityType.Asset, Guid.NewGuid()),
                    db, user),
                LinkEndpoints.EntityNotFoundMessage);

            Assert.Empty(await db.ResourceLinks.ForTenant(user).ToListAsync());
        }
    }

    [Fact]
    public async Task Create_List_Delete_Own_Tenant_Links_From_Or_To()
    {
        var (tenantA, _, companyA, _, assetA, _, documentA, _, runbookA, _, keeperA, _, dbName) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var created = await LinkEndpoints.CreateAsync(
                new CreateResourceLinkRequest(LinkEntityType.Company, companyA, LinkEntityType.Document, documentA, "runbook notes"),
                db, user);
            Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<IStatusCodeHttpResult>(created).StatusCode);

            var createdAsset = await LinkEndpoints.CreateAsync(
                new CreateResourceLinkRequest(LinkEntityType.Asset, assetA, LinkEntityType.Company, companyA),
                db, user);
            Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<IStatusCodeHttpResult>(createdAsset).StatusCode);

            await LinkEndpoints.CreateAsync(
                new CreateResourceLinkRequest(LinkEntityType.Runbook, runbookA, LinkEntityType.KeeperLink, keeperA),
                db, user);

            var fromCompany = await LinkEndpoints.ListForEntityAsync(db, user, LinkEntityType.Company, companyA);
            Assert.Equal(2, fromCompany.Count);
            Assert.Contains(fromCompany, l => l.ToType == LinkEntityType.Document && l.ToName == "A-Doc");
            Assert.Contains(fromCompany, l => l.FromType == LinkEntityType.Asset && l.FromName == "Firewall");

            var related = await LinkEndpoints.LoadRelatedForEntityAsync(db, user, LinkEntityType.Company, companyA);
            Assert.Equal(2, related.Count);
            Assert.Contains(related.Items, i => i.EntityType == LinkEntityType.Document && i.Name == "A-Doc");
            Assert.Contains(related.Items, i => i.EntityType == LinkEntityType.Asset && i.Name == "Firewall");

            var snapshot = await CompanyEndpoints.LoadRelatedAsync(db, user, companyA);
            Assert.Equal(2, snapshot.RelatedLinkCount);
            Assert.Equal(2, snapshot.RelatedLinks.Count);

            var duplicate = await LinkEndpoints.CreateAsync(
                new CreateResourceLinkRequest(LinkEntityType.Company, companyA, LinkEntityType.Document, documentA),
                db, user);
            var dupStatus = Assert.IsAssignableFrom<IStatusCodeHttpResult>(duplicate);
            Assert.Equal(StatusCodes.Status409Conflict, dupStatus.StatusCode);

            var self = await LinkEndpoints.CreateAsync(
                new CreateResourceLinkRequest(LinkEntityType.Company, companyA, LinkEntityType.Company, companyA),
                db, user);
            AssertBadRequest(self, LinkEndpoints.SelfLinkMessage);

            var unknown = await LinkEndpoints.CreateAsync(
                new CreateResourceLinkRequest("Widget", companyA, LinkEntityType.Asset, assetA),
                db, user);
            AssertBadRequest(unknown, LinkEndpoints.UnknownEntityTypeMessage);

            var toDelete = fromCompany.First(l => l.ToType == LinkEntityType.Document);
            var deleted = await LinkEndpoints.DeleteAsync(toDelete.Id, db, user);
            Assert.Equal(StatusCodes.Status204NoContent, Assert.IsAssignableFrom<IStatusCodeHttpResult>(deleted).StatusCode);
            Assert.Equal(2, await db.ResourceLinks.ForTenant(user).CountAsync());
        }
    }

    [Fact]
    public async Task Delete_Other_Tenant_Link_Is_NotFound()
    {
        var (tenantA, tenantB, companyA, companyB, assetA, assetB, _, _, _, _, _, _, dbName) = await SeedAsync();

        Guid poisonId;
        var (dbB, userB) = Open(dbName, tenantB);
        await using (dbB)
        {
            var created = await LinkEndpoints.CreateAsync(
                new CreateResourceLinkRequest(LinkEntityType.Company, companyB, LinkEntityType.Asset, assetB),
                dbB, userB);
            var value = Assert.IsAssignableFrom<IValueHttpResult>(created);
            var item = Assert.IsType<ResourceLinkItem>(value.Value);
            poisonId = item.Id;
        }

        var (dbA, userA) = Open(dbName, tenantA);
        await using (dbA)
        {
            await LinkEndpoints.CreateAsync(
                new CreateResourceLinkRequest(LinkEntityType.Company, companyA, LinkEntityType.Asset, assetA),
                dbA, userA);

            var deleted = await LinkEndpoints.DeleteAsync(poisonId, dbA, userA);
            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(deleted).StatusCode);
        }

        var (checkB, checkUserB) = Open(dbName, tenantB);
        await using (checkB)
        {
            Assert.NotNull(await checkB.ResourceLinks.ForTenant(checkUserB).FirstOrDefaultAsync(l => l.Id == poisonId));
        }
    }

    [Fact]
    public async Task Company_Related_Skips_Poison_Link_Pointing_At_Other_Tenant_Entity()
    {
        var (tenantA, _, companyA, companyB, _, _, _, _, _, _, _, _, dbName) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            db.ResourceLinks.Add(new ResourceLink
            {
                TenantId = tenantA,
                FromType = LinkEntityType.Company,
                FromId = companyA,
                ToType = LinkEntityType.Company,
                ToId = companyB,
                Label = "poison",
            });
            await db.SaveChangesAsync();

            var listed = await LinkEndpoints.ListForEntityAsync(db, user, LinkEntityType.Company, companyA);
            Assert.Empty(listed);

            var related = await CompanyEndpoints.LoadRelatedAsync(db, user, companyA);
            Assert.Equal(0, related.RelatedLinkCount);
            Assert.Empty(related.RelatedLinks);
        }
    }
}
