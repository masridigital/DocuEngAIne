using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

public class FlagTests
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
        Guid DocumentB,
        Guid RunbookB,
        Guid KeeperB,
        FlagDefinition FlagA,
        FlagDefinition FlagB,
        string DbName)> SeedAsync()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var companyA = new Company { TenantId = tenantA, Name = "ExampleCo", Slug = "exampleco" };
        var companyB = new Company { TenantId = tenantB, Name = "PoisonCo", Slug = "poisonco" };
        var typeA = new AssetType { TenantId = tenantA, Name = "Servers" };
        var typeB = new AssetType { TenantId = tenantB, Name = "PoisonType" };
        var flagA = new FlagDefinition { TenantId = tenantA, Name = "Critical", Color = "#DC2626" };
        var flagB = new FlagDefinition { TenantId = tenantB, Name = "Break-Glass", Color = "#F97316" };

        Asset assetA;
        Asset assetB;
        Document documentB;
        Runbook runbookB;
        KeeperLink keeperB;

        var (dbA, _) = Open(dbName, tenantA);
        await using (dbA)
        {
            dbA.Companies.Add(companyA);
            dbA.AssetTypes.Add(typeA);
            dbA.FlagDefinitions.Add(flagA);
            await dbA.SaveChangesAsync();

            assetA = new Asset
            {
                TenantId = tenantA,
                Name = "Firewall",
                AssetTypeId = typeA.Id,
                CompanyId = companyA.Id,
            };
            dbA.Assets.Add(assetA);
            dbA.Documents.Add(new Document { TenantId = tenantA, Title = "A-Runbook-Doc", Slug = "a-doc", CompanyId = companyA.Id });
            dbA.Runbooks.Add(new Runbook { TenantId = tenantA, Title = "A-SOP", Slug = "a-sop", CompanyId = companyA.Id });
            dbA.KeeperLinks.Add(new KeeperLink { TenantId = tenantA, Name = "A-Vault", CompanyId = companyA.Id, KeeperRecordUrl = "https://keeper.example/a" });
            await dbA.SaveChangesAsync();
        }

        var (dbB, _) = Open(dbName, tenantB);
        await using (dbB)
        {
            dbB.Companies.Add(companyB);
            dbB.AssetTypes.Add(typeB);
            dbB.FlagDefinitions.Add(flagB);
            await dbB.SaveChangesAsync();

            assetB = new Asset
            {
                TenantId = tenantB,
                Name = "Poison SSL",
                AssetTypeId = typeB.Id,
                CompanyId = companyB.Id,
            };
            documentB = new Document { TenantId = tenantB, Title = "Poison-Doc", Slug = "poison-doc", CompanyId = companyB.Id };
            runbookB = new Runbook { TenantId = tenantB, Title = "Poison-SOP", Slug = "poison-sop", CompanyId = companyB.Id };
            keeperB = new KeeperLink { TenantId = tenantB, Name = "Poison-Vault", CompanyId = companyB.Id, KeeperRecordUrl = "https://keeper.example/b" };
            dbB.Assets.Add(assetB);
            dbB.Documents.Add(documentB);
            dbB.Runbooks.Add(runbookB);
            dbB.KeeperLinks.Add(keeperB);
            await dbB.SaveChangesAsync();
        }

        return (tenantA, tenantB, companyA.Id, companyB.Id, assetA.Id, assetB.Id, documentB.Id, runbookB.Id, keeperB.Id, flagA, flagB, dbName);
    }

    [Fact]
    public async Task ForTenant_Does_Not_Leak_Other_Tenant_Definitions()
    {
        var (tenantA, tenantB, _, _, _, _, _, _, _, _, _, dbName) = await SeedAsync();

        var (dbA, userA) = Open(dbName, tenantA);
        await using (dbA)
        {
            var listed = await FlagEndpoints.ListDefinitionsAsync(dbA, userA);
            Assert.Single(listed);
            Assert.Equal("Critical", listed[0].Name);
            Assert.DoesNotContain(listed, f => f.Name == "Break-Glass");

            var hidden = await dbA.FlagDefinitions.ForTenant(userA).FirstOrDefaultAsync(f => f.Name == "Break-Glass");
            Assert.Null(hidden);
        }

        var (dbB, userB) = Open(dbName, tenantB);
        await using (dbB)
        {
            var listed = await FlagEndpoints.ListDefinitionsAsync(dbB, userB);
            Assert.Single(listed);
            Assert.Equal("Break-Glass", listed[0].Name);
        }
    }

    [Fact]
    public async Task ForTenant_Does_Not_Leak_Other_Tenant_Assignments_Or_Review_Names()
    {
        var (tenantA, tenantB, companyA, companyB, assetA, assetB, _, _, _, flagA, flagB, dbName) = await SeedAsync();

        var (dbA, userA) = Open(dbName, tenantA);
        await using (dbA)
        {
            var assigned = await FlagEndpoints.AssignAsync(
                flagA.Id,
                new AssignFlagRequest(FlagEntityType.Company, companyA),
                dbA,
                userA);
            Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<IStatusCodeHttpResult>(assigned).StatusCode);

            var assetAssigned = await FlagEndpoints.AssignAsync(
                flagA.Id,
                new AssignFlagRequest(FlagEntityType.Asset, assetA),
                dbA,
                userA);
            Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<IStatusCodeHttpResult>(assetAssigned).StatusCode);
        }

        var (dbB, userB) = Open(dbName, tenantB);
        await using (dbB)
        {
            var assigned = await FlagEndpoints.AssignAsync(
                flagB.Id,
                new AssignFlagRequest(FlagEntityType.Company, companyB),
                dbB,
                userB);
            Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<IStatusCodeHttpResult>(assigned).StatusCode);

            var assetAssigned = await FlagEndpoints.AssignAsync(
                flagB.Id,
                new AssignFlagRequest(FlagEntityType.Asset, assetB),
                dbB,
                userB);
            Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<IStatusCodeHttpResult>(assetAssigned).StatusCode);
        }

        var (queryA, queryUserA) = Open(dbName, tenantA);
        await using (queryA)
        {
            var assignments = await queryA.FlagAssignments.ForTenant(queryUserA).Include(a => a.FlagDefinition).ToListAsync();
            Assert.Equal(2, assignments.Count);
            Assert.All(assignments, a => Assert.Equal("Critical", a.FlagDefinition.Name));
            Assert.DoesNotContain(assignments, a => a.EntityId == companyB || a.EntityId == assetB);

            var review = await FlagEndpoints.QueryReviewAsync(queryA, queryUserA);
            Assert.Equal(2, review.Count);
            Assert.Contains(review, i => i.EntityName == "ExampleCo" && i.EntityType == FlagEntityType.Company);
            Assert.Contains(review, i => i.EntityName == "Firewall" && i.CompanyName == "ExampleCo");
            Assert.DoesNotContain(review, i => i.EntityName.Contains("Poison", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(review, i => i.FlagName == "Break-Glass");
            Assert.DoesNotContain(review, i => i.CompanyName == "PoisonCo");
        }

        var (queryB, queryUserB) = Open(dbName, tenantB);
        await using (queryB)
        {
            var review = await FlagEndpoints.QueryReviewAsync(queryB, queryUserB, FlagEntityType.Asset);
            Assert.Single(review);
            Assert.Equal("Poison SSL", review[0].EntityName);
            Assert.DoesNotContain(review, i => i.EntityName == "Firewall");
        }
    }

    [Fact]
    public async Task Cannot_Assign_Flag_To_Other_Tenant_Entity()
    {
        var (tenantA, _, _, companyB, _, assetB, documentB, runbookB, keeperB, flagA, _, dbName) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            AssertBadRequest(
                await FlagEndpoints.AssignAsync(flagA.Id, new AssignFlagRequest(FlagEntityType.Company, companyB), db, user),
                FlagEndpoints.EntityNotFoundMessage);
            AssertBadRequest(
                await FlagEndpoints.AssignAsync(flagA.Id, new AssignFlagRequest(FlagEntityType.Asset, assetB), db, user),
                FlagEndpoints.EntityNotFoundMessage);
            AssertBadRequest(
                await FlagEndpoints.AssignAsync(flagA.Id, new AssignFlagRequest(FlagEntityType.Document, documentB), db, user),
                FlagEndpoints.EntityNotFoundMessage);
            AssertBadRequest(
                await FlagEndpoints.AssignAsync(flagA.Id, new AssignFlagRequest(FlagEntityType.Runbook, runbookB), db, user),
                FlagEndpoints.EntityNotFoundMessage);
            AssertBadRequest(
                await FlagEndpoints.AssignAsync(flagA.Id, new AssignFlagRequest(FlagEntityType.KeeperLink, keeperB), db, user),
                FlagEndpoints.EntityNotFoundMessage);
            AssertBadRequest(
                await FlagEndpoints.AssignAsync(flagA.Id, new AssignFlagRequest(FlagEntityType.Company, Guid.NewGuid()), db, user),
                FlagEndpoints.EntityNotFoundMessage);

            Assert.Empty(await db.FlagAssignments.ForTenant(user).ToListAsync());
        }
    }

    [Fact]
    public async Task Review_Skips_Poison_Assignment_Pointing_At_Other_Tenant_Entity()
    {
        var (tenantA, _, _, companyB, _, _, _, _, _, flagA, _, dbName) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            db.FlagAssignments.Add(new FlagAssignment
            {
                TenantId = tenantA,
                FlagDefinitionId = flagA.Id,
                EntityType = FlagEntityType.Company,
                EntityId = companyB,
            });
            await db.SaveChangesAsync();

            var review = await FlagEndpoints.QueryReviewAsync(db, user);
            Assert.Empty(review);
        }
    }

    [Fact]
    public async Task Create_Normalizes_Color_And_Rejects_Invalid()
    {
        var tenantId = Guid.NewGuid();
        var (db, user) = Open(Guid.NewGuid().ToString(), tenantId);
        await using (db)
        {
            var created = await FlagEndpoints.CreateDefinitionAsync(
                new CreateFlagDefinitionRequest("Needs Review", "#eab"),
                db,
                user);
            Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<IStatusCodeHttpResult>(created).StatusCode);

            var listed = await FlagEndpoints.ListDefinitionsAsync(db, user);
            Assert.Single(listed);
            Assert.Equal("#EEAABB", listed[0].Color);

            var bad = await FlagEndpoints.CreateDefinitionAsync(
                new CreateFlagDefinitionRequest("Compliance", "red"),
                db,
                user);
            AssertBadRequest(bad, FlagEndpoints.InvalidColorMessage);
        }
    }
}
