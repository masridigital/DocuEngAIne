using System.Text.Json;
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
    public async Task Portal_Urls_Round_Trip_On_Create_And_Update()
    {
        var tenantId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();
        var haloUrl = "https://halo.example/clients/42";
        var ninjaUrl = "https://ninja.example/organizations/99";

        Guid companyId;
        var (db, user) = Open(dbName, tenantId);
        await using (db)
        {
            var created = await CompanyEndpoints.CreateAsync(
                new CreateCompanyRequest(
                    "ExampleCo",
                    "exampleco",
                    HaloClientId: "halo-42",
                    NinjaOrganizationId: "ninja-99",
                    HaloPortalUrl: $"  {haloUrl}  ",
                    NinjaPortalUrl: ninjaUrl),
                db,
                user);
            Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<IStatusCodeHttpResult>(created).StatusCode);

            var stored = await db.Companies.ForTenant(user).SingleAsync();
            companyId = stored.Id;
            Assert.Equal(haloUrl, stored.HaloPortalUrl);
            Assert.Equal(ninjaUrl, stored.NinjaPortalUrl);
            Assert.Equal("halo-42", stored.HaloClientId);
            Assert.Equal("ninja-99", stored.NinjaOrganizationId);
            Assert.Null(stored.GetType().GetProperty("AuthSecretName"));
            Assert.Null(stored.GetType().GetProperty("EncryptedValue"));

            db.ChangeTracker.Clear();
            var get = await CompanyEndpoints.GetAsync(companyId, db, user);
            Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(get).StatusCode);
            var payload = Assert.IsAssignableFrom<IValueHttpResult>(get).Value!;
            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            Assert.Equal(haloUrl, doc.RootElement.GetProperty("HaloPortalUrl").GetString());
            Assert.Equal(ninjaUrl, doc.RootElement.GetProperty("NinjaPortalUrl").GetString());
        }

        var (updateDb, updateUser) = Open(dbName, tenantId);
        await using (updateDb)
        {
            var updatedHalo = "https://halo.example/clients/42/tickets";
            var updated = await CompanyEndpoints.UpdateAsync(
                companyId,
                new UpdateCompanyRequest(HaloPortalUrl: updatedHalo, NinjaPortalUrl: ""),
                updateDb,
                updateUser);
            Assert.Equal(StatusCodes.Status204NoContent, Assert.IsAssignableFrom<IStatusCodeHttpResult>(updated).StatusCode);

            updateDb.ChangeTracker.Clear();
            var reloaded = await updateDb.Companies.ForTenant(updateUser).AsNoTracking().SingleAsync(c => c.Id == companyId);
            Assert.Equal(updatedHalo, reloaded.HaloPortalUrl);
            Assert.Null(reloaded.NinjaPortalUrl);
            Assert.Equal("halo-42", reloaded.HaloClientId);
            Assert.Equal("ninja-99", reloaded.NinjaOrganizationId);
        }
    }

    [Fact]
    public async Task ForTenant_Does_Not_Leak_Other_Tenant_Portal_Urls()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        Guid companyBId;
        var (seedB, userB) = Open(dbName, tenantB);
        await using (seedB)
        {
            var created = await CompanyEndpoints.CreateAsync(
                new CreateCompanyRequest(
                    "PoisonCo",
                    "poisonco",
                    HaloPortalUrl: "https://halo.example/clients/secret",
                    NinjaPortalUrl: "https://ninja.example/organizations/secret"),
                seedB,
                userB);
            Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<IStatusCodeHttpResult>(created).StatusCode);
            companyBId = (await seedB.Companies.ForTenant(userB).SingleAsync()).Id;
        }

        var (seedA, userA) = Open(dbName, tenantA);
        await using (seedA)
        {
            var created = await CompanyEndpoints.CreateAsync(
                new CreateCompanyRequest(
                    "ExampleCo",
                    "exampleco",
                    HaloPortalUrl: "https://halo.example/clients/own",
                    NinjaPortalUrl: "https://ninja.example/organizations/own"),
                seedA,
                userA);
            Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<IStatusCodeHttpResult>(created).StatusCode);

            var listed = await seedA.Companies.ForTenant(userA).ToListAsync();
            Assert.Single(listed);
            Assert.Equal("ExampleCo", listed[0].Name);
            Assert.Equal("https://halo.example/clients/own", listed[0].HaloPortalUrl);
            Assert.DoesNotContain(listed, c => c.Id == companyBId);
            Assert.DoesNotContain(listed, c => c.HaloPortalUrl != null && c.HaloPortalUrl.Contains("secret", StringComparison.Ordinal));
            Assert.DoesNotContain(listed, c => c.NinjaPortalUrl != null && c.NinjaPortalUrl.Contains("secret", StringComparison.Ordinal));

            var hidden = await seedA.Companies.ForTenant(userA).FirstOrDefaultAsync(c => c.Id == companyBId);
            Assert.Null(hidden);

            var getHidden = await CompanyEndpoints.GetAsync(companyBId, seedA, userA);
            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(getHidden).StatusCode);

            var updateHidden = await CompanyEndpoints.UpdateAsync(
                companyBId,
                new UpdateCompanyRequest(HaloPortalUrl: "https://halo.example/stolen"),
                seedA,
                userA);
            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(updateHidden).StatusCode);
        }

        var (verifyB, verifyUserB) = Open(dbName, tenantB);
        await using (verifyB)
        {
            var stillB = await verifyB.Companies.ForTenant(verifyUserB).AsNoTracking().SingleAsync(c => c.Id == companyBId);
            Assert.Equal("https://halo.example/clients/secret", stillB.HaloPortalUrl);
            Assert.Equal("https://ninja.example/organizations/secret", stillB.NinjaPortalUrl);
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

    [Fact]
    public async Task Cannot_Set_ParentCompany_From_Another_Tenant()
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
            var created = await CompanyEndpoints.CreateAsync(
                new CreateCompanyRequest("Child", "child", ParentCompanyId: companyB.Id),
                db,
                user);
            AssertParentNotFound(created);

            var updated = await CompanyEndpoints.UpdateAsync(
                companyA.Id,
                new UpdateCompanyRequest(ParentCompanyId: companyB.Id),
                db,
                user);
            AssertParentNotFound(updated);

            db.ChangeTracker.Clear();
            var reloaded = await db.Companies.ForTenant(user).SingleAsync(c => c.Id == companyA.Id);
            Assert.Null(reloaded.ParentCompanyId);
            Assert.False(await db.Companies.ForTenant(user).AnyAsync(c => c.Slug == "child"));
        }
    }

    [Fact]
    public async Task Same_Tenant_Parent_Is_Accepted_And_List_Still_Works()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var parent = new Company
        {
            TenantId = tenantA,
            Name = "Parent Co",
            Slug = "parent-co",
            CompanyType = "Holding",
            Nickname = "Parent",
        };
        var poison = new Company { TenantId = tenantB, Name = "Poison Co", Slug = "poison-co" };

        var (seedA, _) = Open(dbName, tenantA);
        await using (seedA)
        {
            seedA.Companies.Add(parent);
            await seedA.SaveChangesAsync();
        }

        var (seedB, _) = Open(dbName, tenantB);
        await using (seedB)
        {
            seedB.Companies.Add(poison);
            await seedB.SaveChangesAsync();
        }

        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var created = await CompanyEndpoints.CreateAsync(
                new CreateCompanyRequest(
                    "Child Co",
                    "child-co",
                    CompanyType: "Subsidiary",
                    Nickname: "Kid",
                    ParentCompanyId: parent.Id,
                    Country: "US",
                    PostalCode: "10001",
                    Fax: "555-0100"),
                db,
                user);
            Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<IStatusCodeHttpResult>(created).StatusCode);

            var createdJson = JsonPayload(created);
            Assert.Equal(parent.Id.ToString(), createdJson.GetProperty("ParentCompanyId").GetGuid().ToString());
            Assert.Equal("Subsidiary", createdJson.GetProperty("CompanyType").GetString());
            Assert.Equal("Kid", createdJson.GetProperty("Nickname").GetString());
            Assert.Equal("US", createdJson.GetProperty("Country").GetString());
            Assert.Equal("10001", createdJson.GetProperty("PostalCode").GetString());
            Assert.Equal("555-0100", createdJson.GetProperty("Fax").GetString());

            var listed = await CompanyEndpoints.ListAsync(null, db, user);
            Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(listed).StatusCode);
            var listJson = JsonPayload(listed);
            Assert.Equal(JsonValueKind.Array, listJson.ValueKind);
            Assert.Equal(2, listJson.GetArrayLength());
            Assert.DoesNotContain(listJson.EnumerateArray(), item => item.GetProperty("Slug").GetString() == "poison-co");

            var childRow = listJson.EnumerateArray().Single(item => item.GetProperty("Slug").GetString() == "child-co");
            Assert.Equal(parent.Id, childRow.GetProperty("ParentCompanyId").GetGuid());
            Assert.Equal("Subsidiary", childRow.GetProperty("CompanyType").GetString());
            Assert.Equal("Kid", childRow.GetProperty("Nickname").GetString());

            var fetched = await CompanyEndpoints.GetAsync(
                childRow.GetProperty("Id").GetGuid(),
                db,
                user);
            Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(fetched).StatusCode);
            var getJson = JsonPayload(fetched);
            Assert.Equal(parent.Id, getJson.GetProperty("ParentCompanyId").GetGuid());
            Assert.Equal("US", getJson.GetProperty("Country").GetString());
            Assert.Equal("10001", getJson.GetProperty("PostalCode").GetString());
            Assert.Equal("555-0100", getJson.GetProperty("Fax").GetString());

            var childId = childRow.GetProperty("Id").GetGuid();
            var updated = await CompanyEndpoints.UpdateAsync(
                childId,
                new UpdateCompanyRequest(CompanyType: "Affiliate", Nickname: "Kiddo", Fax: "555-0199"),
                db,
                user);
            Assert.Equal(StatusCodes.Status204NoContent, Assert.IsAssignableFrom<IStatusCodeHttpResult>(updated).StatusCode);

            db.ChangeTracker.Clear();
            var reloaded = await db.Companies.ForTenant(user).SingleAsync(c => c.Id == childId);
            Assert.Equal(parent.Id, reloaded.ParentCompanyId);
            Assert.Equal("Affiliate", reloaded.CompanyType);
            Assert.Equal("Kiddo", reloaded.Nickname);
            Assert.Equal("555-0199", reloaded.Fax);
        }
    }

    private static void AssertParentNotFound(IResult result)
    {
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, status.StatusCode);
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result);
        Assert.Equal(CompanyEndpoints.ParentCompanyNotFoundMessage, value.Value);
    }

    private static JsonElement JsonPayload(IResult result)
    {
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result);
        var json = JsonSerializer.Serialize(value.Value);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }
}
