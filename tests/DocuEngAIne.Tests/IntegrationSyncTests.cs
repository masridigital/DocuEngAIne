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
        Assert.Equal("ninja_list_organizations", Assert.Single(mcp.Calls).Tool);
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
}
