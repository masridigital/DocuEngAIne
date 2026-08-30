using System.Text.Json;
using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Core.Mcp;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Integrations;
using Microsoft.AspNetCore.Http;
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

    private sealed class RecordingMcp : IMcpClient
    {
        public List<(Guid ServerId, string Tool, string? Args)> Calls { get; } = [];
        public List<object> Clients { get; init; } = [];
        public int PageSizeOverride { get; init; }

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((mcpServerId, toolName, argumentsJson));
            var pageNo = 1;
            var pageSize = PageSizeOverride > 0 ? PageSizeOverride : HaloClientMapper.DefaultPageSize;
            if (!string.IsNullOrWhiteSpace(argumentsJson))
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.TryGetProperty("pageNo", out var p))
                    pageNo = p.GetInt32();
                if (PageSizeOverride == 0 && doc.RootElement.TryGetProperty("pageSize", out var s))
                    pageSize = s.GetInt32();
            }

            var slice = Clients.Skip((pageNo - 1) * pageSize).Take(pageSize).ToList();
            var inner = JsonSerializer.Serialize(new { clients = slice });
            var body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "1",
                result = new { content = new[] { new { type = "text", text = inner } } },
            });
            return Task.FromResult(body);
        }
    }

    private sealed class RecordingNinjaMcp : IMcpClient
    {
        public List<(Guid ServerId, string Tool, string? Args)> Calls { get; } = [];
        public string OrganizationsJson { get; init; } = "[]";

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((mcpServerId, toolName, argumentsJson));
            int? after = null;
            var pageSize = NinjaOrganizationMapper.DefaultPageSize;
            if (!string.IsNullOrWhiteSpace(argumentsJson))
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.TryGetProperty("after", out var a) && a.ValueKind == JsonValueKind.Number)
                    after = a.GetInt32();
                if (doc.RootElement.TryGetProperty("pageSize", out var s) && s.ValueKind == JsonValueKind.Number)
                    pageSize = s.GetInt32();
            }

            var inner = SliceOrganizationsJson(OrganizationsJson, after, pageSize);
            var body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "1",
                result = new { content = new[] { new { type = "text", text = inner } } },
            });
            return Task.FromResult(body);
        }

        private static string SliceOrganizationsJson(string json, int? after, int pageSize)
        {
            using var doc = JsonDocument.Parse(json);
            var items = new List<string>();
            foreach (var org in doc.RootElement.EnumerateArray())
            {
                var id = org.GetProperty("id").GetInt32();
                if (after is int afterId && id <= afterId)
                    continue;
                items.Add(org.GetRawText());
                if (items.Count >= pageSize)
                    break;
            }
            return "[" + string.Join(",", items) + "]";
        }
    }

    private static (DocuEngAIneDbContext Db, FakeCurrentUser User, IntegrationSyncService Sync) Create(IMcpClient? mcp = null)
    {
        var tenantId = Guid.NewGuid();
        var user = new FakeCurrentUser { TenantId = tenantId, ObjectId = Guid.NewGuid().ToString(), Role = UserRole.Owner };
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new DocuEngAIneDbContext(options, user);
        var sync = new IntegrationSyncService(db, user, mcp ?? new NoopMcp(), new NoopAudit());
        return (db, user, sync);
    }

    private static (DocuEngAIneDbContext Db, FakeCurrentUser User) Open(string dbName, Guid tenantId)
    {
        var user = new FakeCurrentUser { TenantId = tenantId, ObjectId = Guid.NewGuid().ToString(), Role = UserRole.Owner };
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return (new DocuEngAIneDbContext(options, user), user);
    }

    private static async Task<(McpServer Server, IntegrationConnection Connection)> SeedHaloCompactAsync(
        DocuEngAIneDbContext db, FakeCurrentUser user, bool skipInactive = true, bool updateCompanyDetails = false)
    {
        var server = new McpServer
        {
            TenantId = user.TenantId!.Value,
            Name = "StackJack Compact",
            Kind = McpServerKind.StackJackCompact,
            Transport = McpTransport.Http,
            EndpointUrl = McpServerDefaults.StackJackCompactEndpoint,
            AuthSecretName = "kv-stackjack-compact",
        };
        db.McpServers.Add(server);
        await db.SaveChangesAsync();

        var connection = new IntegrationConnection
        {
            TenantId = user.TenantId.Value,
            Provider = IntegrationProvider.Halo,
            DisplayName = "Halo",
            McpServerId = server.Id,
            SkipInactive = skipInactive,
            UpdateCompanyDetails = updateCompanyDetails,
        };
        db.IntegrationConnections.Add(connection);
        await db.SaveChangesAsync();
        return (server, connection);
    }

    private static async Task<(McpServer Server, IntegrationConnection Connection)> SeedNinjaCompactAsync(
        DocuEngAIneDbContext db, FakeCurrentUser user, bool skipInactive = true, bool updateCompanyDetails = false)
    {
        var server = new McpServer
        {
            TenantId = user.TenantId!.Value,
            Name = "StackJack Compact",
            Kind = McpServerKind.StackJackCompact,
            Transport = McpTransport.Http,
            EndpointUrl = McpServerDefaults.StackJackCompactEndpoint,
            AuthSecretName = "kv-stackjack-compact",
        };
        db.McpServers.Add(server);
        await db.SaveChangesAsync();

        var connection = new IntegrationConnection
        {
            TenantId = user.TenantId.Value,
            Provider = IntegrationProvider.NinjaOne,
            DisplayName = "NinjaOne",
            McpServerId = server.Id,
            SkipInactive = skipInactive,
            UpdateCompanyDetails = updateCompanyDetails,
        };
        db.IntegrationConnections.Add(connection);
        await db.SaveChangesAsync();
        return (server, connection);
    }

    private sealed class RecordingCippMcp : IMcpClient
    {
        public List<(Guid ServerId, string Tool, string? Args)> Calls { get; } = [];
        public string TenantsJson { get; init; } = "[]";

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((mcpServerId, toolName, argumentsJson));
            var body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "1",
                result = new { content = new[] { new { type = "text", text = TenantsJson } } },
            });
            return Task.FromResult(body);
        }
    }

    private static async Task<(McpServer Server, IntegrationConnection Connection)> SeedCippCompactAsync(
        DocuEngAIneDbContext db, FakeCurrentUser user, bool skipInactive = true, bool updateCompanyDetails = false)
    {
        var server = new McpServer
        {
            TenantId = user.TenantId!.Value,
            Name = "StackJack Compact",
            Kind = McpServerKind.StackJackCompact,
            Transport = McpTransport.Http,
            EndpointUrl = McpServerDefaults.StackJackCompactEndpoint,
            AuthSecretName = "kv-stackjack-compact",
        };
        db.McpServers.Add(server);
        await db.SaveChangesAsync();

        var connection = new IntegrationConnection
        {
            TenantId = user.TenantId.Value,
            Provider = IntegrationProvider.Cipp,
            DisplayName = "CIPP",
            McpServerId = server.Id,
            SkipInactive = skipInactive,
            UpdateCompanyDetails = updateCompanyDetails,
        };
        db.IntegrationConnections.Add(connection);
        await db.SaveChangesAsync();
        return (server, connection);
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
        a.McpServers.Add(new McpServer { TenantId = tenantA, Name = "StackJack Compact", Kind = McpServerKind.StackJackCompact, EndpointUrl = McpServerDefaults.StackJackCompactEndpoint });
        await a.SaveChangesAsync();

        await using var b = new DocuEngAIneDbContext(options, new FakeCurrentUser { TenantId = tenantB, ObjectId = "b", Role = UserRole.Owner });
        var forB = await b.McpServers.ForTenant(new FakeCurrentUser { TenantId = tenantB }).ToListAsync();
        Assert.Empty(forB);
    }

    private static async Task<(IntegrationConnection Connection, Company Company)> SeedMappedCompany(
        DocuEngAIneDbContext db, FakeCurrentUser user, bool updateCompanyDetails, Guid? mcpServerId = null)
    {
        var connection = new IntegrationConnection
        {
            TenantId = user.TenantId!.Value,
            Provider = IntegrationProvider.Halo,
            DisplayName = "Halo",
            AuthSecretName = "halo-secret",
            McpServerId = mcpServerId,
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

    [Fact]
    public async Task Halo_SyncAsync_Creates_Companies_Mappings_And_HaloClientId()
    {
        var mcp = new RecordingMcp
        {
            Clients =
            [
                new { id = 100, name = "ExampleCo", city = "Austin", state = "TX", inactive = false },
            ],
        };
        var (db, user, sync) = Create(mcp);
        var (server, connection) = await SeedHaloCompactAsync(db, user);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(1, run.ItemsCreated);
        Assert.Equal("halo_list_clients", Assert.Single(mcp.Calls).Tool);
        Assert.Equal(server.Id, mcp.Calls[0].ServerId);
        Assert.Contains("\"includeInactive\":false", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"activeInactive\":\"active\"", mcp.Calls[0].Args, StringComparison.Ordinal);

        var company = await db.Companies.SingleAsync();
        Assert.Equal("ExampleCo", company.Name);
        Assert.Equal("100", company.HaloClientId);
        var mapping = await db.IntegrationMappings.SingleAsync();
        Assert.Equal("100", mapping.ExternalId);
        Assert.Equal(company.Id, mapping.LocalEntityId);
    }

    [Fact]
    public async Task Halo_SyncAsync_SkipInactive_Requests_Active_And_Skips_Inactive_Rows()
    {
        var mcp = new RecordingMcp
        {
            Clients =
            [
                new { id = 1, name = "DeadCo", inactive = true },
                new { id = 2, name = "LiveCo", inactive = false },
            ],
        };
        var (db, user, sync) = Create(mcp);
        var (_, connection) = await SeedHaloCompactAsync(db, user, skipInactive: true);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(1, run.ItemsSkipped);
        Assert.Equal(1, run.ItemsCreated);
        Assert.Contains("\"includeInactive\":false", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"activeInactive\":\"active\"", mcp.Calls[0].Args, StringComparison.Ordinal);
        var company = await db.Companies.SingleAsync();
        Assert.Equal("LiveCo", company.Name);
        Assert.Equal("2", company.HaloClientId);
    }

    [Fact]
    public async Task Halo_SyncAsync_Does_Not_Clobber_Name_When_UpdateCompanyDetails_False()
    {
        var mcp = new RecordingMcp
        {
            Clients = [new { id = "halo-100", name = "Remote Name", inactive = false }],
        };
        var (db, user, sync) = Create(mcp);
        var server = new McpServer
        {
            TenantId = user.TenantId!.Value,
            Name = "StackJack Compact",
            Kind = McpServerKind.StackJackCompact,
            EndpointUrl = McpServerDefaults.StackJackCompactEndpoint,
            AuthSecretName = "kv-stackjack-compact",
        };
        db.McpServers.Add(server);
        await db.SaveChangesAsync();
        var (connection, _) = await SeedMappedCompany(db, user, updateCompanyDetails: false, mcpServerId: server.Id);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(1, run.ItemsUpdated);
        Assert.Equal("halo_list_clients", Assert.Single(mcp.Calls).Tool);
        var company = await db.Companies.SingleAsync();
        Assert.Equal("Local Name", company.Name);
        Assert.Equal("halo-100", company.HaloClientId);
    }

    [Fact]
    public async Task Halo_SyncAsync_Updates_Name_When_UpdateCompanyDetails_True()
    {
        var mcp = new RecordingMcp
        {
            Clients = [new { id = "halo-100", name = "Remote Name", inactive = false }],
        };
        var (db, user, sync) = Create(mcp);
        var server = new McpServer
        {
            TenantId = user.TenantId!.Value,
            Name = "StackJack Compact",
            Kind = McpServerKind.StackJackCompact,
            EndpointUrl = McpServerDefaults.StackJackCompactEndpoint,
            AuthSecretName = "kv-stackjack-compact",
        };
        db.McpServers.Add(server);
        await db.SaveChangesAsync();
        var (connection, _) = await SeedMappedCompany(db, user, updateCompanyDetails: true, mcpServerId: server.Id);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(1, run.ItemsUpdated);
        var company = await db.Companies.SingleAsync();
        Assert.Equal("Remote Name", company.Name);
        Assert.Equal("halo-100", company.HaloClientId);
    }

    [Fact]
    public async Task Halo_SyncAsync_Missing_McpServerId_Fails_Without_Calling_Mcp()
    {
        var mcp = new RecordingMcp { Clients = [new { id = 1, name = "ShouldNotImport" }] };
        var (db, user, sync) = Create(mcp);
        var connection = new IntegrationConnection
        {
            TenantId = user.TenantId!.Value,
            Provider = IntegrationProvider.Halo,
            DisplayName = "Halo",
            AuthSecretName = "kv-name-only",
        };
        db.IntegrationConnections.Add(connection);
        await db.SaveChangesAsync();

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Failed, run.Status);
        Assert.Contains("McpServerId", run.ErrorSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Key Vault", run.ErrorSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(mcp.Calls);
        Assert.Empty(await db.Companies.ToListAsync());
    }

    [Fact]
    public async Task Other_Tenant_Connection_Sync_Returns_404_And_Does_Not_Call_Mcp()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var mcp = new RecordingMcp { Clients = [new { id = 9, name = "PoisonCo" }] };

        Guid connectionBId;
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
            var connection = new IntegrationConnection
            {
                TenantId = tenantB,
                Provider = IntegrationProvider.Halo,
                DisplayName = "Halo B",
                McpServerId = server.Id,
            };
            dbB.IntegrationConnections.Add(connection);
            await dbB.SaveChangesAsync();
            connectionBId = connection.Id;
        }

        var (dbA, userA) = Open(dbName, tenantA);
        await using (dbA)
        {
            var sync = new IntegrationSyncService(dbA, userA, mcp, new NoopAudit());
            var result = await IntegrationEndpoints.SyncAsync(connectionBId, null, sync, dbA, userA);
            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
            Assert.Empty(mcp.Calls);
            Assert.Empty(await dbA.IntegrationConnections.ForTenant(userA).ToListAsync());
            Assert.Empty(await dbA.SyncRuns.ForTenant(userA).ToListAsync());
            Assert.Empty(await dbA.Companies.ForTenant(userA).ToListAsync());
        }
    }

    [Fact]
    public async Task Create_Compact_And_Composio_McpServers_Use_Defaults_ForTenant_Without_Secret_Values()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        Guid compactId;
        Guid composioId;
        var (dbA, userA) = Open(dbName, tenantA);
        await using (dbA)
        {
            var compact = Assert.IsAssignableFrom<IValueHttpResult>(
                await IntegrationEndpoints.CreateMcpServerAsync(
                    new CreateMcpServerRequest("StackJack Compact", McpServerKind.StackJackCompact, AuthSecretName: "kv-stackjack-compact"),
                    dbA, userA));
            var composio = Assert.IsAssignableFrom<IValueHttpResult>(
                await IntegrationEndpoints.CreateMcpServerAsync(
                    new CreateMcpServerRequest("Composio Connect", McpServerKind.Composio, AuthSecretName: "kv-composio"),
                    dbA, userA));

            var compactJson = JsonSerializer.Serialize(compact.Value);
            using var compactDoc = JsonDocument.Parse(compactJson);
            Assert.Equal("StackJackCompact", compactDoc.RootElement.GetProperty("Kind").GetString());
            Assert.Equal(McpServerDefaults.StackJackCompactEndpoint, compactDoc.RootElement.GetProperty("EndpointUrl").GetString());
            Assert.Equal("kv-stackjack-compact", compactDoc.RootElement.GetProperty("AuthSecretName").GetString());
            Assert.Equal("Http", compactDoc.RootElement.GetProperty("Transport").GetString());
            compactId = compactDoc.RootElement.GetProperty("Id").GetGuid();

            var composioJson = JsonSerializer.Serialize(composio.Value);
            using var composioDoc = JsonDocument.Parse(composioJson);
            Assert.Equal("Composio", composioDoc.RootElement.GetProperty("Kind").GetString());
            Assert.Equal(McpServerDefaults.ComposioEndpoint, composioDoc.RootElement.GetProperty("EndpointUrl").GetString());
            Assert.Equal("kv-composio", composioDoc.RootElement.GetProperty("AuthSecretName").GetString());
            composioId = composioDoc.RootElement.GetProperty("Id").GetGuid();

            var stored = await dbA.McpServers.ForTenant(userA).ToListAsync();
            Assert.Equal(2, stored.Count);
            Assert.All(stored, s => Assert.False(string.IsNullOrWhiteSpace(s.AuthSecretName)));
            Assert.DoesNotContain(stored, s => s.AuthSecretName!.Contains("secret-value", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(stored, s => (s.Notes ?? "").Contains("sk-", StringComparison.Ordinal));
        }

        var (dbB, userB) = Open(dbName, tenantB);
        await using (dbB)
        {
            var leaked = await dbB.McpServers.ForTenant(userB).ToListAsync();
            Assert.Empty(leaked);
            Assert.Null(await dbB.McpServers.ForTenant(userB).FirstOrDefaultAsync(s => s.Id == compactId));
            Assert.Null(await dbB.McpServers.ForTenant(userB).FirstOrDefaultAsync(s => s.Id == composioId));
        }
    }

    [Fact]
    public async Task Ninja_SyncAsync_Creates_Companies_Mappings_And_NinjaOrganizationId()
    {
        var mcp = new RecordingNinjaMcp { OrganizationsJson = NinjaOrganizationMapperTests.LiveCompactListFixture };
        var (db, user, sync) = Create(mcp);
        var (server, connection) = await SeedNinjaCompactAsync(db, user);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(5, run.ItemsCreated);
        Assert.Equal(0, run.ItemsSkipped);
        // A Ninja sync now pulls organizations and then devices, so this is no longer a single call.
        Assert.Equal("ninja_list_organizations", mcp.Calls[0].Tool);
        Assert.Contains(mcp.Calls, c => c.Tool == NinjaDeviceMapper.ToolName);
        Assert.Equal(server.Id, mcp.Calls[0].ServerId);
        Assert.DoesNotContain("after", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":50", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("ninja_get_organization", mcp.Calls.Select(c => c.Tool));

        var companies = await db.Companies.ToListAsync();
        Assert.Equal(5, companies.Count);
        var masri = Assert.Single(companies, c => c.NinjaOrganizationId == "2");
        Assert.Equal("Masri Digital", masri.Name);
        Assert.Null(masri.HaloClientId);

        var mappings = await db.IntegrationMappings.ToListAsync();
        Assert.Equal(5, mappings.Count);
        var masriMapping = Assert.Single(mappings, m => m.ExternalId == "2");
        Assert.Equal("company", masriMapping.ExternalType);
        Assert.Equal(masri.Id, masriMapping.LocalEntityId);
        Assert.Contains(mappings, m => m.ExternalId == "23");
    }

    [Fact]
    public async Task Ninja_List_Organizations_Cursor_Second_CallToolAsync_Receives_After_23()
    {
        var mcp = new RecordingNinjaMcp { OrganizationsJson = NinjaOrganizationMapperTests.LiveCompactListFixture };
        var (db, user, _) = Create(mcp);
        var (server, _) = await SeedNinjaCompactAsync(db, user);

        var companies = await NinjaOrganizationMapper.PullAsync(mcp, server.Id, pageSize: 5);

        Assert.Equal(5, companies.Count);
        Assert.Equal("2", companies[0].ExternalId);
        Assert.Equal("Masri Digital", companies[0].Name);
        Assert.Equal(2, mcp.Calls.Count);
        Assert.All(mcp.Calls, c => Assert.Equal("ninja_list_organizations", c.Tool));
        Assert.Equal(server.Id, mcp.Calls[0].ServerId);
        Assert.DoesNotContain("after", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":5", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"after\":23", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":5", mcp.Calls[1].Args, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ninja_SyncAsync_Does_Not_Clobber_Name_When_UpdateCompanyDetails_False()
    {
        var mcp = new RecordingNinjaMcp { OrganizationsJson = NinjaOrganizationMapperTests.LiveCompactListFixture };
        var (db, user, sync) = Create(mcp);
        var server = new McpServer
        {
            TenantId = user.TenantId!.Value,
            Name = "StackJack Compact",
            Kind = McpServerKind.StackJackCompact,
            EndpointUrl = McpServerDefaults.StackJackCompactEndpoint,
            AuthSecretName = "kv-stackjack-compact",
        };
        db.McpServers.Add(server);
        await db.SaveChangesAsync();
        var connection = new IntegrationConnection
        {
            TenantId = user.TenantId.Value,
            Provider = IntegrationProvider.NinjaOne,
            DisplayName = "NinjaOne",
            McpServerId = server.Id,
            UpdateCompanyDetails = false,
        };
        db.IntegrationConnections.Add(connection);
        var company = new Company
        {
            TenantId = user.TenantId.Value,
            Name = "Local Name",
            Slug = "local-name",
        };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.IntegrationMappings.Add(new IntegrationMapping
        {
            TenantId = user.TenantId.Value,
            IntegrationConnectionId = connection.Id,
            ExternalId = "2",
            ExternalType = "company",
            LocalEntityType = nameof(Company),
            LocalEntityId = company.Id,
        });
        await db.SaveChangesAsync();

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(1, run.ItemsUpdated);
        Assert.Equal(4, run.ItemsCreated);
        var masri = await db.Companies.SingleAsync(c => c.NinjaOrganizationId == "2");
        Assert.Equal("Local Name", masri.Name);
        Assert.Equal("2", masri.NinjaOrganizationId);
    }

    [Fact]
    public async Task Ninja_SyncAsync_Missing_McpServerId_Fails_Without_Calling_Mcp()
    {
        var mcp = new RecordingNinjaMcp { OrganizationsJson = NinjaOrganizationMapperTests.LiveCompactListFixture };
        var (db, user, sync) = Create(mcp);
        var connection = new IntegrationConnection
        {
            TenantId = user.TenantId!.Value,
            Provider = IntegrationProvider.NinjaOne,
            DisplayName = "NinjaOne",
            AuthSecretName = "kv-name-only",
        };
        db.IntegrationConnections.Add(connection);
        await db.SaveChangesAsync();

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Failed, run.Status);
        Assert.Contains("McpServerId", run.ErrorSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Key Vault", run.ErrorSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(mcp.Calls);
        Assert.Empty(await db.Companies.ToListAsync());
    }

    [Fact]
    public async Task Ninja_Other_Tenant_Connection_Sync_Returns_404_And_Does_Not_Call_Mcp()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var mcp = new RecordingNinjaMcp { OrganizationsJson = NinjaOrganizationMapperTests.LiveCompactListFixture };

        Guid connectionBId;
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
            var connection = new IntegrationConnection
            {
                TenantId = tenantB,
                Provider = IntegrationProvider.NinjaOne,
                DisplayName = "NinjaOne B",
                McpServerId = server.Id,
            };
            dbB.IntegrationConnections.Add(connection);
            await dbB.SaveChangesAsync();
            connectionBId = connection.Id;
        }

        var (dbA, userA) = Open(dbName, tenantA);
        await using (dbA)
        {
            var sync = new IntegrationSyncService(dbA, userA, mcp, new NoopAudit());
            var result = await IntegrationEndpoints.SyncAsync(connectionBId, null, sync, dbA, userA);
            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
            Assert.Empty(mcp.Calls);
            Assert.Empty(await dbA.IntegrationConnections.ForTenant(userA).ToListAsync());
            Assert.Empty(await dbA.SyncRuns.ForTenant(userA).ToListAsync());
            Assert.Empty(await dbA.Companies.ForTenant(userA).ToListAsync());
        }
    }

    [Fact]
    public async Task Cipp_SyncAsync_Creates_Company_And_Mapping_From_CustomerId_Skips_Partner()
    {
        var mcp = new RecordingCippMcp { TenantsJson = CippTenantMapperTests.LiveCompactListFixture };
        var (db, user, sync) = Create(mcp);
        var (server, connection) = await SeedCippCompactAsync(db, user, skipInactive: true);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(1, run.ItemsCreated);
        Assert.Equal(1, run.ItemsSkipped);
        Assert.Equal("cipp_list_tenants", Assert.Single(mcp.Calls).Tool);
        Assert.Equal(server.Id, mcp.Calls[0].ServerId);
        Assert.Contains("\"tenantsOnly\":\"true\"", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("pageSize", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("pageNo", mcp.Calls[0].Args, StringComparison.Ordinal);

        var company = await db.Companies.SingleAsync();
        Assert.Equal("ADROC Capital, LLC", company.Name);
        Assert.Equal("adroccap.com", company.PrimaryDomain);
        Assert.Null(company.HaloClientId);
        Assert.Null(company.NinjaOrganizationId);

        var mapping = await db.IntegrationMappings.SingleAsync();
        Assert.Equal("company", mapping.ExternalType);
        Assert.Equal("8c65106e-9e7e-45d4-b55a-3cbd4b415a08", mapping.ExternalId);
        Assert.Equal(company.Id, mapping.LocalEntityId);

        Assert.DoesNotContain(await db.Companies.ToListAsync(), c => c.Name == "*Partner Tenant");
        Assert.DoesNotContain(await db.Companies.ToListAsync(), c => c.Name == "Gone Co");
        Assert.DoesNotContain(await db.IntegrationMappings.ToListAsync(), m => m.ExternalId == "f7812296-5bce-41dc-8102-b1b270e7c4c7");
        Assert.DoesNotContain(await db.IntegrationMappings.ToListAsync(), m => m.ExternalId == "deadbeef-0000-0000-0000-000000000001");
    }

    [Fact]
    public async Task Cipp_SyncAsync_SkipInactive_Skips_Excluded_True()
    {
        var mcp = new RecordingCippMcp { TenantsJson = CippTenantMapperTests.LiveCompactListFixture };
        var (db, user, sync) = Create(mcp);
        var (_, connection) = await SeedCippCompactAsync(db, user, skipInactive: true);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(1, run.ItemsSkipped);
        Assert.Equal(1, run.ItemsCreated);
        var company = await db.Companies.SingleAsync();
        Assert.Equal("ADROC Capital, LLC", company.Name);
        Assert.Equal("8c65106e-9e7e-45d4-b55a-3cbd4b415a08", (await db.IntegrationMappings.SingleAsync()).ExternalId);
    }

    [Fact]
    public async Task Cipp_SyncAsync_Missing_McpServerId_Fails_Without_Calling_Mcp()
    {
        var mcp = new RecordingCippMcp { TenantsJson = CippTenantMapperTests.LiveCompactListFixture };
        var (db, user, sync) = Create(mcp);
        var connection = new IntegrationConnection
        {
            TenantId = user.TenantId!.Value,
            Provider = IntegrationProvider.Cipp,
            DisplayName = "CIPP",
            AuthSecretName = "kv-name-only",
        };
        db.IntegrationConnections.Add(connection);
        await db.SaveChangesAsync();

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Failed, run.Status);
        Assert.Contains("McpServerId", run.ErrorSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Key Vault", run.ErrorSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(mcp.Calls);
        Assert.Empty(await db.Companies.ToListAsync());
    }

    [Fact]
    public async Task Cipp_Other_Tenant_Connection_Sync_Returns_404_And_Does_Not_Call_Mcp()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var mcp = new RecordingCippMcp { TenantsJson = CippTenantMapperTests.LiveCompactListFixture };

        Guid connectionBId;
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
            var connection = new IntegrationConnection
            {
                TenantId = tenantB,
                Provider = IntegrationProvider.Cipp,
                DisplayName = "CIPP B",
                McpServerId = server.Id,
            };
            dbB.IntegrationConnections.Add(connection);
            await dbB.SaveChangesAsync();
            connectionBId = connection.Id;
        }

        var (dbA, userA) = Open(dbName, tenantA);
        await using (dbA)
        {
            var sync = new IntegrationSyncService(dbA, userA, mcp, new NoopAudit());
            var result = await IntegrationEndpoints.SyncAsync(connectionBId, null, sync, dbA, userA);
            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
            Assert.Empty(mcp.Calls);
            Assert.Empty(await dbA.IntegrationConnections.ForTenant(userA).ToListAsync());
            Assert.Empty(await dbA.SyncRuns.ForTenant(userA).ToListAsync());
            Assert.Empty(await dbA.Companies.ForTenant(userA).ToListAsync());
        }
    }

    private sealed class RecordingMerakiMcp : IMcpClient
    {
        public List<(Guid ServerId, string Tool, string? Args)> Calls { get; } = [];
        public string OrganizationsJson { get; init; } = "[]";

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((mcpServerId, toolName, argumentsJson));
            string? startingAfter = null;
            var perPage = MerakiOrganizationMapper.DefaultPageSize;
            if (!string.IsNullOrWhiteSpace(argumentsJson))
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.TryGetProperty("startingAfter", out var a) && a.ValueKind == JsonValueKind.String)
                    startingAfter = a.GetString();
                if (doc.RootElement.TryGetProperty("perPage", out var s) && s.ValueKind == JsonValueKind.Number)
                    perPage = s.GetInt32();
            }

            var inner = SliceOrganizationsJson(OrganizationsJson, startingAfter, perPage);
            var body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "1",
                result = new { content = new[] { new { type = "text", text = inner } } },
            });
            return Task.FromResult(body);
        }

        private static string SliceOrganizationsJson(string json, string? startingAfter, int perPage)
        {
            using var doc = JsonDocument.Parse(json);
            var items = new List<string>();
            var skip = startingAfter is not null;
            foreach (var org in doc.RootElement.EnumerateArray())
            {
                var id = org.GetProperty("id").GetString();
                if (skip)
                {
                    if (id == startingAfter)
                        skip = false;
                    continue;
                }
                items.Add(org.GetRawText());
                if (items.Count >= perPage)
                    break;
            }
            return "[" + string.Join(",", items) + "]";
        }
    }

    private static async Task<(McpServer Server, IntegrationConnection Connection)> SeedMerakiCompactAsync(
        DocuEngAIneDbContext db, FakeCurrentUser user, bool skipInactive = true, bool updateCompanyDetails = false)
    {
        var server = new McpServer
        {
            TenantId = user.TenantId!.Value,
            Name = "StackJack Compact",
            Kind = McpServerKind.StackJackCompact,
            Transport = McpTransport.Http,
            EndpointUrl = McpServerDefaults.StackJackCompactEndpoint,
            AuthSecretName = "kv-stackjack-compact",
        };
        db.McpServers.Add(server);
        await db.SaveChangesAsync();

        var connection = new IntegrationConnection
        {
            TenantId = user.TenantId.Value,
            Provider = IntegrationProvider.Meraki,
            DisplayName = "Meraki",
            McpServerId = server.Id,
            SkipInactive = skipInactive,
            UpdateCompanyDetails = updateCompanyDetails,
        };
        db.IntegrationConnections.Add(connection);
        await db.SaveChangesAsync();
        return (server, connection);
    }

    [Fact]
    public async Task Meraki_SyncAsync_Creates_Companies_And_Mappings_From_Org_Id()
    {
        var mcp = new RecordingMerakiMcp { OrganizationsJson = MerakiOrganizationMapperTests.LiveCompactListFixture };
        var (db, user, sync) = Create(mcp);
        var (server, connection) = await SeedMerakiCompactAsync(db, user);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(2, run.ItemsCreated);
        Assert.Equal(0, run.ItemsSkipped);
        Assert.Equal("meraki_get_organizations", Assert.Single(mcp.Calls).Tool);
        Assert.Equal(server.Id, mcp.Calls[0].ServerId);
        Assert.DoesNotContain("startingAfter", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"perPage\":50", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("meraki_get_organization_networks", mcp.Calls.Select(c => c.Tool));

        var companies = await db.Companies.ToListAsync();
        Assert.Equal(2, companies.Count);
        var compression = Assert.Single(companies, c => c.Name == "7 Compression");
        Assert.Equal("https://n565.dashboard.meraki.com/o/T-0Fub/manage/organization/overview", compression.Website);
        Assert.Null(compression.HaloClientId);
        Assert.Null(compression.NinjaOrganizationId);

        var mappings = await db.IntegrationMappings.ToListAsync();
        Assert.Equal(2, mappings.Count);
        var compressionMapping = Assert.Single(mappings, m => m.ExternalId == "1279651");
        Assert.Equal("company", compressionMapping.ExternalType);
        Assert.Equal(compression.Id, compressionMapping.LocalEntityId);
        Assert.Contains(mappings, m => m.ExternalId == "1721429");
    }

    [Fact]
    public async Task Meraki_Get_Organizations_Cursor_Second_CallToolAsync_Receives_StartingAfter_1721429()
    {
        var mcp = new RecordingMerakiMcp { OrganizationsJson = MerakiOrganizationMapperTests.LiveCompactListFixture };
        var (db, user, _) = Create(mcp);
        var (server, _) = await SeedMerakiCompactAsync(db, user);

        var companies = await MerakiOrganizationMapper.PullAsync(mcp, server.Id, pageSize: 2);

        Assert.Equal(2, companies.Count);
        Assert.Equal("1279651", companies[0].ExternalId);
        Assert.Equal("7 Compression", companies[0].Name);
        Assert.Equal("https://n565.dashboard.meraki.com/o/T-0Fub/manage/organization/overview", companies[0].Website);
        Assert.Equal(2, mcp.Calls.Count);
        Assert.All(mcp.Calls, c => Assert.Equal("meraki_get_organizations", c.Tool));
        Assert.Equal(server.Id, mcp.Calls[0].ServerId);
        Assert.DoesNotContain("startingAfter", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"perPage\":2", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"startingAfter\":\"1721429\"", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.Contains("\"perPage\":2", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("meraki_get_organization_networks", mcp.Calls.Select(c => c.Tool));
    }

    [Fact]
    public async Task Meraki_SyncAsync_Missing_McpServerId_Fails_Without_Calling_Mcp()
    {
        var mcp = new RecordingMerakiMcp { OrganizationsJson = MerakiOrganizationMapperTests.LiveCompactListFixture };
        var (db, user, sync) = Create(mcp);
        var connection = new IntegrationConnection
        {
            TenantId = user.TenantId!.Value,
            Provider = IntegrationProvider.Meraki,
            DisplayName = "Meraki",
            AuthSecretName = "kv-name-only",
        };
        db.IntegrationConnections.Add(connection);
        await db.SaveChangesAsync();

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Failed, run.Status);
        Assert.Contains("McpServerId", run.ErrorSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Key Vault", run.ErrorSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(mcp.Calls);
        Assert.Empty(await db.Companies.ToListAsync());
    }

    [Fact]
    public async Task Meraki_Other_Tenant_Connection_Sync_Returns_404_And_Does_Not_Call_Mcp()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var mcp = new RecordingMerakiMcp { OrganizationsJson = MerakiOrganizationMapperTests.LiveCompactListFixture };

        Guid connectionBId;
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
            var connection = new IntegrationConnection
            {
                TenantId = tenantB,
                Provider = IntegrationProvider.Meraki,
                DisplayName = "Meraki B",
                McpServerId = server.Id,
            };
            dbB.IntegrationConnections.Add(connection);
            await dbB.SaveChangesAsync();
            connectionBId = connection.Id;
        }

        var (dbA, userA) = Open(dbName, tenantA);
        await using (dbA)
        {
            var sync = new IntegrationSyncService(dbA, userA, mcp, new NoopAudit());
            var result = await IntegrationEndpoints.SyncAsync(connectionBId, null, sync, dbA, userA);
            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
            Assert.Empty(mcp.Calls);
            Assert.Empty(await dbA.IntegrationConnections.ForTenant(userA).ToListAsync());
            Assert.Empty(await dbA.SyncRuns.ForTenant(userA).ToListAsync());
            Assert.Empty(await dbA.Companies.ForTenant(userA).ToListAsync());
        }
    }

    private sealed class RecordingUnifiMcp : IMcpClient
    {
        public List<(Guid ServerId, string Tool, string? Args)> Calls { get; } = [];
        public string HostsJson { get; init; } = """{"data":[]}""";

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((mcpServerId, toolName, argumentsJson));
            string? nextToken = null;
            var pageSize = UnifiHostMapper.DefaultPageSize;
            if (!string.IsNullOrWhiteSpace(argumentsJson))
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.TryGetProperty("nextToken", out var t) && t.ValueKind == JsonValueKind.String)
                    nextToken = t.GetString();
                if (doc.RootElement.TryGetProperty("pageSize", out var s) && s.ValueKind == JsonValueKind.Number)
                    pageSize = s.GetInt32();
            }

            var inner = SliceHostsJson(HostsJson, nextToken, pageSize);
            var body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "1",
                result = new { content = new[] { new { type = "text", text = inner } } },
            });
            return Task.FromResult(body);
        }

        private static string SliceHostsJson(string json, string? nextToken, int pageSize)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return """{"data":[]}""";

            var all = data.EnumerateArray().ToList();
            var start = 0;
            if (nextToken is not null)
            {
                start = all.FindIndex(h =>
                    h.ValueKind == JsonValueKind.Object
                    && h.TryGetProperty("id", out var id)
                    && id.GetString() == nextToken);
                if (start < 0)
                    start = all.Count;
            }

            var page = all.Skip(start).Take(pageSize).ToList();
            var dataJson = "[" + string.Join(",", page.Select(p => p.GetRawText())) + "]";
            if (start + page.Count < all.Count)
            {
                var outgoing = all[start + page.Count].GetProperty("id").GetString();
                return $$"""{"data":{{dataJson}},"nextToken":"{{outgoing}}","httpStatusCode":200}""";
            }

            return $$"""{"data":{{dataJson}},"httpStatusCode":200}""";
        }
    }

    private static async Task<(McpServer Server, IntegrationConnection Connection)> SeedUnifiCompactAsync(
        DocuEngAIneDbContext db, FakeCurrentUser user, bool skipInactive = true, bool updateCompanyDetails = false)
    {
        var server = new McpServer
        {
            TenantId = user.TenantId!.Value,
            Name = "StackJack Compact",
            Kind = McpServerKind.StackJackCompact,
            Transport = McpTransport.Http,
            EndpointUrl = McpServerDefaults.StackJackCompactEndpoint,
            AuthSecretName = "kv-stackjack-compact",
        };
        db.McpServers.Add(server);
        await db.SaveChangesAsync();

        var connection = new IntegrationConnection
        {
            TenantId = user.TenantId.Value,
            Provider = IntegrationProvider.UniFi,
            DisplayName = "UniFi",
            McpServerId = server.Id,
            SkipInactive = skipInactive,
            UpdateCompanyDetails = updateCompanyDetails,
        };
        db.IntegrationConnections.Add(connection);
        await db.SaveChangesAsync();
        return (server, connection);
    }

    [Fact]
    public async Task UniFi_SyncAsync_Creates_Company_From_Host_Name_And_City()
    {
        var mcp = new RecordingUnifiMcp { HostsJson = UnifiHostMapperTests.LiveCompactListFixture };
        var (db, user, sync) = Create(mcp);
        var (server, connection) = await SeedUnifiCompactAsync(db, user);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(1, run.ItemsCreated);
        Assert.Equal(1, run.ItemsSkipped);
        Assert.Equal("unifi_sm_list_hosts", Assert.Single(mcp.Calls).Tool);
        Assert.Equal(server.Id, mcp.Calls[0].ServerId);
        Assert.DoesNotContain("nextToken", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":50", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("unifi_sm_list_sites", mcp.Calls.Select(c => c.Tool));

        var company = await db.Companies.SingleAsync();
        Assert.Equal("Adroc Capital: 1425 RXR Plaza", company.Name);
        Assert.Equal("Wyandanch, NY, United States", company.City);
        Assert.Null(company.HaloClientId);
        Assert.Null(company.NinjaOrganizationId);
        Assert.Equal("host-1", CompanyIdentity.ReadExternalIds(company.ExternalIdsJson)["unifi"]);

        var mapping = await db.IntegrationMappings.SingleAsync();
        Assert.Equal("company", mapping.ExternalType);
        Assert.Equal("host-1", mapping.ExternalId);
        Assert.Equal(company.Id, mapping.LocalEntityId);
    }

    [Fact]
    public async Task UniFi_SyncAsync_Adopts_Existing_Company_By_Name_Instead_Of_Duplicating()
    {
        var mcp = new RecordingUnifiMcp { HostsJson = UnifiHostMapperTests.LiveCompactListFixture };
        var (db, user, sync) = Create(mcp);
        var (_, connection) = await SeedUnifiCompactAsync(db, user);

        db.Companies.Add(new Company
        {
            TenantId = user.TenantId!.Value,
            Name = "Adroc Capital: 1425 RXR Plaza",
            Slug = "adroc-capital-1425-rxr-plaza",
            HaloClientId = "halo-100",
            ExternalIdsJson = CompanyIdentity.UpsertExternalId(null, "halo", "halo-100"),
        });
        await db.SaveChangesAsync();

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(0, run.ItemsCreated);
        Assert.Equal(1, run.ItemsUpdated);
        Assert.Equal(1, run.ItemsSkipped);

        var company = await db.Companies.SingleAsync();
        Assert.Equal("halo-100", company.HaloClientId);
        Assert.Equal("host-1", CompanyIdentity.ReadExternalIds(company.ExternalIdsJson)["unifi"]);
        Assert.Equal("halo-100", CompanyIdentity.ReadExternalIds(company.ExternalIdsJson)["halo"]);

        var mapping = await db.IntegrationMappings.SingleAsync();
        Assert.Equal("host-1", mapping.ExternalId);
        Assert.Equal(company.Id, mapping.LocalEntityId);
        Assert.Contains(CompanyMatchIndex.MatchedByName, mapping.MetadataJson!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UniFi_SkipInactive_Drops_IsBlocked_True()
    {
        var mcp = new RecordingUnifiMcp { HostsJson = UnifiHostMapperTests.LiveCompactListFixture };
        var (db, user, sync) = Create(mcp);
        var (_, connection) = await SeedUnifiCompactAsync(db, user, skipInactive: true);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(1, run.ItemsSkipped);
        Assert.Equal(1, run.ItemsCreated);
        var company = await db.Companies.SingleAsync();
        Assert.Equal("Adroc Capital: 1425 RXR Plaza", company.Name);
        Assert.DoesNotContain(await db.Companies.ToListAsync(), c => c.Name == "Blocked Co");
        Assert.DoesNotContain(await db.IntegrationMappings.ToListAsync(), m => m.ExternalId == "host-2");
    }

    [Fact]
    public async Task UniFi_List_Hosts_Cursor_Second_CallToolAsync_Receives_NextToken()
    {
        var mcp = new RecordingUnifiMcp { HostsJson = UnifiHostMapperTests.LiveCompactListFixture };
        var (db, user, _) = Create(mcp);
        var (server, _) = await SeedUnifiCompactAsync(db, user);

        var companies = await UnifiHostMapper.PullAsync(mcp, server.Id, pageSize: 1);

        Assert.Equal(2, companies.Count);
        Assert.Equal("host-1", companies[0].ExternalId);
        Assert.Equal("Adroc Capital: 1425 RXR Plaza", companies[0].Name);
        Assert.Equal("Wyandanch, NY, United States", companies[0].City);
        Assert.Equal(2, mcp.Calls.Count);
        Assert.All(mcp.Calls, c => Assert.Equal("unifi_sm_list_hosts", c.Tool));
        Assert.Equal(server.Id, mcp.Calls[0].ServerId);
        Assert.DoesNotContain("nextToken", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":1", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"nextToken\":\"host-2\"", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":1", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("unifi_sm_list_sites", mcp.Calls.Select(c => c.Tool));
    }

    [Fact]
    public async Task UniFi_SyncAsync_Missing_McpServerId_Fails_Without_Calling_Mcp()
    {
        var mcp = new RecordingUnifiMcp { HostsJson = UnifiHostMapperTests.LiveCompactListFixture };
        var (db, user, sync) = Create(mcp);
        var connection = new IntegrationConnection
        {
            TenantId = user.TenantId!.Value,
            Provider = IntegrationProvider.UniFi,
            DisplayName = "UniFi",
            AuthSecretName = "kv-name-only",
        };
        db.IntegrationConnections.Add(connection);
        await db.SaveChangesAsync();

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Failed, run.Status);
        Assert.Contains("McpServerId", run.ErrorSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Key Vault", run.ErrorSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(mcp.Calls);
        Assert.Empty(await db.Companies.ToListAsync());
    }

    [Fact]
    public async Task UniFi_Other_Tenant_Connection_Sync_Returns_404_And_Does_Not_Call_Mcp()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var mcp = new RecordingUnifiMcp { HostsJson = UnifiHostMapperTests.LiveCompactListFixture };

        Guid connectionBId;
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
            var connection = new IntegrationConnection
            {
                TenantId = tenantB,
                Provider = IntegrationProvider.UniFi,
                DisplayName = "UniFi B",
                McpServerId = server.Id,
            };
            dbB.IntegrationConnections.Add(connection);
            await dbB.SaveChangesAsync();
            connectionBId = connection.Id;
        }

        var (dbA, userA) = Open(dbName, tenantA);
        await using (dbA)
        {
            var sync = new IntegrationSyncService(dbA, userA, mcp, new NoopAudit());
            var result = await IntegrationEndpoints.SyncAsync(connectionBId, null, sync, dbA, userA);
            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
            Assert.Empty(mcp.Calls);
            Assert.Empty(await dbA.IntegrationConnections.ForTenant(userA).ToListAsync());
            Assert.Empty(await dbA.SyncRuns.ForTenant(userA).ToListAsync());
            Assert.Empty(await dbA.Companies.ForTenant(userA).ToListAsync());
        }
    }

    private sealed class RecordingAction1Mcp : IMcpClient
    {
        public List<(Guid ServerId, string Tool, string? Args)> Calls { get; } = [];
        public string OrganizationsJson { get; init; } = """{"id":"1","type":"ResultPage","items":[],"next_page":""}""";

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((mcpServerId, toolName, argumentsJson));
            var body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "1",
                result = new { content = new[] { new { type = "text", text = OrganizationsJson } } },
            });
            return Task.FromResult(body);
        }
    }

    private static async Task<(McpServer Server, IntegrationConnection Connection)> SeedAction1CompactAsync(
        DocuEngAIneDbContext db, FakeCurrentUser user, bool skipInactive = true, bool updateCompanyDetails = false)
    {
        var server = new McpServer
        {
            TenantId = user.TenantId!.Value,
            Name = "StackJack Compact",
            Kind = McpServerKind.StackJackCompact,
            Transport = McpTransport.Http,
            EndpointUrl = McpServerDefaults.StackJackCompactEndpoint,
            AuthSecretName = "kv-stackjack-compact",
        };
        db.McpServers.Add(server);
        await db.SaveChangesAsync();

        var connection = new IntegrationConnection
        {
            TenantId = user.TenantId.Value,
            Provider = IntegrationProvider.Action1,
            DisplayName = "Action1",
            McpServerId = server.Id,
            SkipInactive = skipInactive,
            UpdateCompanyDetails = updateCompanyDetails,
        };
        db.IntegrationConnections.Add(connection);
        await db.SaveChangesAsync();
        return (server, connection);
    }

    [Fact]
    public async Task Action1_SyncAsync_Creates_Company_And_Mapping_From_Org_Id_Skips_Default()
    {
        var mcp = new RecordingAction1Mcp { OrganizationsJson = Action1OrganizationMapperTests.LiveCompactListFixture };
        var (db, user, sync) = Create(mcp);
        var (server, connection) = await SeedAction1CompactAsync(db, user);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(1, run.ItemsCreated);
        Assert.Equal(0, run.ItemsSkipped);
        Assert.Equal("action1_list_organizations", Assert.Single(mcp.Calls).Tool);
        Assert.Equal(server.Id, mcp.Calls[0].ServerId);
        Assert.Contains("\"admin\":true", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":50", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("from", mcp.Calls[0].Args, StringComparison.Ordinal);

        var company = await db.Companies.SingleAsync();
        Assert.Equal("Adroc Capital", company.Name);
        Assert.Null(company.HaloClientId);
        Assert.Null(company.NinjaOrganizationId);
        Assert.Equal("4702a030-5f67-11f0-9cb3-e3f0bda36034",
            CompanyIdentity.ReadExternalIds(company.ExternalIdsJson)["action1"]);

        var mapping = await db.IntegrationMappings.SingleAsync();
        Assert.Equal("company", mapping.ExternalType);
        Assert.Equal("4702a030-5f67-11f0-9cb3-e3f0bda36034", mapping.ExternalId);
        Assert.Equal(company.Id, mapping.LocalEntityId);

        Assert.DoesNotContain(await db.Companies.ToListAsync(), c => c.Name == "Masri Digital");
        Assert.DoesNotContain(await db.IntegrationMappings.ToListAsync(),
            m => m.ExternalId == "4fa9a577-6ec2-46a3-b3c4-144099fc4ab4");
    }

    [Fact]
    public async Task Action1_SyncAsync_Missing_McpServerId_Fails_Without_Calling_Mcp()
    {
        var mcp = new RecordingAction1Mcp { OrganizationsJson = Action1OrganizationMapperTests.LiveCompactListFixture };
        var (db, user, sync) = Create(mcp);
        var connection = new IntegrationConnection
        {
            TenantId = user.TenantId!.Value,
            Provider = IntegrationProvider.Action1,
            DisplayName = "Action1",
            AuthSecretName = "kv-name-only",
        };
        db.IntegrationConnections.Add(connection);
        await db.SaveChangesAsync();

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Failed, run.Status);
        Assert.Contains("McpServerId", run.ErrorSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Key Vault", run.ErrorSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(mcp.Calls);
        Assert.Empty(await db.Companies.ToListAsync());
    }

    [Fact]
    public async Task Action1_Other_Tenant_Connection_Sync_Returns_404_And_Does_Not_Call_Mcp()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var mcp = new RecordingAction1Mcp { OrganizationsJson = Action1OrganizationMapperTests.LiveCompactListFixture };

        Guid connectionBId;
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
            var connection = new IntegrationConnection
            {
                TenantId = tenantB,
                Provider = IntegrationProvider.Action1,
                DisplayName = "Action1 B",
                McpServerId = server.Id,
            };
            dbB.IntegrationConnections.Add(connection);
            await dbB.SaveChangesAsync();
            connectionBId = connection.Id;
        }

        var (dbA, userA) = Open(dbName, tenantA);
        await using (dbA)
        {
            var sync = new IntegrationSyncService(dbA, userA, mcp, new NoopAudit());
            var result = await IntegrationEndpoints.SyncAsync(connectionBId, null, sync, dbA, userA);
            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
            Assert.Empty(mcp.Calls);
            Assert.Empty(await dbA.IntegrationConnections.ForTenant(userA).ToListAsync());
            Assert.Empty(await dbA.SyncRuns.ForTenant(userA).ToListAsync());
            Assert.Empty(await dbA.Companies.ForTenant(userA).ToListAsync());
        }
    }

    private sealed class RecordingAutotaskMcp : IMcpClient
    {
        public List<(Guid ServerId, string Tool, string? Args)> Calls { get; } = [];
        public string CompaniesJson { get; init; } = AutotaskCompanyMapperTests.LastPageFixture;

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((mcpServerId, toolName, argumentsJson));
            var inner = CompaniesJson;
            if (!string.IsNullOrWhiteSpace(argumentsJson)
                && argumentsJson.Contains("nextPageUrl", StringComparison.Ordinal))
            {
                inner = AutotaskCompanyMapperTests.EmptyItemsFixture;
            }

            var body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "1",
                result = new { content = new[] { new { type = "text", text = inner } } },
            });
            return Task.FromResult(body);
        }
    }

    private static async Task<(McpServer Server, IntegrationConnection Connection)> SeedAutotaskCompactAsync(
        DocuEngAIneDbContext db, FakeCurrentUser user, bool skipInactive = true, bool updateCompanyDetails = false)
    {
        var server = new McpServer
        {
            TenantId = user.TenantId!.Value,
            Name = "StackJack Compact",
            Kind = McpServerKind.StackJackCompact,
            Transport = McpTransport.Http,
            EndpointUrl = McpServerDefaults.StackJackCompactEndpoint,
            AuthSecretName = "kv-stackjack-compact",
        };
        db.McpServers.Add(server);
        await db.SaveChangesAsync();

        var connection = new IntegrationConnection
        {
            TenantId = user.TenantId.Value,
            Provider = IntegrationProvider.Autotask,
            DisplayName = "Autotask",
            McpServerId = server.Id,
            SkipInactive = skipInactive,
            UpdateCompanyDetails = updateCompanyDetails,
        };
        db.IntegrationConnections.Add(connection);
        await db.SaveChangesAsync();
        return (server, connection);
    }

    [Fact]
    public async Task Autotask_SyncAsync_Creates_Company_And_Mapping_From_Id_Zero()
    {
        var mcp = new RecordingAutotaskMcp { CompaniesJson = AutotaskCompanyMapperTests.LastPageFixture };
        var (db, user, sync) = Create(mcp);
        var (server, connection) = await SeedAutotaskCompactAsync(db, user, skipInactive: false);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(2, run.ItemsCreated);
        Assert.Equal(0, run.ItemsSkipped);
        Assert.Equal("at_list_companies", Assert.Single(mcp.Calls).Tool);
        Assert.Equal(server.Id, mcp.Calls[0].ServerId);
        Assert.Contains("\"maxRecords\":50", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("nextPageUrl", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("at_list_active_companies", mcp.Calls.Select(c => c.Tool));
        Assert.DoesNotContain("at_list_customer_companies", mcp.Calls.Select(c => c.Tool));

        var companies = await db.Companies.OrderBy(c => c.Name).ToListAsync();
        Assert.Equal(2, companies.Count);

        var pcc = Assert.Single(companies, c => c.Name == "Pacific Cloud Cyber");
        Assert.Equal("PCC", pcc.Slug);
        Assert.Equal("Salem", pcc.City);
        Assert.Equal("Oregon", pcc.State);
        Assert.Equal("222 Comercial St", pcc.Address);
        Assert.Null(pcc.Website);
        Assert.Null(pcc.HaloClientId);
        Assert.Null(pcc.NinjaOrganizationId);
        Assert.Equal("0", CompanyIdentity.ReadExternalIds(pcc.ExternalIdsJson)["autotask"]);

        var mapping = Assert.Single(await db.IntegrationMappings.Where(m => m.ExternalId == "0").ToListAsync());
        Assert.Equal("company", mapping.ExternalType);
        Assert.Equal(pcc.Id, mapping.LocalEntityId);
    }

    [Fact]
    public async Task Autotask_SkipInactive_Drops_IsActive_False()
    {
        var mcp = new RecordingAutotaskMcp { CompaniesJson = AutotaskCompanyMapperTests.LastPageFixture };
        var (db, user, sync) = Create(mcp);
        var (_, connection) = await SeedAutotaskCompactAsync(db, user, skipInactive: true);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(1, run.ItemsCreated);
        Assert.Equal(1, run.ItemsSkipped);
        var company = await db.Companies.SingleAsync();
        Assert.Equal("Pacific Cloud Cyber", company.Name);
        Assert.Equal("0", CompanyIdentity.ReadExternalIds(company.ExternalIdsJson)["autotask"]);
        Assert.DoesNotContain(await db.Companies.ToListAsync(), c => c.Name == "Autotask Corporation");
        Assert.DoesNotContain(await db.IntegrationMappings.ToListAsync(), m => m.ExternalId == "174");
    }

    [Fact]
    public async Task Autotask_SyncAsync_Missing_McpServerId_Fails_Without_Calling_Mcp()
    {
        var mcp = new RecordingAutotaskMcp { CompaniesJson = AutotaskCompanyMapperTests.LastPageFixture };
        var (db, user, sync) = Create(mcp);
        var connection = new IntegrationConnection
        {
            TenantId = user.TenantId!.Value,
            Provider = IntegrationProvider.Autotask,
            DisplayName = "Autotask",
            AuthSecretName = "kv-name-only",
        };
        db.IntegrationConnections.Add(connection);
        await db.SaveChangesAsync();

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Failed, run.Status);
        Assert.Contains("McpServerId", run.ErrorSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Key Vault", run.ErrorSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(mcp.Calls);
        Assert.Empty(await db.Companies.ToListAsync());
    }

    [Fact]
    public async Task Autotask_Other_Tenant_Connection_Sync_Returns_404_And_Does_Not_Call_Mcp()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var mcp = new RecordingAutotaskMcp { CompaniesJson = AutotaskCompanyMapperTests.LastPageFixture };

        Guid connectionBId;
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
            var connection = new IntegrationConnection
            {
                TenantId = tenantB,
                Provider = IntegrationProvider.Autotask,
                DisplayName = "Autotask B",
                McpServerId = server.Id,
            };
            dbB.IntegrationConnections.Add(connection);
            await dbB.SaveChangesAsync();
            connectionBId = connection.Id;
        }

        var (dbA, userA) = Open(dbName, tenantA);
        await using (dbA)
        {
            var sync = new IntegrationSyncService(dbA, userA, mcp, new NoopAudit());
            var result = await IntegrationEndpoints.SyncAsync(connectionBId, null, sync, dbA, userA);
            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
            Assert.Empty(mcp.Calls);
            Assert.Empty(await dbA.IntegrationConnections.ForTenant(userA).ToListAsync());
            Assert.Empty(await dbA.SyncRuns.ForTenant(userA).ToListAsync());
            Assert.Empty(await dbA.Companies.ForTenant(userA).ToListAsync());
        }
    }

    private sealed class RecordingBlackpointMcp : IMcpClient
    {
        public List<(Guid ServerId, string Tool, string? Args)> Calls { get; } = [];
        public string TenantsJson { get; init; } = CompassOneTenantMapperTests.LastPageFixture;

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((mcpServerId, toolName, argumentsJson));
            var inner = TenantsJson;
            if (!string.IsNullOrWhiteSpace(argumentsJson)
                && argumentsJson.Contains("\"page\":2", StringComparison.Ordinal))
            {
                inner = CompassOneTenantMapperTests.EmptyDataFixture;
            }

            var body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "1",
                result = new { content = new[] { new { type = "text", text = inner } } },
            });
            return Task.FromResult(body);
        }
    }

    private static async Task<(McpServer Server, IntegrationConnection Connection)> SeedBlackpointCompactAsync(
        DocuEngAIneDbContext db, FakeCurrentUser user, bool skipInactive = true, bool updateCompanyDetails = false)
    {
        var server = new McpServer
        {
            TenantId = user.TenantId!.Value,
            Name = "StackJack Compact",
            Kind = McpServerKind.StackJackCompact,
            Transport = McpTransport.Http,
            EndpointUrl = McpServerDefaults.StackJackCompactEndpoint,
            AuthSecretName = "kv-stackjack-compact",
        };
        db.McpServers.Add(server);
        await db.SaveChangesAsync();

        var connection = new IntegrationConnection
        {
            TenantId = user.TenantId.Value,
            Provider = IntegrationProvider.Blackpoint,
            DisplayName = "Blackpoint",
            McpServerId = server.Id,
            SkipInactive = skipInactive,
            UpdateCompanyDetails = updateCompanyDetails,
        };
        db.IntegrationConnections.Add(connection);
        await db.SaveChangesAsync();
        return (server, connection);
    }

    [Fact]
    public async Task Blackpoint_SyncAsync_Creates_Company_And_Mapping_From_Tenant_Id_Stores_Domain_Not_Installer()
    {
        var mcp = new RecordingBlackpointMcp { TenantsJson = CompassOneTenantMapperTests.LastPageFixture };
        var (db, user, sync) = Create(mcp);
        var (server, connection) = await SeedBlackpointCompactAsync(db, user);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(1, run.ItemsCreated);
        Assert.Equal(0, run.ItemsSkipped);
        Assert.Equal("compassone_list_tenants", Assert.Single(mcp.Calls).Tool);
        Assert.Equal(server.Id, mcp.Calls[0].ServerId);
        Assert.Contains("\"page\":1", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":50", mcp.Calls[0].Args, StringComparison.Ordinal);

        var company = await db.Companies.SingleAsync();
        Assert.Equal("Adroc Capital LLC", company.Name);
        Assert.Equal("https://adroccap.com", company.Website);
        Assert.DoesNotContain("installer.blackpointcyber.com", company.Website);
        Assert.Null(company.HaloClientId);
        Assert.Null(company.NinjaOrganizationId);
        Assert.Equal("ce212a59-dab3-49ec-b6d7-546a2159b8ad",
            CompanyIdentity.ReadExternalIds(company.ExternalIdsJson)["blackpoint"]);

        var mapping = await db.IntegrationMappings.SingleAsync();
        Assert.Equal("company", mapping.ExternalType);
        Assert.Equal("ce212a59-dab3-49ec-b6d7-546a2159b8ad", mapping.ExternalId);
        Assert.Equal(company.Id, mapping.LocalEntityId);
    }

    [Fact]
    public async Task Blackpoint_SyncAsync_Missing_McpServerId_Fails_Without_Calling_Mcp()
    {
        var mcp = new RecordingBlackpointMcp { TenantsJson = CompassOneTenantMapperTests.LastPageFixture };
        var (db, user, sync) = Create(mcp);
        var connection = new IntegrationConnection
        {
            TenantId = user.TenantId!.Value,
            Provider = IntegrationProvider.Blackpoint,
            DisplayName = "Blackpoint",
            AuthSecretName = "kv-name-only",
        };
        db.IntegrationConnections.Add(connection);
        await db.SaveChangesAsync();

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Failed, run.Status);
        Assert.Contains("McpServerId", run.ErrorSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Key Vault", run.ErrorSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(mcp.Calls);
        Assert.Empty(await db.Companies.ToListAsync());
    }

    [Fact]
    public async Task Blackpoint_Other_Tenant_Connection_Sync_Returns_404_And_Does_Not_Call_Mcp()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var mcp = new RecordingBlackpointMcp { TenantsJson = CompassOneTenantMapperTests.LastPageFixture };

        Guid connectionBId;
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
            var connection = new IntegrationConnection
            {
                TenantId = tenantB,
                Provider = IntegrationProvider.Blackpoint,
                DisplayName = "Blackpoint B",
                McpServerId = server.Id,
            };
            dbB.IntegrationConnections.Add(connection);
            await dbB.SaveChangesAsync();
            connectionBId = connection.Id;
        }

        var (dbA, userA) = Open(dbName, tenantA);
        await using (dbA)
        {
            var sync = new IntegrationSyncService(dbA, userA, mcp, new NoopAudit());
            var result = await IntegrationEndpoints.SyncAsync(connectionBId, null, sync, dbA, userA);
            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
            Assert.Empty(mcp.Calls);
            Assert.Empty(await dbA.IntegrationConnections.ForTenant(userA).ToListAsync());
            Assert.Empty(await dbA.SyncRuns.ForTenant(userA).ToListAsync());
            Assert.Empty(await dbA.Companies.ForTenant(userA).ToListAsync());
        }
    }

    private sealed class RecordingDefensXMcp : IMcpClient
    {
        public List<(Guid ServerId, string Tool, string? Args)> Calls { get; } = [];
        public string CustomersJson { get; init; } = DefensXCustomerMapperTests.LiveCompactListFixture;

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((mcpServerId, toolName, argumentsJson));
            var body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "1",
                result = new { content = new[] { new { type = "text", text = CustomersJson } } },
            });
            return Task.FromResult(body);
        }
    }

    private static async Task<(McpServer Server, IntegrationConnection Connection)> SeedDefensXCompactAsync(
        DocuEngAIneDbContext db, FakeCurrentUser user, bool skipInactive = true, bool updateCompanyDetails = false)
    {
        var server = new McpServer
        {
            TenantId = user.TenantId!.Value,
            Name = "StackJack Compact",
            Kind = McpServerKind.StackJackCompact,
            Transport = McpTransport.Http,
            EndpointUrl = McpServerDefaults.StackJackCompactEndpoint,
            AuthSecretName = "kv-stackjack-compact",
        };
        db.McpServers.Add(server);
        await db.SaveChangesAsync();

        var connection = new IntegrationConnection
        {
            TenantId = user.TenantId.Value,
            Provider = IntegrationProvider.DefensX,
            DisplayName = "DefensX",
            McpServerId = server.Id,
            SkipInactive = skipInactive,
            UpdateCompanyDetails = updateCompanyDetails,
        };
        db.IntegrationConnections.Add(connection);
        await db.SaveChangesAsync();
        return (server, connection);
    }

    [Fact]
    public async Task DefensX_SyncAsync_Creates_Company_And_Mapping_From_Customer_Id_Stores_PrimaryDomain()
    {
        var mcp = new RecordingDefensXMcp { CustomersJson = DefensXCustomerMapperTests.LiveCompactListFixture };
        var (db, user, sync) = Create(mcp);
        var (server, connection) = await SeedDefensXCompactAsync(db, user);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(2, run.ItemsCreated);
        Assert.Equal(0, run.ItemsSkipped);
        var call = Assert.Single(mcp.Calls);
        Assert.Equal("dfx_list_customers", call.Tool);
        Assert.Equal(server.Id, call.ServerId);
        Assert.True(string.IsNullOrWhiteSpace(call.Args));

        var companies = await db.Companies.OrderBy(c => c.Name).ToListAsync();
        Assert.Equal(2, companies.Count);

        var adroc = Assert.Single(companies, c => c.Name == "Adroc Capital");
        Assert.Equal("adroccap.com", adroc.PrimaryDomain);
        Assert.Null(adroc.HaloClientId);
        Assert.Null(adroc.NinjaOrganizationId);
        Assert.Equal("2db9e3bd-020b-4374-8c1d-c6b83d4cb7f4",
            CompanyIdentity.ReadExternalIds(adroc.ExternalIdsJson)["defensx"]);

        var masri = Assert.Single(companies, c => c.Name == "Masri Digital (Customer)");
        Assert.Null(masri.PrimaryDomain);
        Assert.Equal("f1f4ad1e-6709-4f88-bf93-0d2c60abd5ec",
            CompanyIdentity.ReadExternalIds(masri.ExternalIdsJson)["defensx"]);

        var mapping = Assert.Single(await db.IntegrationMappings.Where(m => m.ExternalId == "2db9e3bd-020b-4374-8c1d-c6b83d4cb7f4").ToListAsync());
        Assert.Equal("company", mapping.ExternalType);
        Assert.Equal(adroc.Id, mapping.LocalEntityId);
    }

    [Fact]
    public async Task DefensX_SkipInactive_Drops_Enabled_False()
    {
        var mcp = new RecordingDefensXMcp { CustomersJson = DefensXCustomerMapperTests.SkipInactiveFixture };
        var (db, user, sync) = Create(mcp);
        var (_, connection) = await SeedDefensXCompactAsync(db, user, skipInactive: true);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(1, run.ItemsCreated);
        Assert.Equal(1, run.ItemsSkipped);
        var company = await db.Companies.SingleAsync();
        Assert.Equal("Adroc Capital", company.Name);
        Assert.Equal("adroccap.com", company.PrimaryDomain);
        Assert.Equal("2db9e3bd-020b-4374-8c1d-c6b83d4cb7f4",
            CompanyIdentity.ReadExternalIds(company.ExternalIdsJson)["defensx"]);
        Assert.DoesNotContain(await db.Companies.ToListAsync(), c => c.Name == "Disabled Co");
        Assert.DoesNotContain(await db.IntegrationMappings.ToListAsync(),
            m => m.ExternalId == "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    }

    [Fact]
    public async Task DefensX_SyncAsync_Missing_McpServerId_Fails_Without_Calling_Mcp()
    {
        var mcp = new RecordingDefensXMcp { CustomersJson = DefensXCustomerMapperTests.LiveCompactListFixture };
        var (db, user, sync) = Create(mcp);
        var connection = new IntegrationConnection
        {
            TenantId = user.TenantId!.Value,
            Provider = IntegrationProvider.DefensX,
            DisplayName = "DefensX",
            AuthSecretName = "kv-name-only",
        };
        db.IntegrationConnections.Add(connection);
        await db.SaveChangesAsync();

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Failed, run.Status);
        Assert.Contains("McpServerId", run.ErrorSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Key Vault", run.ErrorSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(mcp.Calls);
        Assert.Empty(await db.Companies.ToListAsync());
    }

    [Fact]
    public async Task DefensX_Other_Tenant_Connection_Sync_Returns_404_And_Does_Not_Call_Mcp()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var mcp = new RecordingDefensXMcp { CustomersJson = DefensXCustomerMapperTests.LiveCompactListFixture };

        Guid connectionBId;
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
            var connection = new IntegrationConnection
            {
                TenantId = tenantB,
                Provider = IntegrationProvider.DefensX,
                DisplayName = "DefensX B",
                McpServerId = server.Id,
            };
            dbB.IntegrationConnections.Add(connection);
            await dbB.SaveChangesAsync();
            connectionBId = connection.Id;
        }

        var (dbA, userA) = Open(dbName, tenantA);
        await using (dbA)
        {
            var sync = new IntegrationSyncService(dbA, userA, mcp, new NoopAudit());
            var result = await IntegrationEndpoints.SyncAsync(connectionBId, null, sync, dbA, userA);
            Assert.Equal(StatusCodes.Status404NotFound, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
            Assert.Empty(mcp.Calls);
            Assert.Empty(await dbA.IntegrationConnections.ForTenant(userA).ToListAsync());
            Assert.Empty(await dbA.SyncRuns.ForTenant(userA).ToListAsync());
            Assert.Empty(await dbA.Companies.ForTenant(userA).ToListAsync());
        }
    }
}
