using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Infrastructure.Data;
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
}
