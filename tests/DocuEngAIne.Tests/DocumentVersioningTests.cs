using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

public class DocumentVersioningTests
{
    private static (DocuEngAIneDbContext Db, Guid TenantId, Guid UserId, UserRole Role, FakeCurrentUser User) CreateContext(UserRole role = UserRole.Owner)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var user = new FakeCurrentUser { TenantId = tenantId, ObjectId = userId.ToString(), Role = role };
        var db = new DocuEngAIneDbContext(options, user);

        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Test Tenant", Slug = "test" });
        db.Users.Add(new User
        {
            Id = userId,
            TenantId = tenantId,
            EntraObjectId = userId.ToString(),
            Email = "test@example.com",
            Role = role,
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();

        return (db, tenantId, userId, role, user);
    }

    [Fact]
    public async Task UpdateDocument_Creates_Version()
    {
        var (db, tenantId, _, _, user) = CreateContext();
        var doc = new Document
        {
            TenantId = tenantId,
            Title = "Original",
            Slug = "original",
            Content = "Original content",
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var fetched = await db.Documents.ForTenant(user).FirstAsync(d => d.Id == doc.Id);
        var next = (await db.DocumentVersions.Where(v => v.DocumentId == doc.Id).MaxAsync(v => (int?)v.VersionNumber) ?? 0) + 1;
        db.DocumentVersions.Add(new DocumentVersion
        {
            DocumentId = fetched.Id,
            VersionNumber = next,
            Title = fetched.Title,
            Slug = fetched.Slug,
            Content = fetched.Content,
        });

        fetched.Title = "Updated";
        fetched.Content = "Updated content";
        await db.SaveChangesAsync();

        var versions = await db.DocumentVersions.Where(v => v.DocumentId == doc.Id).ToListAsync();
        Assert.Single(versions);
        Assert.Equal("Original", versions[0].Title);
        Assert.Equal("Original content", versions[0].Content);
    }

    [Fact]
    public async Task Document_Has_Versions_Navigation()
    {
        var (db, tenantId, _, _, _) = CreateContext();
        var doc = new Document
        {
            TenantId = tenantId,
            Title = "Doc",
            Slug = "doc",
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var fetched = await db.Documents.Include(d => d.Versions).FirstAsync(d => d.Id == doc.Id);
        Assert.Empty(fetched.Versions);
    }
}

public class DocumentVersionTenantIsolationTests
{
    private static DocuEngAIneDbContext Open(string dbName, FakeCurrentUser user) =>
        new(new DbContextOptionsBuilder<DocuEngAIneDbContext>().UseInMemoryDatabase(dbName).Options, user);

    [Fact]
    public async Task Version_History_Is_Not_Readable_From_Another_Tenant()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var userA = new FakeCurrentUser { TenantId = tenantA, ObjectId = Guid.NewGuid().ToString() };
        var userB = new FakeCurrentUser { TenantId = tenantB, ObjectId = Guid.NewGuid().ToString() };

        Guid docId;
        Guid versionId;
        await using (var dbA = Open(dbName, userA))
        {
            var doc = new Document { TenantId = tenantA, Title = "A-Secret", Slug = "a-secret", Content = "current" };
            dbA.Documents.Add(doc);
            await dbA.SaveChangesAsync();
            var version = new DocumentVersion
            {
                DocumentId = doc.Id,
                VersionNumber = 1,
                Title = "A-Secret",
                Content = "tenant-a-confidential-history",
            };
            dbA.DocumentVersions.Add(version);
            await dbA.SaveChangesAsync();
            docId = doc.Id;
            versionId = version.Id;
        }

        await using (var dbA = Open(dbName, userA))
        {
            var own = await DocumentEndpoints.ListVersionsAsync(docId, dbA, userA);
            var list = Assert.IsAssignableFrom<IValueHttpResult>(own);
            Assert.Single(Assert.IsAssignableFrom<IEnumerable<DocumentVersionListItem>>(list.Value));

            var detail = await DocumentEndpoints.GetVersionAsync(docId, versionId, dbA, userA);
            var body = Assert.IsType<DocumentVersionDetail>(Assert.IsAssignableFrom<IValueHttpResult>(detail).Value);
            Assert.Equal("tenant-a-confidential-history", body.Content);
        }

        await using (var dbB = Open(dbName, userB))
        {
            var foreignList = await DocumentEndpoints.ListVersionsAsync(docId, dbB, userB);
            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(foreignList).StatusCode);

            var foreignDetail = await DocumentEndpoints.GetVersionAsync(docId, versionId, dbB, userB);
            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(foreignDetail).StatusCode);
        }
    }
}
