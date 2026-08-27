using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Integrations;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

public class IntegrationSyncTests
{
    private sealed class NoopAudit : IAuditService
    {
        public Task LogAsync(string action, string entityType, Guid? entityId = null, string? details = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NoopMcp : IMcpClient
    {
        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{}}""");
    }

    private static (DocuEngAIneDbContext Db, FakeCurrentUser User, IntegrationSyncService Sync) Create()
    {
        var tenantId = Guid.NewGuid();
        var user = new FakeCurrentUser { TenantId = tenantId, ObjectId = Guid.NewGuid().ToString(), Role = UserRole.Owner };
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new DocuEngAIneDbContext(options, user);
        var sync = new IntegrationSyncService(db, user, new NoopMcp(), new NoopAudit());
        return (db, user, sync);
    }

    [Fact]
    public async Task SyncFromPayload_Creates_Company_And_Mapping_With_HaloClientId()
    {
        var (db, user, sync) = Create();
        var connection = new IntegrationConnection
        {
            TenantId = user.TenantId!.Value,
            Provider = IntegrationProvider.Halo,
            DisplayName = "Halo",
            AuthSecretName = "halo-secret",
        };
        db.IntegrationConnections.Add(connection);
        await db.SaveChangesAsync();

        var run = await sync.SyncFromPayloadAsync(connection.Id, [
            new ExternalCompanyDto("halo-100", "ExampleCo", "exampleco", City: "Austin", State: "TX")
        ]);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(1, run.ItemsCreated);

        var company = await db.Companies.SingleAsync();
        Assert.Equal("ExampleCo", company.Name);
        Assert.Equal("halo-100", company.HaloClientId);

        var mapping = await db.IntegrationMappings.SingleAsync();
        Assert.Equal("company", mapping.ExternalType);
        Assert.Equal("halo-100", mapping.ExternalId);
        Assert.Equal(company.Id, mapping.LocalEntityId);
    }

    [Fact]
    public async Task McpServer_Is_Tenant_Scoped()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var a = new DocuEngAIneDbContext(options, new FakeCurrentUser { TenantId = tenantA, ObjectId = "a", Role = UserRole.Owner });
        a.McpServers.Add(new McpServer { TenantId = tenantA, Name = "StackJack", EndpointUrl = "https://example/mcp" });
        await a.SaveChangesAsync();

        await using var b = new DocuEngAIneDbContext(options, new FakeCurrentUser { TenantId = tenantB, ObjectId = "b", Role = UserRole.Owner });
        var forB = await b.McpServers.ForTenant(new FakeCurrentUser { TenantId = tenantB }).ToListAsync();
        Assert.Empty(forB);
    }

    private static async Task<(IntegrationConnection Connection, Company Company)> SeedMappedCompany(
        DocuEngAIneDbContext db, FakeCurrentUser user, bool updateCompanyDetails)
    {
        var connection = new IntegrationConnection
        {
            TenantId = user.TenantId!.Value,
            Provider = IntegrationProvider.Halo,
            DisplayName = "Halo",
            AuthSecretName = "halo-secret",
            UpdateCompanyDetails = updateCompanyDetails,
        };
        db.IntegrationConnections.Add(connection);
        var company = new Company
        {
            TenantId = user.TenantId!.Value,
            Name = "Local Name",
            Slug = "local-name",
            Address = "1 Main",
            City = "Austin",
            State = "TX",
            Website = "https://local.example",
            PrimaryDomain = "local.example",
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.IntegrationMappings.Add(new IntegrationMapping
        {
            TenantId = user.TenantId!.Value,
            IntegrationConnectionId = connection.Id,
            ExternalId = "halo-100",
            ExternalType = "company",
            LocalEntityType = nameof(Company),
            LocalEntityId = company.Id,
        });
        await db.SaveChangesAsync();
        return (connection, company);
    }

    [Fact]
    public async Task SyncFromPayload_Preserves_Company_Name_When_UpdateCompanyDetails_False()
    {
        var (db, user, sync) = Create();
        var (connection, _) = await SeedMappedCompany(db, user, updateCompanyDetails: false);

        var run = await sync.SyncFromPayloadAsync(connection.Id, [
            new ExternalCompanyDto("halo-100", "Remote Name", PrimaryDomain: "remote.example", City: "Dallas", State: "TX", Website: "https://remote.example", Address: "2 Remote")
        ]);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(1, run.ItemsUpdated);
        var company = await db.Companies.SingleAsync();
        Assert.Equal("Local Name", company.Name);
        Assert.Equal("1 Main", company.Address);
        Assert.Equal("Austin", company.City);
        Assert.Equal("TX", company.State);
        Assert.Equal("https://local.example", company.Website);
        Assert.Equal("local.example", company.PrimaryDomain);
        Assert.Equal("halo-100", company.HaloClientId);
    }

    [Fact]
    public async Task SyncFromPayload_Updates_Company_Name_When_UpdateCompanyDetails_True()
    {
        var (db, user, sync) = Create();
        var (connection, _) = await SeedMappedCompany(db, user, updateCompanyDetails: true);

        var run = await sync.SyncFromPayloadAsync(connection.Id, [
            new ExternalCompanyDto("halo-100", "Remote Name", PrimaryDomain: "remote.example", City: "Dallas", State: "TX", Website: "https://remote.example", Address: "2 Remote")
        ]);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(1, run.ItemsUpdated);
        var company = await db.Companies.SingleAsync();
        Assert.Equal("Remote Name", company.Name);
        Assert.Equal("Dallas", company.City);
        Assert.Equal("TX", company.State);
        Assert.Equal("https://remote.example", company.Website);
        Assert.Equal("remote.example", company.PrimaryDomain);
        Assert.Equal("2 Remote", company.Address);
        Assert.Equal("halo-100", company.HaloClientId);
    }

    [Fact]
    public async Task SyncFromPayload_Skips_Inactive_When_SkipInactive_True()
    {
        var (db, user, sync) = Create();
        var connection = new IntegrationConnection
        {
            TenantId = user.TenantId!.Value,
            Provider = IntegrationProvider.Halo,
            DisplayName = "Halo",
            AuthSecretName = "halo-secret",
            SkipInactive = true,
        };
        db.IntegrationConnections.Add(connection);
        await db.SaveChangesAsync();

        var run = await sync.SyncFromPayloadAsync(connection.Id, [
            new ExternalCompanyDto("halo-1", "DeadCo", IsInactive: true),
            new ExternalCompanyDto("halo-2", "LiveCo", IsInactive: false),
        ]);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(1, run.ItemsSkipped);
        Assert.Equal(1, run.ItemsCreated);
        var company = await db.Companies.SingleAsync();
        Assert.Equal("LiveCo", company.Name);
    }
}
