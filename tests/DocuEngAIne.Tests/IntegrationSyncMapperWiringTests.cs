using System.Text.Json;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Core.Mcp;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Integrations;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

/// <summary>
/// Wires Compact mappers that already exist on main into <see cref="IntegrationSyncService"/>.
/// Payloads match the mapper-test fixtures. No live Compact calls.
/// </summary>
public class IntegrationSyncMapperWiringTests
{
    private sealed class NoopAudit : IAuditService
    {
        public Task LogAsync(string action, string entityType, Guid? entityId = null, string? details = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private static (DocuEngAIneDbContext Db, FakeCurrentUser User, IntegrationSyncService Sync) Create(IMcpClient mcp)
    {
        var tenantId = Guid.NewGuid();
        var user = new FakeCurrentUser { TenantId = tenantId, ObjectId = Guid.NewGuid().ToString(), Role = UserRole.Owner };
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new DocuEngAIneDbContext(options, user);
        var sync = new IntegrationSyncService(db, user, mcp, new NoopAudit());
        return (db, user, sync);
    }

    private static async Task<(McpServer Server, IntegrationConnection Connection)> SeedAsync(
        DocuEngAIneDbContext db,
        FakeCurrentUser user,
        IntegrationProvider provider,
        bool skipInactive = true,
        bool skipContacts = false,
        bool skipLocations = false,
        bool skipAssets = false,
        bool autoUpdateAssetNames = false)
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
            Provider = provider,
            DisplayName = provider.ToString(),
            McpServerId = server.Id,
            SkipInactive = skipInactive,
            SkipContacts = skipContacts,
            SkipLocations = skipLocations,
            SkipAssets = skipAssets,
            AutoUpdateAssetNames = autoUpdateAssetNames,
        };
        db.IntegrationConnections.Add(connection);
        await db.SaveChangesAsync();
        return (server, connection);
    }

    private static string WrapRpc(string inner) => JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0",
        id = "1",
        result = new { content = new[] { new { type = "text", text = inner } } },
    });

    [Fact]
    public async Task Halo_SyncAsync_Creates_People_And_Locations_After_Companies()
    {
        var mcp = new RecordingHaloMcp
        {
            Clients =
            [
                new { id = 12, name = "Masri", inactive = false },
                new { id = 29, name = "Inactive Co", inactive = true },
            ],
            UsersJson = HaloUserMapperTests.CompactListFixture,
            SitesJson = HaloSiteMapperTests.SitesEnvelopeFixture,
        };
        var (db, user, sync) = Create(mcp);
        var (_, connection) = await SeedAsync(db, user, IntegrationProvider.Halo);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(HaloClientMapper.ToolName, mcp.Calls[0].Tool);
        Assert.Contains(mcp.Calls, c => c.Tool == HaloSiteMapper.ToolName);
        Assert.Contains(mcp.Calls, c => c.Tool == HaloUserMapper.ToolName);

        var company = await db.Companies.SingleAsync();
        Assert.Equal("Masri", company.Name);
        Assert.Equal("12", company.HaloClientId);

        var peopleType = Assert.Single(await db.AssetTypes.Where(t => t.Name == IntegrationSyncService.PeopleAssetTypeName).ToListAsync());
        var locationType = Assert.Single(await db.AssetTypes.Where(t => t.Name == IntegrationSyncService.LocationAssetTypeName).ToListAsync());

        var james = Assert.Single(await db.Assets.Where(a => a.AssetTypeId == peopleType.Id).ToListAsync());
        Assert.Equal("James Masri", james.Name);
        Assert.Equal(company.Id, james.CompanyId);

        var hq = Assert.Single(await db.Assets.Where(a => a.AssetTypeId == locationType.Id).ToListAsync());
        Assert.Equal("HQ", hq.Name);
        Assert.Equal(company.Id, hq.CompanyId);

        var contactMapping = Assert.Single(await db.IntegrationMappings.Where(m => m.ExternalType == "contact").ToListAsync());
        Assert.Equal("41", contactMapping.ExternalId);
        Assert.Equal(james.Id, contactMapping.LocalEntityId);
        Assert.Contains("james@example.com", contactMapping.MetadataJson, StringComparison.Ordinal);

        var locationMapping = Assert.Single(await db.IntegrationMappings.Where(m => m.ExternalType == "location").ToListAsync());
        Assert.Equal("4", locationMapping.ExternalId);
        Assert.Equal(hq.Id, locationMapping.LocalEntityId);
        Assert.Contains("Austin", locationMapping.MetadataJson, StringComparison.Ordinal);

        Assert.DoesNotContain(await db.Assets.ToListAsync(), a => a.Name == "Inactive User");
        Assert.DoesNotContain(await db.Assets.ToListAsync(), a => a.Name == "Closed Depot");
        // Inactive company + inactive user + inactive site.
        Assert.Equal(3, run.ItemsSkipped);
        Assert.Equal(3, run.ItemsCreated);
    }

    [Fact]
    public async Task Halo_SyncAsync_SkipContacts_Never_Calls_The_User_Tool()
    {
        var mcp = new RecordingHaloMcp
        {
            Clients = [new { id = 12, name = "Masri", inactive = false }],
            UsersJson = HaloUserMapperTests.CompactListFixture,
            SitesJson = HaloSiteMapperTests.SitesEnvelopeFixture,
        };
        var (db, user, sync) = Create(mcp);
        var (_, connection) = await SeedAsync(db, user, IntegrationProvider.Halo, skipContacts: true);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.DoesNotContain(mcp.Calls, c => c.Tool == HaloUserMapper.ToolName);
        Assert.Contains(mcp.Calls, c => c.Tool == HaloSiteMapper.ToolName);
        Assert.Empty(await db.AssetTypes.Where(t => t.Name == IntegrationSyncService.PeopleAssetTypeName).ToListAsync());
        Assert.Single(await db.Assets.ToListAsync());
        Assert.Equal("HQ", Assert.Single(await db.Assets.ToListAsync()).Name);
    }

    [Fact]
    public async Task Halo_SyncAsync_SkipLocations_Never_Calls_The_Site_Tool()
    {
        var mcp = new RecordingHaloMcp
        {
            Clients = [new { id = 12, name = "Masri", inactive = false }],
            UsersJson = HaloUserMapperTests.CompactListFixture,
            SitesJson = HaloSiteMapperTests.SitesEnvelopeFixture,
        };
        var (db, user, sync) = Create(mcp);
        var (_, connection) = await SeedAsync(db, user, IntegrationProvider.Halo, skipLocations: true);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.DoesNotContain(mcp.Calls, c => c.Tool == HaloSiteMapper.ToolName);
        Assert.Contains(mcp.Calls, c => c.Tool == HaloUserMapper.ToolName);
        Assert.Empty(await db.AssetTypes.Where(t => t.Name == IntegrationSyncService.LocationAssetTypeName).ToListAsync());
        Assert.Equal("James Masri", Assert.Single(await db.Assets.ToListAsync()).Name);
    }

    [Fact]
    public async Task Halo_SyncAsync_SkipInactive_False_Imports_Inactive_User_And_Site()
    {
        var mcp = new RecordingHaloMcp
        {
            Clients =
            [
                new { id = 12, name = "Masri", inactive = false },
                new { id = 29, name = "Inactive Co", inactive = true },
            ],
            UsersJson = HaloUserMapperTests.CompactListFixture,
            SitesJson = HaloSiteMapperTests.SitesEnvelopeFixture,
        };
        var (db, user, sync) = Create(mcp);
        var (_, connection) = await SeedAsync(db, user, IntegrationProvider.Halo, skipInactive: false);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(2, await db.Companies.CountAsync());
        var peopleType = Assert.Single(await db.AssetTypes.Where(t => t.Name == IntegrationSyncService.PeopleAssetTypeName).ToListAsync());
        var locationType = Assert.Single(await db.AssetTypes.Where(t => t.Name == IntegrationSyncService.LocationAssetTypeName).ToListAsync());
        Assert.Equal(2, await db.Assets.CountAsync(a => a.AssetTypeId == peopleType.Id));
        Assert.Equal(2, await db.Assets.CountAsync(a => a.AssetTypeId == locationType.Id));
        Assert.Contains(await db.Assets.ToListAsync(), a => a.Name == "Inactive User");
        Assert.Contains(await db.Assets.ToListAsync(), a => a.Name == "Closed Depot");
        Assert.Equal(0, run.ItemsSkipped);
    }

    [Fact]
    public async Task Halo_SyncAsync_Twice_Updates_Contacts_And_Locations_Instead_Of_Duplicating()
    {
        var mcp = new RecordingHaloMcp
        {
            Clients = [new { id = 12, name = "Masri", inactive = false }],
            UsersJson = HaloUserMapperTests.CompactListFixture,
            SitesJson = HaloSiteMapperTests.SitesEnvelopeFixture,
        };
        var (db, user, sync) = Create(mcp);
        var (_, connection) = await SeedAsync(db, user, IntegrationProvider.Halo);

        await sync.SyncAsync(connection.Id);
        var second = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, second.Status);
        Assert.Equal(0, second.ItemsCreated);
        Assert.Equal(3, second.ItemsUpdated); // company + contact + location
        var peopleType = Assert.Single(await db.AssetTypes.Where(t => t.Name == IntegrationSyncService.PeopleAssetTypeName).ToListAsync());
        var locationType = Assert.Single(await db.AssetTypes.Where(t => t.Name == IntegrationSyncService.LocationAssetTypeName).ToListAsync());
        Assert.Equal(1, await db.Assets.CountAsync(a => a.AssetTypeId == peopleType.Id));
        Assert.Equal(1, await db.Assets.CountAsync(a => a.AssetTypeId == locationType.Id));
        Assert.Equal(1, await db.IntegrationMappings.CountAsync(m => m.ExternalType == "contact"));
        Assert.Equal(1, await db.IntegrationMappings.CountAsync(m => m.ExternalType == "location"));
    }

    [Fact]
    public async Task Halo_SyncAsync_Does_Not_Clobber_Contact_Name_When_AutoUpdateAssetNames_False()
    {
        var mcp = new RecordingHaloMcp
        {
            Clients = [new { id = 12, name = "Masri", inactive = false }],
            UsersJson = HaloUserMapperTests.CompactListFixture,
            SitesJson = """{"sites":[]}""",
        };
        var (db, user, sync) = Create(mcp);
        var (_, connection) = await SeedAsync(db, user, IntegrationProvider.Halo);

        await sync.SyncAsync(connection.Id);
        var james = Assert.Single(await db.Assets.ToListAsync());
        james.Name = "J. Masri";
        await db.SaveChangesAsync();

        await sync.SyncAsync(connection.Id);

        Assert.Equal("J. Masri", (await db.Assets.SingleAsync()).Name);
    }

    [Fact]
    public async Task Action1_SyncAsync_Creates_Devices_After_Orgs_Via_Company_Mappings()
    {
        var mcp = new RecordingAction1Mcp
        {
            OrganizationsJson = Action1OrganizationMapperTests.LiveCompactListFixture,
            EndpointsJson = Action1EndpointMapperTests.CompactListFixture,
        };
        var (db, user, sync) = Create(mcp);
        var (_, connection) = await SeedAsync(db, user, IntegrationProvider.Action1);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(Action1OrganizationMapper.ToolName, mcp.Calls[0].Tool);
        var endpointCalls = mcp.Calls.Where(c => c.Tool == Action1EndpointMapper.ToolName).ToList();
        Assert.Single(endpointCalls);
        Assert.Contains($"\"orgId\":\"{Action1EndpointMapperTests.AdrocOrgId}\"", endpointCalls[0].Args, StringComparison.Ordinal);

        var company = await db.Companies.SingleAsync();
        Assert.Equal("Adroc Capital", company.Name);

        var devices = await db.Assets.ToListAsync();
        Assert.Equal(3, devices.Count);
        var computerType = Assert.Single(await db.AssetTypes.ToListAsync());
        Assert.Equal(IntegrationSyncService.ComputerAssetTypeName, computerType.Name);
        Assert.All(devices, a => Assert.Equal(company.Id, a.CompanyId));
        Assert.All(devices, a => Assert.Equal(computerType.Id, a.AssetTypeId));
        Assert.Contains(devices, a => a.Name == "WKS-ADROC-01");
        Assert.Contains(devices, a => a.Name == "WKS-ADROC-02");
        Assert.Contains(devices, a => a.Name == "MAC-ADROC-01");
        Assert.Equal(3, await db.IntegrationMappings.CountAsync(m => m.ExternalType == "device"));
        Assert.Equal(4, run.ItemsCreated);
    }

    [Fact]
    public async Task Action1_SyncAsync_SkipAssets_Never_Calls_The_Endpoint_Tool()
    {
        var mcp = new RecordingAction1Mcp
        {
            OrganizationsJson = Action1OrganizationMapperTests.LiveCompactListFixture,
            EndpointsJson = Action1EndpointMapperTests.CompactListFixture,
        };
        var (db, user, sync) = Create(mcp);
        var (_, connection) = await SeedAsync(db, user, IntegrationProvider.Action1, skipAssets: true);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.DoesNotContain(mcp.Calls, c => c.Tool == Action1EndpointMapper.ToolName);
        Assert.Empty(await db.Assets.ToListAsync());
        Assert.Equal(1, run.ItemsCreated);
    }

    [Fact]
    public async Task Cipp_SyncAsync_Creates_Devices_With_TenantFilter_Equal_To_Company_ExternalId()
    {
        var mcp = new RecordingCippMcp
        {
            TenantsJson = CippTenantMapperTests.LiveCompactListFixture,
            DevicesJson = CippDeviceMapperTests.DeviceListFixture,
        };
        var (db, user, sync) = Create(mcp);
        var (_, connection) = await SeedAsync(db, user, IntegrationProvider.Cipp);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(CippTenantMapper.ToolName, mcp.Calls[0].Tool);
        var deviceCalls = mcp.Calls.Where(c => c.Tool == CippDeviceMapper.ToolName).ToList();
        var deviceCall = Assert.Single(deviceCalls);
        Assert.Contains($"\"tenantFilter\":\"{CippDeviceMapperTests.AdrocCustomerId}\"", deviceCall.Args, StringComparison.Ordinal);
        Assert.DoesNotContain(CippDeviceMapperTests.AdrocTenantFilter, deviceCall.Args, StringComparison.Ordinal);
        Assert.DoesNotContain("deadbeef", string.Join("", mcp.Calls.Select(c => c.Args)), StringComparison.Ordinal);

        var company = await db.Companies.SingleAsync();
        Assert.Equal("ADROC Capital, LLC", company.Name);

        var devices = await db.Assets.ToListAsync();
        Assert.Equal(2, devices.Count);
        Assert.All(devices, a => Assert.Equal(company.Id, a.CompanyId));
        Assert.Contains(devices, a => a.Name == "ADROC-LAPTOP-01");
        Assert.Contains(devices, a => a.Name == "ADROC-IPHONE-12");
        Assert.Equal(2, await db.IntegrationMappings.CountAsync(m => m.ExternalType == "device"));
        Assert.Equal(3, run.ItemsCreated);
        Assert.Equal(1, run.ItemsSkipped);
    }

    [Fact]
    public async Task Cipp_SyncAsync_SkipAssets_Never_Calls_The_Device_Tool()
    {
        var mcp = new RecordingCippMcp
        {
            TenantsJson = CippTenantMapperTests.LiveCompactListFixture,
            DevicesJson = CippDeviceMapperTests.DeviceListFixture,
        };
        var (db, user, sync) = Create(mcp);
        var (_, connection) = await SeedAsync(db, user, IntegrationProvider.Cipp, skipAssets: true);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.DoesNotContain(mcp.Calls, c => c.Tool == CippDeviceMapper.ToolName);
        Assert.Empty(await db.Assets.ToListAsync());
        Assert.Equal(1, run.ItemsCreated);
    }

    [Fact]
    public async Task Meraki_SyncAsync_Creates_Networks_After_Orgs()
    {
        var mcp = new RecordingMerakiMcp
        {
            OrganizationsJson = MerakiOrganizationMapperTests.LiveCompactListFixture,
            NetworksJson = MerakiNetworkMapperTests.CompactNetworkListFixture,
        };
        var (db, user, sync) = Create(mcp);
        var (_, connection) = await SeedAsync(db, user, IntegrationProvider.Meraki);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(MerakiOrganizationMapper.ToolName, mcp.Calls[0].Tool);
        var networkCalls = mcp.Calls.Where(c => c.Tool == MerakiNetworkMapper.ToolName).ToList();
        Assert.Equal(2, networkCalls.Count);
        Assert.Contains(networkCalls, c => c.Args is not null && c.Args.Contains("\"organizationId\":\"1279651\"", StringComparison.Ordinal));
        Assert.Contains(networkCalls, c => c.Args is not null && c.Args.Contains("\"organizationId\":\"1721429\"", StringComparison.Ordinal));

        var compression = Assert.Single(await db.Companies.Where(c => c.Name == "7 Compression").ToListAsync());
        var networks = await db.Assets.ToListAsync();
        Assert.Equal(2, networks.Count);
        var lanType = Assert.Single(await db.AssetTypes.ToListAsync());
        Assert.Equal(IntegrationSyncService.NetworkAssetTypeName, lanType.Name);
        Assert.All(networks, a => Assert.Equal(compression.Id, a.CompanyId));
        Assert.All(networks, a => Assert.Equal(lanType.Id, a.AssetTypeId));
        Assert.Contains(networks, a => a.Name == "Main Office");
        Assert.Contains(networks, a => a.Name == "Long Island Office");
        Assert.Equal(2, await db.IntegrationMappings.CountAsync(m => m.ExternalType == "network"));
        Assert.Equal(4, run.ItemsCreated);
    }

    [Fact]
    public async Task Meraki_SyncAsync_SkipLocations_Never_Calls_The_Network_Tool()
    {
        var mcp = new RecordingMerakiMcp
        {
            OrganizationsJson = MerakiOrganizationMapperTests.LiveCompactListFixture,
            NetworksJson = MerakiNetworkMapperTests.CompactNetworkListFixture,
        };
        var (db, user, sync) = Create(mcp);
        var (_, connection) = await SeedAsync(db, user, IntegrationProvider.Meraki, skipLocations: true);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.DoesNotContain(mcp.Calls, c => c.Tool == MerakiNetworkMapper.ToolName);
        Assert.Empty(await db.Assets.ToListAsync());
        Assert.Equal(2, run.ItemsCreated);
    }

    [Fact]
    public async Task UniFi_SyncAsync_Creates_Sites_And_Devices_After_Hosts()
    {
        var mcp = new RecordingUnifiMcp
        {
            HostsJson = UnifiHostMapperTests.LiveCompactListFixture,
            SitesJson = UnifiSiteMapperTests.LiveCompactListFixture,
            DevicesJson = UnifiDeviceMapperTests.CompactListFixture,
        };
        var (db, user, sync) = Create(mcp);
        var (_, connection) = await SeedAsync(db, user, IntegrationProvider.UniFi);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(UnifiHostMapper.ToolName, mcp.Calls[0].Tool);
        Assert.Contains(mcp.Calls, c => c.Tool == UnifiSiteMapper.ToolName);
        Assert.Contains(mcp.Calls, c => c.Tool == UnifiDeviceMapper.ToolName);

        var company = await db.Companies.SingleAsync();
        Assert.Equal("Adroc Capital: 1425 RXR Plaza", company.Name);

        var locationType = Assert.Single(await db.AssetTypes.Where(t => t.Name == IntegrationSyncService.LocationAssetTypeName).ToListAsync());
        var computerType = Assert.Single(await db.AssetTypes.Where(t => t.Name == IntegrationSyncService.ComputerAssetTypeName).ToListAsync());

        var site = Assert.Single(await db.Assets.Where(a => a.AssetTypeId == locationType.Id).ToListAsync());
        Assert.Equal("default", site.Name);
        Assert.Equal(company.Id, site.CompanyId);

        var devices = await db.Assets.Where(a => a.AssetTypeId == computerType.Id).ToListAsync();
        Assert.Equal(2, devices.Count);
        Assert.All(devices, a => Assert.Equal(company.Id, a.CompanyId));
        Assert.Contains(devices, a => a.Name == "Office AP");
        Assert.Contains(devices, a => a.Name == "UDM Pro");

        Assert.DoesNotContain(await db.Assets.ToListAsync(), a => a.Name == "Warehouse");
        Assert.DoesNotContain(await db.Assets.ToListAsync(), a => a.Name == "Core Switch");
        Assert.Equal(1, await db.IntegrationMappings.CountAsync(m => m.ExternalType == "location"));
        Assert.Equal(2, await db.IntegrationMappings.CountAsync(m => m.ExternalType == "device"));
        Assert.Equal(4, run.ItemsCreated);
        Assert.True(run.ItemsSkipped >= 1);
    }

    [Fact]
    public async Task UniFi_SyncAsync_SkipLocations_Never_Calls_The_Site_Tool()
    {
        var mcp = new RecordingUnifiMcp
        {
            HostsJson = UnifiHostMapperTests.LiveCompactListFixture,
            SitesJson = UnifiSiteMapperTests.LiveCompactListFixture,
            DevicesJson = UnifiDeviceMapperTests.CompactListFixture,
        };
        var (db, user, sync) = Create(mcp);
        var (_, connection) = await SeedAsync(db, user, IntegrationProvider.UniFi, skipLocations: true);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.DoesNotContain(mcp.Calls, c => c.Tool == UnifiSiteMapper.ToolName);
        Assert.Contains(mcp.Calls, c => c.Tool == UnifiDeviceMapper.ToolName);
        Assert.Empty(await db.AssetTypes.Where(t => t.Name == IntegrationSyncService.LocationAssetTypeName).ToListAsync());
        var computerType = Assert.Single(await db.AssetTypes.ToListAsync());
        Assert.Equal(IntegrationSyncService.ComputerAssetTypeName, computerType.Name);
        Assert.Equal(2, await db.Assets.CountAsync(a => a.AssetTypeId == computerType.Id));
    }

    [Fact]
    public async Task UniFi_SyncAsync_SkipAssets_Never_Calls_The_Device_Tool()
    {
        var mcp = new RecordingUnifiMcp
        {
            HostsJson = UnifiHostMapperTests.LiveCompactListFixture,
            SitesJson = UnifiSiteMapperTests.LiveCompactListFixture,
            DevicesJson = UnifiDeviceMapperTests.CompactListFixture,
        };
        var (db, user, sync) = Create(mcp);
        var (_, connection) = await SeedAsync(db, user, IntegrationProvider.UniFi, skipAssets: true);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.DoesNotContain(mcp.Calls, c => c.Tool == UnifiDeviceMapper.ToolName);
        Assert.Contains(mcp.Calls, c => c.Tool == UnifiSiteMapper.ToolName);
        Assert.Empty(await db.AssetTypes.Where(t => t.Name == IntegrationSyncService.ComputerAssetTypeName).ToListAsync());
        var locationType = Assert.Single(await db.AssetTypes.ToListAsync());
        Assert.Equal(IntegrationSyncService.LocationAssetTypeName, locationType.Name);
        Assert.Single(await db.Assets.Where(a => a.AssetTypeId == locationType.Id).ToListAsync());
    }

    private sealed class RecordingHaloMcp : IMcpClient
    {
        public List<(Guid ServerId, string Tool, string? Args)> Calls { get; } = [];
        public List<object> Clients { get; init; } = [];
        public string UsersJson { get; init; } = """{"users":[]}""";
        public string SitesJson { get; init; } = """{"sites":[]}""";

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((mcpServerId, toolName, argumentsJson));
            string inner;
            if (toolName == HaloUserMapper.ToolName)
            {
                inner = SliceUsers(UsersJson, argumentsJson);
            }
            else if (toolName == HaloSiteMapper.ToolName)
            {
                inner = SitesJson;
            }
            else
            {
                var pageNo = 1;
                var pageSize = HaloClientMapper.DefaultPageSize;
                if (!string.IsNullOrWhiteSpace(argumentsJson))
                {
                    using var doc = JsonDocument.Parse(argumentsJson);
                    if (doc.RootElement.TryGetProperty("pageNo", out var p))
                        pageNo = p.GetInt32();
                    if (doc.RootElement.TryGetProperty("pageSize", out var s))
                        pageSize = s.GetInt32();
                }

                var slice = Clients.Skip((pageNo - 1) * pageSize).Take(pageSize).ToList();
                inner = JsonSerializer.Serialize(new { clients = slice });
            }

            return Task.FromResult(WrapRpc(inner));
        }

        private static string SliceUsers(string json, string? argumentsJson)
        {
            var pageNo = 1;
            var pageSize = HaloUserMapper.DefaultPageSize;
            if (!string.IsNullOrWhiteSpace(argumentsJson))
            {
                using var args = JsonDocument.Parse(argumentsJson);
                if (args.RootElement.TryGetProperty("pageNo", out var p))
                    pageNo = p.GetInt32();
                if (args.RootElement.TryGetProperty("pageSize", out var s))
                    pageSize = s.GetInt32();
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("users", out var users) || users.ValueKind != JsonValueKind.Array)
                return """{"users":[]}""";

            var slice = users.EnumerateArray().Skip((pageNo - 1) * pageSize).Take(pageSize)
                .Select(u => u.GetRawText()).ToList();
            return "{\"users\":[" + string.Join(",", slice) + "]}";
        }
    }

    private sealed class RecordingAction1Mcp : IMcpClient
    {
        public List<(Guid ServerId, string Tool, string? Args)> Calls { get; } = [];
        public string OrganizationsJson { get; init; } = """{"items":[],"next_page":""}""";
        public string EndpointsJson { get; init; } = """{"items":[],"next":null}""";

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((mcpServerId, toolName, argumentsJson));
            var inner = toolName == Action1EndpointMapper.ToolName ? EndpointsJson : OrganizationsJson;
            return Task.FromResult(WrapRpc(inner));
        }
    }

    private sealed class RecordingCippMcp : IMcpClient
    {
        public List<(Guid ServerId, string Tool, string? Args)> Calls { get; } = [];
        public string TenantsJson { get; init; } = "[]";
        public string DevicesJson { get; init; } = "[]";

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((mcpServerId, toolName, argumentsJson));
            var inner = toolName == CippDeviceMapper.ToolName ? DevicesJson : TenantsJson;
            return Task.FromResult(WrapRpc(inner));
        }
    }

    private sealed class RecordingMerakiMcp : IMcpClient
    {
        public List<(Guid ServerId, string Tool, string? Args)> Calls { get; } = [];
        public string OrganizationsJson { get; init; } = "[]";
        public string NetworksJson { get; init; } = "[]";

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((mcpServerId, toolName, argumentsJson));
            var inner = toolName == MerakiNetworkMapper.ToolName ? NetworksJson : OrganizationsJson;
            return Task.FromResult(WrapRpc(inner));
        }
    }

    private sealed class RecordingUnifiMcp : IMcpClient
    {
        public List<(Guid ServerId, string Tool, string? Args)> Calls { get; } = [];
        public string HostsJson { get; init; } = """{"data":[]}""";
        public string SitesJson { get; init; } = """{"data":[]}""";
        public string DevicesJson { get; init; } = """{"data":[]}""";

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((mcpServerId, toolName, argumentsJson));
            var inner = toolName switch
            {
                var t when t == UnifiSiteMapper.ToolName => SitesJson,
                var t when t == UnifiDeviceMapper.ToolName => DevicesJson,
                _ => HostsJson,
            };
            return Task.FromResult(WrapRpc(inner));
        }
    }
}
