using System.Text.Json;
using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

public class PortalEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static (DocuEngAIneDbContext Db, FakeCurrentUser User) Open(string dbName, Guid tenantId)
    {
        var user = new FakeCurrentUser
        {
            TenantId = tenantId,
            ObjectId = Guid.NewGuid().ToString(),
            Role = UserRole.Reader,
        };
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return (new DocuEngAIneDbContext(options, user), user);
    }

    private static async Task<(
        string DbName,
        Guid TenantA,
        Guid TenantB,
        Guid EnabledA,
        Guid DisabledA,
        Guid EnabledB,
        Guid DocA,
        Guid DraftA,
        Guid CentralA,
        Guid DocB,
        Guid KeeperA)> SeedAsync()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var enabledA = new Company { TenantId = tenantA, Name = "ExampleCo", Slug = "exampleco", PortalEnabled = true, Website = "https://example.co", Phone = "555-0100" };
        var disabledA = new Company { TenantId = tenantA, Name = "ClosedCo", Slug = "closedco", PortalEnabled = false };
        var enabledB = new Company { TenantId = tenantB, Name = "PoisonCo", Slug = "poisonco", PortalEnabled = true };
        var typeA = new AssetType { TenantId = tenantA, Name = "Licenses" };
        var typeB = new AssetType { TenantId = tenantB, Name = "PoisonType" };

        var (dbA, _) = Open(dbName, tenantA);
        await using (dbA)
        {
            dbA.Tenants.Add(new Tenant { Id = tenantA, Name = "A", Slug = "a" });
            dbA.Companies.AddRange(enabledA, disabledA);
            dbA.AssetTypes.Add(typeA);
            await dbA.SaveChangesAsync();

            dbA.Assets.Add(new Asset
            {
                TenantId = tenantA,
                Name = "Office 365",
                AssetTypeId = typeA.Id,
                CompanyId = enabledA.Id,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(21),
            });
            dbA.Assets.Add(new Asset
            {
                TenantId = tenantA,
                Name = "Closed license",
                AssetTypeId = typeA.Id,
                CompanyId = disabledA.Id,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(5),
            });
            dbA.Documents.AddRange(
                new Document
                {
                    TenantId = tenantA,
                    Title = "VPN guide",
                    Slug = "vpn-guide",
                    Summary = "How to connect",
                    Content = "Use the company VPN.",
                    Tags = "network",
                    CompanyId = enabledA.Id,
                    IsPublished = true,
                },
                new Document
                {
                    TenantId = tenantA,
                    Title = "Draft SOP",
                    Slug = "draft-sop",
                    Content = "internal-only",
                    CompanyId = enabledA.Id,
                    IsPublished = false,
                },
                new Document
                {
                    TenantId = tenantA,
                    Title = "Central KB",
                    Slug = "central-kb",
                    Content = "not company scoped",
                    CompanyId = null,
                    IsPublished = true,
                },
                new Document
                {
                    TenantId = tenantA,
                    Title = "Closed handbook",
                    Slug = "closed-handbook",
                    CompanyId = disabledA.Id,
                    IsPublished = true,
                });
            dbA.KeeperLinks.Add(new KeeperLink
            {
                TenantId = tenantA,
                Name = "Firewall admin",
                KeeperRecordUrl = "https://keeper.example/a",
                KeeperRecordUid = "uid-a",
                UsernameHint = "admin-a",
                Notes = "do-not-leak-notes",
                CompanyId = enabledA.Id,
            });
            dbA.KeeperLinks.Add(new KeeperLink
            {
                TenantId = tenantA,
                Name = "Closed vault",
                KeeperRecordUrl = "https://keeper.example/closed",
                Notes = "closed-secret",
                CompanyId = disabledA.Id,
            });
            await dbA.SaveChangesAsync();
        }

        var (dbB, _) = Open(dbName, tenantB);
        await using (dbB)
        {
            dbB.Tenants.Add(new Tenant { Id = tenantB, Name = "B", Slug = "b" });
            dbB.Companies.Add(enabledB);
            dbB.AssetTypes.Add(typeB);
            await dbB.SaveChangesAsync();

            dbB.Assets.Add(new Asset
            {
                TenantId = tenantB,
                Name = "Poison-Server",
                AssetTypeId = typeB.Id,
                CompanyId = enabledB.Id,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(3),
            });
            dbB.Documents.Add(new Document
            {
                TenantId = tenantB,
                Title = "Poison-Doc",
                Slug = "poison-doc",
                Content = "cross-tenant leak",
                CompanyId = enabledB.Id,
                IsPublished = true,
            });
            dbB.KeeperLinks.Add(new KeeperLink
            {
                TenantId = tenantB,
                Name = "Poison-Vault",
                KeeperRecordUrl = "https://keeper.example/poison",
                Notes = "secret-b",
                UsernameHint = "admin-b",
                CompanyId = enabledB.Id,
            });
            await dbB.SaveChangesAsync();
        }

        var (ids, _) = Open(dbName, tenantA);
        await using (ids)
        {
            var docA = await ids.Documents.SingleAsync(d => d.Title == "VPN guide");
            var draftA = await ids.Documents.SingleAsync(d => d.Title == "Draft SOP");
            var centralA = await ids.Documents.SingleAsync(d => d.Title == "Central KB");
            var docB = await ids.Documents.SingleAsync(d => d.Title == "Poison-Doc");
            var keeperA = await ids.KeeperLinks.SingleAsync(k => k.Name == "Firewall admin");
            return (dbName, tenantA, tenantB, enabledA.Id, disabledA.Id, enabledB.Id, docA.Id, draftA.Id, centralA.Id, docB.Id, keeperA.Id);
        }
    }

    private static int StatusOf(IResult result)
        => result is IStatusCodeHttpResult s && s.StatusCode is int code ? code : 0;

    private static T ValueOf<T>(IResult result)
    {
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result);
        return Assert.IsType<T>(value.Value);
    }

    private static string JsonOf(IResult result)
    {
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result);
        return JsonSerializer.Serialize(value.Value, JsonOptions);
    }

    [Fact]
    public void Describe_Is_Read_Only_Without_Reveal_Or_Vault()
    {
        var json = JsonSerializer.Serialize(PortalEndpoints.Describe(), JsonOptions);

        Assert.Contains("\"readOnly\":true", json);
        Assert.Contains("\"passwordVault\":false", json);
        Assert.Contains("\"forTenant\":true", json);
        Assert.Contains("\"metadataOnly\":true", json);
        Assert.Contains("\"reveal\":false", json);
        Assert.Contains("ForTenant", json);
        Assert.Contains("No password vault", json);
        Assert.Equal(new[] { "documents", "expirations", "keeperLinks" }, PortalEndpoints.Surfaces);
        Assert.DoesNotContain(PortalEndpoints.Surfaces, s => s.Contains("reveal", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(PortalEndpoints.Surfaces, s => s.Contains("runbook", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(PortalEndpoints.Surfaces, s => s.Contains("asset", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task List_Companies_Is_ForTenant_And_PortalEnabled_Only()
    {
        var seed = await SeedAsync();
        var (db, user) = Open(seed.DbName, seed.TenantA);
        await using (db)
        {
            var items = ValueOf<List<PortalCompanyListItem>>(await PortalEndpoints.ListCompaniesAsync(db, user));

            Assert.Single(items);
            Assert.Equal(seed.EnabledA, items[0].Id);
            Assert.Equal("ExampleCo", items[0].Name);
            Assert.DoesNotContain(items, c => c.Name == "ClosedCo");
            Assert.DoesNotContain(items, c => c.Name == "PoisonCo");
            Assert.DoesNotContain(items, c => c.Id == seed.EnabledB);
        }
    }

    [Fact]
    public async Task Get_Company_Other_Tenant_Or_Disabled_Is_Not_Found()
    {
        var seed = await SeedAsync();
        var (db, user) = Open(seed.DbName, seed.TenantA);
        await using (db)
        {
            var own = ValueOf<PortalCompanyDetail>(await PortalEndpoints.GetCompanyAsync(seed.EnabledA, db, user));
            Assert.Equal("ExampleCo", own.Name);
            Assert.Equal(1, own.Counts.Documents);
            Assert.Equal(1, own.Counts.KeeperLinks);
            Assert.True(own.Counts.Expirations >= 1);
            Assert.Equal("https://example.co", own.Website);

            Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await PortalEndpoints.GetCompanyAsync(seed.DisabledA, db, user)));
            Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await PortalEndpoints.GetCompanyAsync(seed.EnabledB, db, user)));
            Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await PortalEndpoints.GetCompanyAsync(Guid.NewGuid(), db, user)));
        }
    }

    [Fact]
    public async Task Documents_Are_Published_Company_Docs_ForTenant_Only()
    {
        var seed = await SeedAsync();
        var (db, user) = Open(seed.DbName, seed.TenantA);
        await using (db)
        {
            var listed = ValueOf<List<PortalDocumentListItem>>(
                await PortalEndpoints.ListDocumentsAsync(seed.EnabledA, db, user));
            Assert.Single(listed);
            Assert.Equal("VPN guide", listed[0].Title);
            Assert.DoesNotContain(listed, d => d.Title == "Draft SOP");
            Assert.DoesNotContain(listed, d => d.Title == "Central KB");
            Assert.DoesNotContain(listed, d => d.Title == "Poison-Doc");
            Assert.DoesNotContain(listed, d => d.Title == "Closed handbook");

            var published = ValueOf<PortalDocumentDetail>(
                await PortalEndpoints.GetDocumentAsync(seed.EnabledA, seed.DocA, db, user));
            Assert.Equal("Use the company VPN.", published.Content);

            Assert.Equal(
                StatusCodes.Status404NotFound,
                StatusOf(await PortalEndpoints.GetDocumentAsync(seed.EnabledA, seed.DraftA, db, user)));
            Assert.Equal(
                StatusCodes.Status404NotFound,
                StatusOf(await PortalEndpoints.GetDocumentAsync(seed.EnabledA, seed.CentralA, db, user)));
            Assert.Equal(
                StatusCodes.Status404NotFound,
                StatusOf(await PortalEndpoints.GetDocumentAsync(seed.EnabledA, seed.DocB, db, user)));
            Assert.Equal(
                StatusCodes.Status404NotFound,
                StatusOf(await PortalEndpoints.ListDocumentsAsync(seed.EnabledB, db, user)));
            Assert.Equal(
                StatusCodes.Status404NotFound,
                StatusOf(await PortalEndpoints.ListDocumentsAsync(seed.DisabledA, db, user)));

            var foreignJson = JsonOf(await PortalEndpoints.ListDocumentsAsync(seed.EnabledB, db, user));
            Assert.DoesNotContain("Poison", foreignJson);
            Assert.DoesNotContain("cross-tenant", foreignJson);
        }
    }

    [Fact]
    public async Task Expirations_Are_ForTenant_And_Company_Scoped()
    {
        var seed = await SeedAsync();
        var (db, user) = Open(seed.DbName, seed.TenantA);
        await using (db)
        {
            var items = ValueOf<List<ExpirationItem>>(
                await PortalEndpoints.ListExpirationsAsync(seed.EnabledA, false, null, db, user));
            Assert.Contains(items, i => i.Name == "Office 365");
            Assert.DoesNotContain(items, i => i.Name == "Poison-Server");
            Assert.DoesNotContain(items, i => i.Name == "Closed license");

            Assert.Equal(
                StatusCodes.Status404NotFound,
                StatusOf(await PortalEndpoints.ListExpirationsAsync(seed.EnabledB, true, null, db, user)));
            Assert.Equal(
                StatusCodes.Status404NotFound,
                StatusOf(await PortalEndpoints.ListExpirationsAsync(seed.DisabledA, true, null, db, user)));

            var foreignJson = JsonOf(await PortalEndpoints.ListExpirationsAsync(seed.EnabledB, true, null, db, user));
            Assert.DoesNotContain("Poison", foreignJson);
        }
    }

    [Fact]
    public async Task Keeper_Links_Are_Metadata_Only_Without_Reveal()
    {
        var seed = await SeedAsync();
        var (db, user) = Open(seed.DbName, seed.TenantA);
        await using (db)
        {
            var items = ValueOf<List<PortalKeeperLinkItem>>(
                await PortalEndpoints.ListKeeperLinksAsync(seed.EnabledA, db, user));
            Assert.Single(items);
            Assert.Equal(seed.KeeperA, items[0].Id);
            Assert.Equal("Firewall admin", items[0].Title);
            Assert.True(items[0].HasRecordUrl);

            var json = JsonOf(await PortalEndpoints.ListKeeperLinksAsync(seed.EnabledA, db, user));
            Assert.DoesNotContain("keeper.example", json);
            Assert.DoesNotContain("admin-a", json);
            Assert.DoesNotContain("do-not-leak-notes", json);
            Assert.DoesNotContain("uid-a", json);
            Assert.DoesNotContain("Poison-Vault", json);
            Assert.DoesNotContain("Closed vault", json);
            Assert.DoesNotContain("reveal", json, StringComparison.OrdinalIgnoreCase);

            Assert.Empty(await db.AuditLogs.Where(a => a.Action == "KeeperLink.Reveal").ToListAsync());

            Assert.Equal(
                StatusCodes.Status404NotFound,
                StatusOf(await PortalEndpoints.ListKeeperLinksAsync(seed.EnabledB, db, user)));
            Assert.Equal(
                StatusCodes.Status404NotFound,
                StatusOf(await PortalEndpoints.ListKeeperLinksAsync(seed.DisabledA, db, user)));
        }
    }

    [Fact]
    public async Task Create_And_Update_Can_Toggle_PortalEnabled()
    {
        var tenantId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();
        var (db, user) = Open(dbName, tenantId);
        await using (db)
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "A", Slug = "a" });
            await db.SaveChangesAsync();

            var created = await CompanyEndpoints.CreateAsync(
                new CreateCompanyRequest("PortalCo", "portalco", PortalEnabled: true),
                db,
                user);
            Assert.Equal(StatusCodes.Status201Created, StatusOf(created));
            var company = await db.Companies.ForTenant(user).SingleAsync();
            Assert.True(company.PortalEnabled);

            var listed = ValueOf<List<PortalCompanyListItem>>(await PortalEndpoints.ListCompaniesAsync(db, user));
            Assert.Single(listed);
            Assert.Equal(company.Id, listed[0].Id);

            var disabled = await CompanyEndpoints.UpdateAsync(
                company.Id,
                new UpdateCompanyRequest(PortalEnabled: false),
                db,
                user);
            Assert.Equal(StatusCodes.Status204NoContent, StatusOf(disabled));

            db.ChangeTracker.Clear();
            Assert.False((await db.Companies.ForTenant(user).SingleAsync()).PortalEnabled);
            Assert.Empty(ValueOf<List<PortalCompanyListItem>>(await PortalEndpoints.ListCompaniesAsync(db, user)));
            Assert.Equal(StatusCodes.Status404NotFound, StatusOf(await PortalEndpoints.GetCompanyAsync(company.Id, db, user)));
        }
    }
}
