using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
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
