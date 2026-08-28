using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

/// <summary>
/// Documents, runbooks and Keeper links accept an optional CompanyId on create and update,
/// exactly as assets do. A company from another tenant is rejected with 400, and a null
/// CompanyId on update means "leave unchanged" (never "clear"), matching every other
/// optional field on those update endpoints.
/// </summary>
public class CompanyAttachmentTests
{
    private const string CompanyNotFoundMessage = "Company not found.";
    private const string KeeperUrl = "https://keeper.example/record";

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

    private static void AssertCompanyNotFound(IResult result)
    {
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, status.StatusCode);
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result);
        Assert.Equal(CompanyNotFoundMessage, value.Value);
    }

    private static void AssertStatus(int expected, IResult result) =>
        Assert.Equal(expected, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);

    /// <summary>
    /// Tenant A owns CompanyA and SecondCompanyA; tenant B owns CompanyB (the cross-tenant poison id).
    /// </summary>
    private static async Task<(string DbName, Guid TenantA, Guid CompanyA, Guid SecondCompanyA, Guid CompanyB)> SeedAsync()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var companyA = new Company { TenantId = tenantA, Name = "ExampleCo", Slug = "exampleco" };
        var secondCompanyA = new Company { TenantId = tenantA, Name = "SecondCo", Slug = "secondco" };
        var companyB = new Company { TenantId = tenantB, Name = "PoisonCo", Slug = "poisonco" };

        var (dbA, _) = Open(dbName, tenantA);
        await using (dbA)
        {
            dbA.Companies.Add(companyA);
            dbA.Companies.Add(secondCompanyA);
            await dbA.SaveChangesAsync();
        }

        var (dbB, _) = Open(dbName, tenantB);
        await using (dbB)
        {
            dbB.Companies.Add(companyB);
            await dbB.SaveChangesAsync();
        }

        return (dbName, tenantA, companyA.Id, secondCompanyA.Id, companyB.Id);
    }

    // ----- Document -----

    [Fact]
    public async Task Document_Create_Attaches_Own_Tenant_Company()
    {
        var (dbName, tenantA, companyA, _, _) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var created = await DocumentEndpoints.CreateAsync(
                new CreateDocumentRequest("Onboarding", "onboarding", null, null, null, true, companyA),
                db,
                user);
            AssertStatus(StatusCodes.Status201Created, created);

            db.ChangeTracker.Clear();
            var stored = await db.Documents.ForTenant(user).AsNoTracking().SingleAsync();
            Assert.Equal(companyA, stored.CompanyId);

            var listed = await DocumentEndpoints.ListAsync(db, user);
            Assert.Single(listed);
            Assert.Equal(companyA, listed[0].CompanyId);
        }
    }

    [Fact]
    public async Task Document_Create_Rejects_Other_Tenant_Company()
    {
        var (dbName, tenantA, _, _, companyB) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var created = await DocumentEndpoints.CreateAsync(
                new CreateDocumentRequest("Leak", "leak", null, null, null, true, companyB),
                db,
                user);
            AssertCompanyNotFound(created);

            db.ChangeTracker.Clear();
            Assert.False(await db.Documents.ForTenant(user).AnyAsync());
        }
    }

    [Fact]
    public async Task Document_Update_Rejects_Other_Tenant_Company()
    {
        var (dbName, tenantA, companyA, _, companyB) = await SeedAsync();
        var docId = Guid.NewGuid();

        var (seed, _) = Open(dbName, tenantA);
        await using (seed)
        {
            seed.Documents.Add(new Document { Id = docId, TenantId = tenantA, Title = "Doc", Slug = "doc", CompanyId = companyA });
            await seed.SaveChangesAsync();
        }

        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var updated = await DocumentEndpoints.UpdateAsync(
                docId,
                new UpdateDocumentRequest(null, null, null, null, null, null, null, companyB),
                db,
                user);
            AssertCompanyNotFound(updated);

            db.ChangeTracker.Clear();
            var stored = await db.Documents.ForTenant(user).AsNoTracking().SingleAsync(d => d.Id == docId);
            Assert.Equal(companyA, stored.CompanyId);
        }
    }

    [Fact]
    public async Task Document_Update_Null_CompanyId_Leaves_Company_Unchanged()
    {
        var (dbName, tenantA, companyA, secondCompanyA, _) = await SeedAsync();
        var docId = Guid.NewGuid();

        var (seed, _) = Open(dbName, tenantA);
        await using (seed)
        {
            seed.Documents.Add(new Document { Id = docId, TenantId = tenantA, Title = "Doc", Slug = "doc", CompanyId = companyA });
            await seed.SaveChangesAsync();
        }

        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            // A supplied own-tenant company does move the document.
            var moved = await DocumentEndpoints.UpdateAsync(
                docId,
                new UpdateDocumentRequest(null, null, null, null, null, null, null, secondCompanyA),
                db,
                user);
            AssertStatus(StatusCodes.Status204NoContent, moved);

            db.ChangeTracker.Clear();
            Assert.Equal(
                secondCompanyA,
                (await db.Documents.ForTenant(user).AsNoTracking().SingleAsync(d => d.Id == docId)).CompanyId);

            // An omitted company leaves it where it is; it does not clear the field.
            var renamed = await DocumentEndpoints.UpdateAsync(
                docId,
                new UpdateDocumentRequest("Renamed", null, null, null, null, null, null, null),
                db,
                user);
            AssertStatus(StatusCodes.Status204NoContent, renamed);

            db.ChangeTracker.Clear();
            var stored = await db.Documents.ForTenant(user).AsNoTracking().SingleAsync(d => d.Id == docId);
            Assert.Equal(secondCompanyA, stored.CompanyId);
            Assert.Equal("Renamed", stored.Title);
        }
    }

    // ----- Runbook -----

    [Fact]
    public async Task Runbook_Create_Attaches_Own_Tenant_Company()
    {
        var (dbName, tenantA, companyA, _, _) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var created = await RunbookEndpoints.CreateAsync(
                new CreateRunbookRequest("Onboard client", "onboard-client", null, null, true, null, companyA),
                db,
                user);
            AssertStatus(StatusCodes.Status201Created, created);

            db.ChangeTracker.Clear();
            var stored = await db.Runbooks.ForTenant(user).AsNoTracking().SingleAsync();
            Assert.Equal(companyA, stored.CompanyId);

            var listed = await RunbookEndpoints.ListPublishedAsync(db, user);
            Assert.Single(listed);
            Assert.Equal(companyA, listed[0].CompanyId);
        }
    }

    [Fact]
    public async Task Runbook_Create_Rejects_Other_Tenant_Company()
    {
        var (dbName, tenantA, _, _, companyB) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var created = await RunbookEndpoints.CreateAsync(
                new CreateRunbookRequest("Leak SOP", "leak-sop", null, null, true, null, companyB),
                db,
                user);
            AssertCompanyNotFound(created);

            db.ChangeTracker.Clear();
            Assert.False(await db.Runbooks.ForTenant(user).AnyAsync());
        }
    }

    [Fact]
    public async Task Runbook_Update_Rejects_Other_Tenant_Company()
    {
        var (dbName, tenantA, companyA, _, companyB) = await SeedAsync();
        var runbookId = Guid.NewGuid();

        var (seed, _) = Open(dbName, tenantA);
        await using (seed)
        {
            seed.Runbooks.Add(new Runbook { Id = runbookId, TenantId = tenantA, Title = "SOP", Slug = "sop", CompanyId = companyA });
            await seed.SaveChangesAsync();
        }

        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var updated = await RunbookEndpoints.UpdateAsync(
                runbookId,
                new UpdateRunbookRequest(null, null, null, null, null, null, companyB),
                db,
                user);
            AssertCompanyNotFound(updated);

            db.ChangeTracker.Clear();
            var stored = await db.Runbooks.ForTenant(user).AsNoTracking().SingleAsync(r => r.Id == runbookId);
            Assert.Equal(companyA, stored.CompanyId);
        }
    }

    [Fact]
    public async Task Runbook_Update_Null_CompanyId_Leaves_Company_Unchanged()
    {
        var (dbName, tenantA, companyA, secondCompanyA, _) = await SeedAsync();
        var runbookId = Guid.NewGuid();

        var (seed, _) = Open(dbName, tenantA);
        await using (seed)
        {
            seed.Runbooks.Add(new Runbook { Id = runbookId, TenantId = tenantA, Title = "SOP", Slug = "sop", CompanyId = companyA });
            await seed.SaveChangesAsync();
        }

        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var moved = await RunbookEndpoints.UpdateAsync(
                runbookId,
                new UpdateRunbookRequest(null, null, null, null, null, null, secondCompanyA),
                db,
                user);
            AssertStatus(StatusCodes.Status204NoContent, moved);

            db.ChangeTracker.Clear();
            Assert.Equal(
                secondCompanyA,
                (await db.Runbooks.ForTenant(user).AsNoTracking().SingleAsync(r => r.Id == runbookId)).CompanyId);

            var renamed = await RunbookEndpoints.UpdateAsync(
                runbookId,
                new UpdateRunbookRequest("Renamed SOP", null, null, null, null, null, null),
                db,
                user);
            AssertStatus(StatusCodes.Status204NoContent, renamed);

            db.ChangeTracker.Clear();
            var stored = await db.Runbooks.ForTenant(user).AsNoTracking().SingleAsync(r => r.Id == runbookId);
            Assert.Equal(secondCompanyA, stored.CompanyId);
            Assert.Equal("Renamed SOP", stored.Title);
        }
    }

    // ----- Keeper link -----

    [Fact]
    public async Task KeeperLink_Create_Attaches_Own_Tenant_Company()
    {
        var (dbName, tenantA, companyA, _, _) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var created = await KeeperLinkEndpoints.CreateAsync(
                new CreateKeeperLinkRequest("Firewall admin", null, KeeperUrl, null, null, null, null, companyA),
                db,
                user);
            AssertStatus(StatusCodes.Status201Created, created);

            db.ChangeTracker.Clear();
            var stored = await db.KeeperLinks.ForTenant(user).AsNoTracking().SingleAsync();
            Assert.Equal(companyA, stored.CompanyId);
        }
    }

    [Fact]
    public async Task KeeperLink_Create_Rejects_Other_Tenant_Company()
    {
        var (dbName, tenantA, _, _, companyB) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var created = await KeeperLinkEndpoints.CreateAsync(
                new CreateKeeperLinkRequest("Leak vault", null, KeeperUrl, null, null, null, null, companyB),
                db,
                user);
            AssertCompanyNotFound(created);

            db.ChangeTracker.Clear();
            Assert.False(await db.KeeperLinks.ForTenant(user).AnyAsync());
        }
    }

    [Fact]
    public async Task KeeperLink_Update_Rejects_Other_Tenant_Company()
    {
        var (dbName, tenantA, companyA, _, companyB) = await SeedAsync();
        var linkId = Guid.NewGuid();

        var (seed, _) = Open(dbName, tenantA);
        await using (seed)
        {
            seed.KeeperLinks.Add(new KeeperLink { Id = linkId, TenantId = tenantA, Name = "Vault", KeeperRecordUrl = KeeperUrl, CompanyId = companyA });
            await seed.SaveChangesAsync();
        }

        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var updated = await KeeperLinkEndpoints.UpdateAsync(
                linkId,
                new UpdateKeeperLinkRequest(null, null, null, null, null, null, null, companyB),
                db,
                user);
            AssertCompanyNotFound(updated);

            db.ChangeTracker.Clear();
            var stored = await db.KeeperLinks.ForTenant(user).AsNoTracking().SingleAsync(k => k.Id == linkId);
            Assert.Equal(companyA, stored.CompanyId);
        }
    }

    [Fact]
    public async Task KeeperLink_Update_Null_CompanyId_Leaves_Company_Unchanged()
    {
        var (dbName, tenantA, companyA, secondCompanyA, _) = await SeedAsync();
        var linkId = Guid.NewGuid();

        var (seed, _) = Open(dbName, tenantA);
        await using (seed)
        {
            seed.KeeperLinks.Add(new KeeperLink { Id = linkId, TenantId = tenantA, Name = "Vault", KeeperRecordUrl = KeeperUrl, CompanyId = companyA });
            await seed.SaveChangesAsync();
        }

        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var moved = await KeeperLinkEndpoints.UpdateAsync(
                linkId,
                new UpdateKeeperLinkRequest(null, null, null, null, null, null, null, secondCompanyA),
                db,
                user);
            AssertStatus(StatusCodes.Status204NoContent, moved);

            db.ChangeTracker.Clear();
            Assert.Equal(
                secondCompanyA,
                (await db.KeeperLinks.ForTenant(user).AsNoTracking().SingleAsync(k => k.Id == linkId)).CompanyId);

            var renamed = await KeeperLinkEndpoints.UpdateAsync(
                linkId,
                new UpdateKeeperLinkRequest("Renamed vault", null, null, null, null, null, null, null),
                db,
                user);
            AssertStatus(StatusCodes.Status204NoContent, renamed);

            db.ChangeTracker.Clear();
            var stored = await db.KeeperLinks.ForTenant(user).AsNoTracking().SingleAsync(k => k.Id == linkId);
            Assert.Equal(secondCompanyA, stored.CompanyId);
            Assert.Equal("Renamed vault", stored.Name);
        }
    }
}
