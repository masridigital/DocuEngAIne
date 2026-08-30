using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

public class CompanyGraphTests
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

    private static CompanyGraph AssertGraph(IResult result)
    {
        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result);
        return Assert.IsType<CompanyGraph>(value.Value);
    }

    private static async Task<(
        Guid TenantA,
        Guid TenantB,
        Guid CompanyA,
        Guid CompanyB,
        Guid AssetA,
        Guid AssetB,
        Guid DocumentA,
        Guid RunbookA,
        Guid KeeperA,
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
        Runbook runbookA;
        KeeperLink keeperA;

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
            dbB.Assets.Add(assetB);
            dbB.Documents.Add(new Document { TenantId = tenantB, Title = "Poison-Doc", Slug = "poison-doc", CompanyId = companyB.Id });
            dbB.Runbooks.Add(new Runbook { TenantId = tenantB, Title = "Poison-SOP", Slug = "poison-sop", CompanyId = companyB.Id });
            dbB.KeeperLinks.Add(new KeeperLink { TenantId = tenantB, Name = "Poison-Vault", CompanyId = companyB.Id, KeeperRecordUrl = "https://keeper.example/b" });
            await dbB.SaveChangesAsync();
        }

        return (tenantA, tenantB, companyA.Id, companyB.Id, assetA.Id, assetB.Id, documentA.Id, runbookA.Id, keeperA.Id, dbName);
    }

    [Fact]
    public async Task Graph_Other_Tenant_Company_Is_NotFound()
    {
        var (tenantA, _, _, companyB, _, _, _, _, _, dbName) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var result = await CompanyEndpoints.GetGraphAsync(companyB, db, user);
            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        }
    }

    [Fact]
    public async Task Graph_Unknown_Company_Is_NotFound()
    {
        var (tenantA, _, _, _, _, _, _, _, _, dbName) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var result = await CompanyEndpoints.GetGraphAsync(Guid.NewGuid(), db, user);
            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        }
    }

    [Fact]
    public async Task Graph_Empty_Company_Has_Root_Node_And_No_Edges()
    {
        var (tenantA, _, companyA, _, _, _, _, _, _, dbName) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var graph = AssertGraph(await CompanyEndpoints.GetGraphAsync(companyA, db, user));
            Assert.Equal(companyA, graph.CompanyId);
            var node = Assert.Single(graph.Nodes);
            Assert.Equal(LinkEntityType.Company, node.Type);
            Assert.Equal(companyA, node.Id);
            Assert.Equal("ExampleCo", node.Name);
            Assert.Empty(graph.Edges);
        }
    }

    [Fact]
    public async Task Graph_Returns_Nodes_And_Edges_From_ResourceLinks()
    {
        var (tenantA, _, companyA, _, assetA, _, documentA, runbookA, keeperA, dbName) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            await LinkEndpoints.CreateAsync(
                new CreateResourceLinkRequest(LinkEntityType.Company, companyA, LinkEntityType.Asset, assetA, "owns-firewall"),
                db, user);
            await LinkEndpoints.CreateAsync(
                new CreateResourceLinkRequest(LinkEntityType.Company, companyA, LinkEntityType.Document, documentA, "kb"),
                db, user);
            await LinkEndpoints.CreateAsync(
                new CreateResourceLinkRequest(LinkEntityType.Company, companyA, LinkEntityType.Runbook, runbookA),
                db, user);
            await LinkEndpoints.CreateAsync(
                new CreateResourceLinkRequest(LinkEntityType.Company, companyA, LinkEntityType.KeeperLink, keeperA, "vault"),
                db, user);
            await LinkEndpoints.CreateAsync(
                new CreateResourceLinkRequest(LinkEntityType.Runbook, runbookA, LinkEntityType.KeeperLink, keeperA, "sop-secret"),
                db, user);

            var graph = AssertGraph(await CompanyEndpoints.GetGraphAsync(companyA, db, user));

            Assert.Equal(companyA, graph.CompanyId);
            Assert.Equal(5, graph.Nodes.Count);
            Assert.Contains(graph.Nodes, n => n.Type == LinkEntityType.Company && n.Id == companyA && n.Name == "ExampleCo");
            Assert.Contains(graph.Nodes, n => n.Type == LinkEntityType.Asset && n.Id == assetA && n.Name == "Firewall");
            Assert.Contains(graph.Nodes, n => n.Type == LinkEntityType.Document && n.Id == documentA && n.Name == "A-Doc");
            Assert.Contains(graph.Nodes, n => n.Type == LinkEntityType.Runbook && n.Id == runbookA && n.Name == "A-SOP");
            Assert.Contains(graph.Nodes, n => n.Type == LinkEntityType.KeeperLink && n.Id == keeperA && n.Name == "A-Vault");

            Assert.Equal(5, graph.Edges.Count);
            Assert.Contains(graph.Edges, e =>
                e.FromType == LinkEntityType.Company && e.FromId == companyA
                && e.ToType == LinkEntityType.Asset && e.ToId == assetA
                && e.Label == "owns-firewall");
            Assert.Contains(graph.Edges, e =>
                e.FromType == LinkEntityType.Company && e.FromId == companyA
                && e.ToType == LinkEntityType.Document && e.ToId == documentA
                && e.Label == "kb");
            Assert.Contains(graph.Edges, e =>
                e.FromType == LinkEntityType.Company && e.FromId == companyA
                && e.ToType == LinkEntityType.Runbook && e.ToId == runbookA
                && e.Label is null);
            Assert.Contains(graph.Edges, e =>
                e.FromType == LinkEntityType.Company && e.FromId == companyA
                && e.ToType == LinkEntityType.KeeperLink && e.ToId == keeperA
                && e.Label == "vault");
            Assert.Contains(graph.Edges, e =>
                e.FromType == LinkEntityType.Runbook && e.FromId == runbookA
                && e.ToType == LinkEntityType.KeeperLink && e.ToId == keeperA
                && e.Label == "sop-secret");

            Assert.Equal(
                ["Asset", "Company", "Document", "KeeperLink", "Runbook"],
                graph.Nodes.Select(n => n.Type).ToArray());
        }
    }

    [Fact]
    public async Task Graph_Does_Not_Leak_Other_Tenant_Links_Or_Nodes()
    {
        var (tenantA, tenantB, companyA, companyB, assetA, assetB, _, _, _, dbName) = await SeedAsync();

        var (dbA, userA) = Open(dbName, tenantA);
        await using (dbA)
        {
            await LinkEndpoints.CreateAsync(
                new CreateResourceLinkRequest(LinkEntityType.Company, companyA, LinkEntityType.Asset, assetA, "own-edge"),
                dbA, userA);
        }

        var (dbB, userB) = Open(dbName, tenantB);
        await using (dbB)
        {
            await LinkEndpoints.CreateAsync(
                new CreateResourceLinkRequest(LinkEntityType.Company, companyB, LinkEntityType.Asset, assetB, "poison-edge"),
                dbB, userB);
        }

        var (queryA, queryUserA) = Open(dbName, tenantA);
        await using (queryA)
        {
            queryA.ResourceLinks.Add(new ResourceLink
            {
                TenantId = tenantA,
                FromType = LinkEntityType.Company,
                FromId = companyA,
                ToType = LinkEntityType.Company,
                ToId = companyB,
                Label = "poison-company",
            });
            await queryA.SaveChangesAsync();

            var graph = AssertGraph(await CompanyEndpoints.GetGraphAsync(companyA, queryA, queryUserA));
            Assert.Single(graph.Edges);
            Assert.Equal("own-edge", graph.Edges[0].Label);
            Assert.DoesNotContain(graph.Nodes, n => n.Name.Contains("Poison", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(graph.Edges, e => e.ToId == companyB || e.ToId == assetB || e.Label == "poison-edge");
            Assert.DoesNotContain(graph.Nodes, n => n.Id == companyB || n.Id == assetB);

            var hidden = await CompanyEndpoints.GetGraphAsync(companyB, queryA, queryUserA);
            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(hidden).StatusCode);
        }

        var (queryB, queryUserB) = Open(dbName, tenantB);
        await using (queryB)
        {
            var graphB = AssertGraph(await CompanyEndpoints.GetGraphAsync(companyB, queryB, queryUserB));
            Assert.Single(graphB.Edges);
            Assert.Equal("poison-edge", graphB.Edges[0].Label);
            Assert.DoesNotContain(graphB.Nodes, n => n.Name == "ExampleCo" || n.Name == "Firewall");
        }
    }
}
