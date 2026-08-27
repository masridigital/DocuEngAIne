using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

public class KeeperLinkTests
{
    private static (DocuEngAIneDbContext Db, AuditService Audit, Guid TenantId, Guid UserId) CreateContext()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var currentUser = new FakeCurrentUser
        {
            TenantId = tenantId,
            ObjectId = userId.ToString(),
            Role = UserRole.Owner,
        };

        var db = new DocuEngAIneDbContext(options, currentUser);
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Test", Slug = "test" });
        db.Users.Add(new User
        {
            Id = userId,
            TenantId = tenantId,
            EntraObjectId = userId.ToString(),
            Email = "test@example.com",
            Role = UserRole.Owner,
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();

        var httpContextAccessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var audit = new AuditService(db, currentUser, httpContextAccessor);
        return (db, audit, tenantId, userId);
    }

    [Fact]
    public async Task Reveal_KeeperLink_Logs_Audit()
    {
        var (db, audit, tenantId, _) = CreateContext();
        var link = new KeeperLink
        {
            TenantId = tenantId,
            Name = "Router admin",
            KeeperRecordUrl = "https://keepersecurity.com/vault/#detail/abc123",
        };
        db.KeeperLinks.Add(link);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await audit.LogAsync("KeeperLink.Reveal", nameof(KeeperLink), link.Id, $"User revealed link '{link.Name}'");

        var logs = await db.AuditLogs.Where(a => a.EntityId == link.Id).ToListAsync();
        Assert.Single(logs);
        Assert.Equal("KeeperLink.Reveal", logs[0].Action);
    }

    [Fact]
    public async Task KeeperLink_Stores_No_Secret_Value()
    {
        var (db, _, tenantId, _) = CreateContext();
        var link = new KeeperLink
        {
            TenantId = tenantId,
            Name = "Switch",
            KeeperRecordUrl = "https://keepersecurity.com/vault/#detail/xyz",
        };
        db.KeeperLinks.Add(link);
        await db.SaveChangesAsync();

        Assert.Null(link.GetType().GetProperty("EncryptedValue"));
    }
}
