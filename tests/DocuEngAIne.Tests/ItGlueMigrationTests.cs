using System.Text.Json;
using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Core.Mcp;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Integrations;
using DocuEngAIne.Infrastructure.Integrations.Migration;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

/// <summary>
/// One-shot IT Glue import. IT Glue is not a live IntegrationProvider.
/// Fixture shape is the Compact JSON:API envelope: {data:[{id, type:organizations, attributes:{name}}]}.
/// </summary>
public class ItGlueMigrationTests
{
    public const string OrganizationsFixture = """
        {"data":[{"id":"1","type":"organizations","attributes":{"name":"ExampleCo"}}]}
        """;

    public const string MixedSliceFixture = """
        {"data":[
          {"id":"1","type":"organizations","attributes":{"name":"ExampleCo"}},
          {"id":"10","type":"documents","attributes":{"name":"VPN runbook","content":"Connect via the client portal."},"relationships":{"organization":{"data":{"id":"1","type":"organizations"}}}},
          {"id":"20","type":"flexible_assets","attributes":{"name":"Firewall","traits":{"vendor":"Fortinet","password":"s3cret-value","api_key":"sk-live"}}},
          {"id":"99","type":"passwords","attributes":{"name":"Domain admin","password":"should-never-land","username":"admin"}}
        ]}
        """;

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

    private sealed class RecordingItGlueMcp : IMcpClient
    {
        public List<(Guid ServerId, string Tool, string? Args)> Calls { get; } = [];
        public string Body { get; init; } = OrganizationsFixture;

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((mcpServerId, toolName, argumentsJson));
            var wrapped = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "1",
                result = new { content = new[] { new { type = "text", text = Body } } },
            });
            return Task.FromResult(wrapped);
        }
    }

    private static (DocuEngAIneDbContext Db, FakeCurrentUser User, ItGlueMigrationService Service) Create(IMcpClient? mcp = null)
    {
        var user = new FakeCurrentUser { TenantId = Guid.NewGuid(), ObjectId = Guid.NewGuid().ToString(), Role = UserRole.Owner };
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new DocuEngAIneDbContext(options, user);
        var service = new ItGlueMigrationService(db, user, mcp ?? new NoopMcp(), new NoopAudit());
        return (db, user, service);
    }

    private static (DocuEngAIneDbContext Db, FakeCurrentUser User) Open(string dbName, Guid tenantId)
    {
        var user = new FakeCurrentUser { TenantId = tenantId, ObjectId = Guid.NewGuid().ToString(), Role = UserRole.Owner };
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return (new DocuEngAIneDbContext(options, user), user);
    }

    [Fact]
    public void IntegrationProvider_Has_No_ITGlue_Value()
    {
        Assert.DoesNotContain(
            Enum.GetNames<IntegrationProvider>(),
            name => name.Contains("ITGlue", StringComparison.OrdinalIgnoreCase)
                || name.Contains("ItGlue", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Itg", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("itglue", CompanyIdentity.ItGlueKey);
        Assert.Equal("custom", CompanyIdentity.ProviderKey(IntegrationProvider.CustomMcp));
    }

    [Fact]
    public void Parse_Sanitized_JsonApi_Fixture_Maps_Organization_Name()
    {
        var slice = ItGlueJsonApiMapper.Parse(OrganizationsFixture);

        var org = Assert.Single(slice.Organizations);
        Assert.Equal("1", org.ExternalId);
        Assert.Equal("ExampleCo", org.Name);
        Assert.Equal(1, slice.OrganizationRowCount);
        Assert.Empty(slice.Documents);
        Assert.Empty(slice.FlexibleAssets);
        Assert.Equal(0, slice.PasswordsSkipped);
    }

    [Fact]
    public void Parse_Drops_Passwords_And_Secret_Traits()
    {
        var slice = ItGlueJsonApiMapper.Parse(MixedSliceFixture);

        Assert.Equal(1, slice.PasswordsSkipped);
        Assert.Single(slice.Organizations);
        Assert.Single(slice.Documents);
        var asset = Assert.Single(slice.FlexibleAssets);
        Assert.Equal("Firewall", asset.Name);
        Assert.Contains("vendor: Fortinet", asset.Notes, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret-value", asset.Notes, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live", asset.Notes, StringComparison.Ordinal);
        Assert.DoesNotContain("password", asset.Notes, StringComparison.OrdinalIgnoreCase);
        Assert.True(ItGlueJsonApiMapper.IsSecretKey("password"));
        Assert.True(ItGlueJsonApiMapper.IsSecretKey("api_key"));
        Assert.False(ItGlueJsonApiMapper.IsSecretKey("vendor"));
    }

    [Fact]
    public async Task Import_Organizations_Fixture_Creates_Company_With_ItGlue_ExternalId()
    {
        var (db, _, service) = Create();
        var result = await service.ImportAsync(null, OrganizationsFixture);

        Assert.Equal(nameof(SyncRunStatus.Succeeded), result.Status);
        Assert.Equal(1, result.CompaniesCreated);
        Assert.Equal(0, result.CompaniesUpdated);

        var company = await db.Companies.SingleAsync();
        Assert.Equal("ExampleCo", company.Name);
        var ids = CompanyIdentity.ReadExternalIds(company.ExternalIdsJson);
        Assert.Equal("1", ids[CompanyIdentity.ItGlueKey]);
        Assert.Empty(await db.IntegrationConnections.ToListAsync());
    }

    [Fact]
    public async Task Import_Is_Idempotent_On_ItGlue_Id()
    {
        var (db, _, service) = Create();
        var first = await service.ImportAsync(null, OrganizationsFixture);
        var second = await service.ImportAsync(null, OrganizationsFixture);

        Assert.Equal(1, first.CompaniesCreated);
        Assert.Equal(0, first.CompaniesUpdated);
        Assert.Equal(0, second.CompaniesCreated);
        Assert.Equal(1, second.CompaniesUpdated);
        Assert.Equal(1, await db.Companies.CountAsync());
        var ids = CompanyIdentity.ReadExternalIds((await db.Companies.SingleAsync()).ExternalIdsJson);
        Assert.Equal("1", ids[CompanyIdentity.ItGlueKey]);
    }

    [Fact]
    public async Task Import_Converges_Onto_Existing_Company_By_Name()
    {
        var (db, user, service) = Create();
        db.Companies.Add(new Company
        {
            TenantId = user.TenantId!.Value,
            Name = "ExampleCo",
            Slug = "exampleco",
            ExternalIdsJson = CompanyIdentity.UpsertExternalId(null, CompanyIdentity.HaloKey, "halo-100"),
        });
        await db.SaveChangesAsync();

        var result = await service.ImportAsync(null, OrganizationsFixture);

        Assert.Equal(0, result.CompaniesCreated);
        Assert.Equal(1, result.CompaniesUpdated);
        var company = await db.Companies.SingleAsync();
        var ids = CompanyIdentity.ReadExternalIds(company.ExternalIdsJson);
        Assert.Equal("halo-100", ids[CompanyIdentity.HaloKey]);
        Assert.Equal("1", ids[CompanyIdentity.ItGlueKey]);
    }

    [Fact]
    public async Task Import_Documents_And_Flexible_Assets_Without_Secrets()
    {
        var (db, _, service) = Create();
        var result = await service.ImportAsync(null, MixedSliceFixture);

        Assert.Equal(nameof(SyncRunStatus.Succeeded), result.Status);
        Assert.Equal(1, result.CompaniesCreated);
        Assert.Equal(1, result.DocumentsCreated);
        Assert.Equal(1, result.AssetsCreated);
        Assert.Equal(1, result.ItemsSkipped);

        var company = await db.Companies.SingleAsync();
        var document = await db.Documents.SingleAsync();
        Assert.Equal("VPN runbook", document.Title);
        Assert.Equal(ItGlueJsonApiMapper.DocumentSlug("10"), document.Slug);
        Assert.Equal(company.Id, document.CompanyId);
        Assert.Contains("Connect via the client portal.", document.Content);
        Assert.DoesNotContain("should-never-land", document.Content);

        var asset = await db.Assets.Include(a => a.CustomFieldValues).ThenInclude(v => v.FieldDefinition).SingleAsync();
        Assert.Equal("Firewall", asset.Name);
        Assert.Contains("Fortinet", asset.Notes);
        Assert.DoesNotContain("s3cret-value", asset.Notes);
        Assert.DoesNotContain("sk-live", asset.Notes);
        Assert.Equal("20", Assert.Single(asset.CustomFieldValues).Value);

        Assert.Empty(await db.KeeperLinks.ToListAsync());
        Assert.DoesNotContain(await db.Documents.ToListAsync(), d => d.Title.Contains("Domain admin", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Import_Documents_And_Assets_Are_Idempotent_On_ItGlue_Ids()
    {
        var (db, _, service) = Create();
        await service.ImportAsync(null, MixedSliceFixture);
        var second = await service.ImportAsync(null, MixedSliceFixture);

        Assert.Equal(0, second.CompaniesCreated);
        Assert.Equal(1, second.CompaniesUpdated);
        Assert.Equal(0, second.DocumentsCreated);
        Assert.Equal(1, second.DocumentsUpdated);
        Assert.Equal(0, second.AssetsCreated);
        Assert.Equal(1, second.AssetsUpdated);
        Assert.Equal(1, await db.Companies.CountAsync());
        Assert.Equal(1, await db.Documents.CountAsync());
        Assert.Equal(1, await db.Assets.CountAsync());
    }

    [Fact]
    public async Task Import_From_Compact_Calls_Itg_List_Organizations()
    {
        var mcp = new RecordingItGlueMcp();
        var (db, user, service) = Create(mcp);
        var server = new McpServer
        {
            TenantId = user.TenantId!.Value,
            Name = McpServerDefaults.StackJackCompactName,
            Kind = McpServerKind.StackJackCompact,
            Transport = McpTransport.Http,
            EndpointUrl = McpServerDefaults.StackJackCompactEndpoint,
            AuthSecretName = "kv-stackjack-compact",
        };
        db.McpServers.Add(server);
        await db.SaveChangesAsync();

        var result = await service.ImportAsync(server.Id, null);

        Assert.Equal(nameof(SyncRunStatus.Succeeded), result.Status);
        Assert.Equal(1, result.CompaniesCreated);
        Assert.Equal(ItGlueJsonApiMapper.OrganizationsToolName, Assert.Single(mcp.Calls).Tool);
        Assert.DoesNotContain(mcp.Calls, c => c.Tool.Contains("password", StringComparison.OrdinalIgnoreCase));
        var ids = CompanyIdentity.ReadExternalIds((await db.Companies.SingleAsync()).ExternalIdsJson);
        Assert.Equal("1", ids[CompanyIdentity.ItGlueKey]);
    }

    [Fact]
    public async Task Other_Tenant_McpServer_Returns_404_And_Does_Not_Call_Mcp()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var mcp = new RecordingItGlueMcp();

        Guid serverBId;
        var (dbB, userB) = Open(dbName, tenantB);
        await using (dbB)
        {
            var server = new McpServer
            {
                TenantId = tenantB,
                Name = "Compact B",
                Kind = McpServerKind.StackJackCompact,
                EndpointUrl = McpServerDefaults.StackJackCompactEndpoint,
            };
            dbB.McpServers.Add(server);
            await dbB.SaveChangesAsync();
            serverBId = server.Id;
        }

        var (dbA, userA) = Open(dbName, tenantA);
        await using (dbA)
        {
            var service = new ItGlueMigrationService(dbA, userA, mcp, new NoopAudit());
            var result = await ItGlueMigrationEndpoints.ImportAsync(
                new ItGlueImportRequest(McpServerId: serverBId), service, dbA, userA);

            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
            Assert.Empty(mcp.Calls);
            Assert.Empty(await dbA.McpServers.ForTenant(userA).ToListAsync());
            Assert.Empty(await dbA.Companies.ForTenant(userA).ToListAsync());
        }
    }

    [Fact]
    public async Task Endpoint_Imports_Raw_JsonApi_Fixture()
    {
        var (db, user, service) = Create();
        using var fixture = JsonDocument.Parse(OrganizationsFixture);
        var request = new ItGlueImportRequest(Data: fixture.RootElement.GetProperty("data").Clone());

        var result = await ItGlueMigrationEndpoints.ImportAsync(request, service, db, user);

        Assert.Equal(StatusCodes.Status200OK, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal("ExampleCo", (await db.Companies.SingleAsync()).Name);
    }

    [Fact]
    public async Task Endpoint_Without_McpServer_Or_Payload_Is_BadRequest()
    {
        var (db, user, service) = Create();
        var result = await ItGlueMigrationEndpoints.ImportAsync(new ItGlueImportRequest(), service, db, user);
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }
}
