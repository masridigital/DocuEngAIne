using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

public class FolderTests
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
        DocumentFolder FolderA,
        DocumentFolder FolderB,
        Document DocA,
        Document DocB,
        string DbName)> SeedAsync()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var companyA = new Company { TenantId = tenantA, Name = "ExampleCo", Slug = "exampleco" };
        var companyB = new Company { TenantId = tenantB, Name = "PoisonCo", Slug = "poisonco" };
        var folderA = new DocumentFolder { TenantId = tenantA, Name = "Onboarding", CompanyId = companyA.Id };
        var folderB = new DocumentFolder { TenantId = tenantB, Name = "Poison-Folder", CompanyId = companyB.Id };

        Document docA;
        Document docB;

        var (dbA, _) = Open(dbName, tenantA);
        await using (dbA)
        {
            dbA.Companies.Add(companyA);
            dbA.DocumentFolders.Add(folderA);
            await dbA.SaveChangesAsync();

            docA = new Document
            {
                TenantId = tenantA,
                Title = "Welcome",
                Slug = "welcome",
                CompanyId = companyA.Id,
                FolderId = folderA.Id,
            };
            dbA.Documents.Add(docA);
            dbA.Documents.Add(new Document { TenantId = tenantA, Title = "Unfiled A", Slug = "unfiled-a" });
            await dbA.SaveChangesAsync();
        }

        var (dbB, _) = Open(dbName, tenantB);
        await using (dbB)
        {
            dbB.Companies.Add(companyB);
            dbB.DocumentFolders.Add(folderB);
            await dbB.SaveChangesAsync();

            docB = new Document
            {
                TenantId = tenantB,
                Title = "Poison-Doc",
                Slug = "poison-doc",
                CompanyId = companyB.Id,
                FolderId = folderB.Id,
            };
            dbB.Documents.Add(docB);
            await dbB.SaveChangesAsync();
        }

        return (tenantA, tenantB, companyA.Id, companyB.Id, folderA, folderB, docA, docB, dbName);
    }

    [Fact]
    public async Task ForTenant_Does_Not_Leak_Other_Tenant_Folders()
    {
        var (tenantA, tenantB, _, _, _, _, _, _, dbName) = await SeedAsync();

        var (dbA, userA) = Open(dbName, tenantA);
        await using (dbA)
        {
            var listed = await FolderEndpoints.ListAsync(dbA, userA);
            Assert.Single(listed);
            Assert.Equal("Onboarding", listed[0].Name);
            Assert.DoesNotContain(listed, f => f.Name == "Poison-Folder");

            var hidden = await dbA.DocumentFolders.ForTenant(userA).FirstOrDefaultAsync(f => f.Name == "Poison-Folder");
            Assert.Null(hidden);
        }

        var (dbB, userB) = Open(dbName, tenantB);
        await using (dbB)
        {
            var listed = await FolderEndpoints.ListAsync(dbB, userB);
            Assert.Single(listed);
            Assert.Equal("Poison-Folder", listed[0].Name);
        }
    }

    [Fact]
    public async Task Cannot_Put_Document_In_Other_Tenant_Folder()
    {
        var (tenantA, _, _, _, _, folderB, docA, _, dbName) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var missing = await FolderEndpoints.EnsureFolderInTenantAsync(db, user, folderB.Id);
            Assert.NotNull(missing);
            AssertBadRequest(missing, FolderEndpoints.FolderNotFoundMessage);

            var create = await DocumentEndpoints.CreateAsync(
                new CreateDocumentRequest("Leak", "leak", null, null, null, true, null, folderB.Id),
                db,
                user);
            AssertBadRequest(create, FolderEndpoints.FolderNotFoundMessage);

            var update = await DocumentEndpoints.UpdateAsync(
                docA.Id,
                new UpdateDocumentRequest(null, null, null, null, null, null, null, null, folderB.Id),
                db,
                user);
            AssertBadRequest(update, FolderEndpoints.FolderNotFoundMessage);

            db.ChangeTracker.Clear();
            var fetched = await db.Documents.ForTenant(user).SingleAsync(d => d.Id == docA.Id);
            Assert.NotEqual(folderB.Id, fetched.FolderId);
        }
    }

    [Fact]
    public async Task Own_Tenant_Folder_Attaches_To_Document()
    {
        var (tenantA, _, _, _, folderA, _, _, _, dbName) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            Assert.Null(await FolderEndpoints.EnsureFolderInTenantAsync(db, user, folderA.Id));
            Assert.Null(await FolderEndpoints.EnsureFolderInTenantAsync(db, user, null));

            var created = await DocumentEndpoints.CreateAsync(
                new CreateDocumentRequest("In folder", "in-folder", null, null, null, true, null, folderA.Id),
                db,
                user);
            Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<IStatusCodeHttpResult>(created).StatusCode);

            var listed = await DocumentEndpoints.ListAsync(db, user, folderId: folderA.Id);
            Assert.Contains(listed, d => d.Title == "Welcome");
            Assert.Contains(listed, d => d.Title == "In folder");
            Assert.DoesNotContain(listed, d => d.Title == "Unfiled A");
            Assert.All(listed, d => Assert.Equal(folderA.Id, d.FolderId));
        }
    }

    [Fact]
    public async Task List_Documents_By_FolderId_Does_Not_Leak_Other_Tenant()
    {
        var (tenantA, _, _, _, folderA, folderB, _, _, dbName) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var own = await DocumentEndpoints.ListAsync(db, user, folderId: folderA.Id);
            Assert.Single(own);
            Assert.Equal("Welcome", own[0].Title);

            var leaked = await DocumentEndpoints.ListAsync(db, user, folderId: folderB.Id);
            Assert.Empty(leaked);

            var all = await DocumentEndpoints.ListAsync(db, user);
            Assert.Contains(all, d => d.Title == "Welcome");
            Assert.Contains(all, d => d.Title == "Unfiled A");
            Assert.DoesNotContain(all, d => d.Title == "Poison-Doc");
        }
    }

    [Fact]
    public async Task Cannot_Nest_Folder_Under_Other_Tenant_Parent()
    {
        var (tenantA, _, _, _, _, folderB, _, _, dbName) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var created = await FolderEndpoints.CreateAsync(
                new CreateFolderRequest("Child", folderB.Id),
                db,
                user);
            AssertBadRequest(created, FolderEndpoints.FolderNotFoundMessage);

            var own = await FolderEndpoints.CreateAsync(new CreateFolderRequest("Network"), db, user);
            Assert.Equal(StatusCodes.Status201Created, Assert.IsAssignableFrom<IStatusCodeHttpResult>(own).StatusCode);

            var listed = await FolderEndpoints.ListAsync(db, user);
            Assert.DoesNotContain(listed, f => f.ParentId == folderB.Id);
        }
    }

    [Fact]
    public async Task Company_Scoped_Folder_List_Empty_For_Other_Tenant_Company()
    {
        var (tenantA, _, _, companyB, _, _, _, _, dbName) = await SeedAsync();
        var (db, user) = Open(dbName, tenantA);
        await using (db)
        {
            var leaked = await FolderEndpoints.ListAsync(db, user, companyId: companyB);
            Assert.Empty(leaked);
        }
    }
}
