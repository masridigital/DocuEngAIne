using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

public class RunbookTests
{
    private static (DocuEngAIneDbContext Db, Guid TenantId) CreateContext()
    {
        var tenantId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var currentUser = new FakeCurrentUser { TenantId = tenantId, Role = UserRole.Owner };
        var db = new DocuEngAIneDbContext(options, currentUser);
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Test", Slug = "test" });
        db.SaveChanges();
        db.ChangeTracker.Clear();
        return (db, tenantId);
    }

    [Fact]
    public async Task Runbook_Stores_Ordered_Steps()
    {
        var (db, tenantId) = CreateContext();

        var runbook = new Runbook
        {
            TenantId = tenantId,
            Title = "Onboard client",
            Slug = "onboard-client",
            Steps =
            [
                new RunbookStep { Order = 1, Title = "Create tenant" },
                new RunbookStep { Order = 2, Title = "Add primary contact" },
                new RunbookStep { Order = 3, Title = "Configure policies" },
            ],
        };

        db.Runbooks.Add(runbook);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var fetched = await db.Runbooks.Include(r => r.Steps.OrderBy(s => s.Order)).FirstAsync(r => r.Id == runbook.Id);
        Assert.Equal(3, fetched.Steps.Count);
        Assert.Equal("Create tenant", fetched.Steps.First().Title);
    }

}
