using System.Text.Json;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Core.Mcp;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Integrations;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

public class NinjaDeviceMapperTests
{
    // Live Compact ninja_list_devices JSON array (field names exact; not a wrapper object).
    // Device 562 and 707 have no displayName at all -- that is the common case, not an edge case.
    // Organizations 11/2/22 exist in NinjaOrganizationMapperTests.LiveCompactListFixture; 24 does not,
    // so device 718 is the "organization was never mapped" case.
    public const string LiveCompactDeviceListFixture = """
        [{"id":402,"uid":"d78bf976-6207-4edb-88ff-0dd67dde7aa4","organizationId":11,"locationId":18,"nodeClass":"WINDOWS_WORKSTATION","nodeRoleId":201,"rolePolicyId":75,"approvalStatus":"APPROVED","offline":false,"displayName":"HIPPO","systemName":"HIPPO","dnsName":"Hippo","created":1697827578.905000000,"lastContact":1787898261.504000000,"lastUpdate":1787898261.504000000},{"id":551,"uid":"13304af9-5f7e-4c63-98fb-2aab8744f704","organizationId":11,"locationId":18,"nodeClass":"WINDOWS_WORKSTATION","nodeRoleId":201,"rolePolicyId":75,"approvalStatus":"APPROVED","offline":false,"displayName":"EAGLE","systemName":"EAGLE","dnsName":"Eagle","created":1704553449.048256000,"lastContact":1787898233.083000000,"lastUpdate":1787898233.083000000},{"id":562,"uid":"74783fbd-cea4-4611-81bd-d80e73d53278","organizationId":2,"locationId":24,"nodeClass":"MAC","nodeRoleId":205,"rolePolicyId":54,"approvalStatus":"APPROVED","offline":true,"systemName":"Mac.lan","dnsName":"Mac.lan","created":1705693792.857588000,"lastContact":1787880643.767000000,"lastUpdate":1787880608.038000000},{"id":707,"uid":"c37dc896-2679-4594-9e57-e60d26a61b3d","organizationId":22,"locationId":37,"nodeClass":"WINDOWS_WORKSTATION","nodeRoleId":202,"rolePolicyId":95,"approvalStatus":"APPROVED","offline":false,"systemName":"AC-LT-01","dnsName":"AC-LT-01","created":1729685954.426026000,"lastContact":1787898231.462000000,"lastUpdate":1787897452.794000000},{"id":718,"uid":"d0923aaf-0e0f-4fc1-8ebb-e58d40896caa","organizationId":24,"locationId":43,"nodeClass":"WINDOWS_WORKSTATION","nodeRoleId":201,"rolePolicyId":101,"policyId":91,"approvalStatus":"APPROVED","offline":false,"displayName":"BL - Property Intel Azure VM","systemName":"property-intel-","dnsName":"property-intel-","created":1732647568.307677000,"lastContact":1787898261.916000000,"lastUpdate":1787898261.916000000}]
        """;

    // Hand-built, not captured: exercises rows the live sample never contains.
    private const string DegenerateDeviceListFixture = """
        [{"id":900,"systemName":"NO-ORG"},{"organizationId":11,"systemName":"NO-ID"},{"id":901,"organizationId":11},{"id":902,"organizationId":11,"nodeClass":"LINUX_WORKSTATION","systemName":"OK-01","dnsName":"ok-01"}]
        """;

    [Fact]
    public void MapDevices_LiveCompactList_MapsIdOrganizationAndNodeClass()
    {
        var devices = NinjaDeviceMapper.MapDevices(LiveCompactDeviceListFixture);

        Assert.Equal(5, devices.Count);

        var hippo = devices[0];
        Assert.Equal("402", hippo.ExternalId);
        Assert.Equal("11", hippo.OrganizationExternalId);
        Assert.Equal("HIPPO", hippo.Name);
        Assert.Equal("WINDOWS_WORKSTATION", hippo.NodeClass);
        Assert.Equal("HIPPO", hippo.SystemName);
        Assert.Equal("Hippo", hippo.DnsName);

        Assert.Equal("718", devices[^1].ExternalId);
        Assert.Equal("24", devices[^1].OrganizationExternalId);
        Assert.Equal("BL - Property Intel Azure VM", devices[^1].Name);
    }

    [Fact]
    public void MapDevices_WithoutDisplayName_FallsBackToSystemName()
    {
        var devices = NinjaDeviceMapper.MapDevices(LiveCompactDeviceListFixture);

        var mac = Assert.Single(devices, d => d.ExternalId == "562");
        Assert.Equal("Mac.lan", mac.Name);
        Assert.Equal("Mac.lan", mac.SystemName);
        Assert.Equal("MAC", mac.NodeClass);
        Assert.Equal("2", mac.OrganizationExternalId);

        var laptop = Assert.Single(devices, d => d.ExternalId == "707");
        Assert.Equal("AC-LT-01", laptop.Name);
    }

    [Fact]
    public void MapDevices_DropsRowsWithoutIdOrganizationOrAnyName()
    {
        var devices = NinjaDeviceMapper.MapDevices(DegenerateDeviceListFixture, out var lastId);

        var only = Assert.Single(devices);
        Assert.Equal("902", only.ExternalId);
        Assert.Equal("11", only.OrganizationExternalId);
        Assert.Equal("OK-01", only.Name);

        // The cursor still advances past dropped rows, or PullAsync would loop on the same page.
        Assert.Equal(902, lastId);
    }

    [Fact]
    public void MapDevices_JsonRpcContentTextArray_UnwrapsToDeviceList()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = LiveCompactDeviceListFixture } } },
        });

        var devices = NinjaDeviceMapper.MapDevices(wrapped);
        Assert.Equal(5, devices.Count);
        Assert.Equal("402", devices[0].ExternalId);
        Assert.Equal("HIPPO", devices[0].Name);
    }

    [Fact]
    public void MapDevices_ToolError_Throws()
    {
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            error = new { code = -32000, message = "ninja auth expired" },
        });

        var ex = Assert.Throws<InvalidOperationException>(() => { NinjaDeviceMapper.MapDevices(body); });
        Assert.Contains("ninja auth expired", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_OmitsAfterOnFirstPage()
    {
        var args = NinjaDeviceMapper.BuildArgumentsJson(afterDeviceId: null);
        Assert.Contains("\"pageSize\":50", args, StringComparison.Ordinal);
        Assert.DoesNotContain("after", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_ClampsPageSizeToMax1000()
    {
        var args = NinjaDeviceMapper.BuildArgumentsJson(afterDeviceId: null, pageSize: 5000);
        Assert.Contains("\"pageSize\":1000", args, StringComparison.Ordinal);
    }

    [Fact]
    public void MapDevices_LiveList_LastDeviceIdIs718()
    {
        NinjaDeviceMapper.MapDevices(LiveCompactDeviceListFixture, out var lastId);
        Assert.Equal(718, lastId);
        var next = NinjaDeviceMapper.BuildArgumentsJson(afterDeviceId: lastId);
        Assert.Contains("\"after\":718", next, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":50", next, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_SecondCall_Receives_After_Cursor_From_First_Page()
    {
        var mcp = new RecordingNinjaMcp { DevicesJson = LiveCompactDeviceListFixture };
        var serverId = Guid.NewGuid();

        var devices = await NinjaDeviceMapper.PullAsync(mcp, serverId, pageSize: 3);

        Assert.Equal(5, devices.Count);
        Assert.Equal(2, mcp.Calls.Count);
        Assert.All(mcp.Calls, c => Assert.Equal("ninja_list_devices", c.Tool));
        Assert.Equal(serverId, mcp.Calls[0].ServerId);
        Assert.DoesNotContain("after", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":3", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"after\":562", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":3", mcp.Calls[1].Args, StringComparison.Ordinal);
    }

    /// <summary>
    /// A full page containing one droppable row must not end the pull. Paging is decided on the rows
    /// the vendor returned, not the rows that mapped — otherwise a single device with no name field
    /// silently abandons every device after it, while the run still reports Succeeded.
    /// </summary>
    [Fact]
    public async Task PullAsync_KeepsPaging_When_A_Full_Page_Contains_A_Dropped_Row()
    {
        // Device 2 has an id (so the cursor can advance) but no displayName, systemName or dnsName,
        // so MapDevice drops it. Devices 4-6 are only reachable if paging continues past that page.
        const string devices = """
            [{"id":1,"organizationId":10,"systemName":"ws-one"},
             {"id":2,"organizationId":10,"nodeClass":"WINDOWS_WORKSTATION"},
             {"id":3,"organizationId":10,"systemName":"ws-three"},
             {"id":4,"organizationId":10,"systemName":"ws-four"},
             {"id":5,"organizationId":10,"systemName":"ws-five"},
             {"id":6,"organizationId":10,"systemName":"ws-six"}]
            """;

        var mcp = new RecordingNinjaMcp { DevicesJson = devices };

        var pulled = await NinjaDeviceMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 3);

        // Five of six map; the sixth call-worth is the empty page that terminates the loop.
        Assert.Equal(5, pulled.Count);
        Assert.Equal(3, mcp.Calls.Count);
        Assert.DoesNotContain(pulled, d => d.ExternalId == "2");
        Assert.Contains(pulled, d => d.ExternalId == "6");
    }

    [Fact]
    public async Task Ninja_SyncAsync_Creates_Assets_Attached_To_Mapped_Companies()
    {
        var mcp = NinjaMcp();
        var (db, user, sync) = Create(mcp);
        var (server, connection) = await SeedNinjaCompactAsync(db, user);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        // 5 companies + 4 devices; device 718 (organization 24) has no company mapping.
        Assert.Equal(9, run.ItemsCreated);
        Assert.Equal(0, run.ItemsUpdated);
        Assert.Equal(1, run.ItemsSkipped);

        Assert.Contains(mcp.Calls, c => c.Tool == "ninja_list_organizations");
        var deviceCall = Assert.Single(mcp.Calls, c => c.Tool == "ninja_list_devices");
        Assert.Equal(server.Id, deviceCall.ServerId);
        Assert.Contains("\"pageSize\":50", deviceCall.Args, StringComparison.Ordinal);
        Assert.DoesNotContain("after", deviceCall.Args, StringComparison.Ordinal);

        var assetType = Assert.Single(await db.AssetTypes.ToListAsync());
        Assert.Equal("Computer Assets", assetType.Name);

        var assets = await db.Assets.ToListAsync();
        Assert.Equal(4, assets.Count);
        Assert.All(assets, a => Assert.Equal(assetType.Id, a.AssetTypeId));
        Assert.All(assets, a => Assert.True(a.CompanyId.HasValue));

        var companies = await db.Companies.ToListAsync();
        var dawn = Assert.Single(companies, c => c.NinjaOrganizationId == "11");
        var masri = Assert.Single(companies, c => c.NinjaOrganizationId == "2");
        var hippo = Assert.Single(assets, a => a.Name == "HIPPO");
        var mac = Assert.Single(assets, a => a.Name == "Mac.lan");
        Assert.Equal(dawn.Id, hippo.CompanyId);
        Assert.Equal(masri.Id, mac.CompanyId);

        var mappings = await db.IntegrationMappings.ToListAsync();
        var deviceMappings = mappings.Where(m => m.ExternalType == "device").ToList();
        Assert.Equal(4, deviceMappings.Count);
        Assert.All(deviceMappings, m => Assert.Equal(nameof(Asset), m.LocalEntityType));
        Assert.All(deviceMappings, m => Assert.Equal(connection.Id, m.IntegrationConnectionId));
        var hippoMapping = Assert.Single(deviceMappings, m => m.ExternalId == "402");
        Assert.Equal(hippo.Id, hippoMapping.LocalEntityId);
        Assert.Contains("WINDOWS_WORKSTATION", hippoMapping.MetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ninja_SyncAsync_Device_Whose_Organization_Has_No_Company_Is_Skipped()
    {
        var mcp = NinjaMcp();
        var (db, user, sync) = Create(mcp);
        var (_, connection) = await SeedNinjaCompactAsync(db, user);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(1, run.ItemsSkipped);
        Assert.DoesNotContain(await db.Companies.ToListAsync(), c => c.NinjaOrganizationId == "24");
        Assert.DoesNotContain(await db.Assets.ToListAsync(), a => a.Name == "BL - Property Intel Azure VM");
        Assert.DoesNotContain(await db.IntegrationMappings.ToListAsync(), m => m.ExternalId == "718");
    }

    [Fact]
    public async Task Ninja_SyncAsync_SkipAssets_Never_Calls_The_Device_Tool()
    {
        var mcp = NinjaMcp();
        var (db, user, sync) = Create(mcp);
        var (_, connection) = await SeedNinjaCompactAsync(db, user, skipAssets: true);

        var run = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(5, run.ItemsCreated);
        Assert.Equal(0, run.ItemsSkipped);
        Assert.DoesNotContain("ninja_list_devices", mcp.Calls.Select(c => c.Tool));
        Assert.Empty(await db.Assets.ToListAsync());
        Assert.Empty(await db.AssetTypes.ToListAsync());
        Assert.Equal(5, await db.Companies.CountAsync());
    }

    [Fact]
    public async Task Ninja_SyncAsync_Twice_Updates_Devices_Instead_Of_Duplicating()
    {
        var mcp = NinjaMcp();
        var (db, user, sync) = Create(mcp);
        var (_, connection) = await SeedNinjaCompactAsync(db, user);

        await sync.SyncAsync(connection.Id);
        var second = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, second.Status);
        Assert.Equal(0, second.ItemsCreated);
        // 5 companies + 4 devices re-seen.
        Assert.Equal(9, second.ItemsUpdated);
        Assert.Equal(1, second.ItemsSkipped);

        Assert.Equal(4, await db.Assets.CountAsync());
        Assert.Single(await db.AssetTypes.ToListAsync());
        Assert.Equal(4, await db.IntegrationMappings.CountAsync(m => m.ExternalType == "device"));
        Assert.Equal(5, await db.IntegrationMappings.CountAsync(m => m.ExternalType == "company"));
        Assert.Equal(2, await db.SyncRuns.CountAsync());
    }

    [Fact]
    public async Task Ninja_SyncAsync_Does_Not_Clobber_Asset_Name_When_AutoUpdateAssetNames_False()
    {
        var mcp = NinjaMcp();
        var (db, user, sync) = Create(mcp);
        var (_, connection) = await SeedNinjaCompactAsync(db, user);

        await sync.SyncAsync(connection.Id);

        var hippo = Assert.Single(await db.Assets.ToListAsync(), a => a.Name == "HIPPO");
        hippo.Name = "Reception PC";
        await db.SaveChangesAsync();

        var second = await sync.SyncAsync(connection.Id);

        Assert.Equal(SyncRunStatus.Succeeded, second.Status);
        Assert.Equal(4, await db.Assets.CountAsync());
        var renamed = await db.Assets.FirstAsync(a => a.Id == hippo.Id);
        Assert.Equal("Reception PC", renamed.Name);
        Assert.DoesNotContain(await db.Assets.ToListAsync(), a => a.Name == "HIPPO");
    }

    [Fact]
    public async Task Ninja_SyncAsync_Overwrites_Asset_Name_When_AutoUpdateAssetNames_True()
    {
        var mcp = NinjaMcp();
        var (db, user, sync) = Create(mcp);
        var (_, connection) = await SeedNinjaCompactAsync(db, user, autoUpdateAssetNames: true);

        await sync.SyncAsync(connection.Id);

        var hippo = Assert.Single(await db.Assets.ToListAsync(), a => a.Name == "HIPPO");
        hippo.Name = "Reception PC";
        await db.SaveChangesAsync();

        await sync.SyncAsync(connection.Id);

        Assert.Equal(4, await db.Assets.CountAsync());
        var restored = await db.Assets.FirstAsync(a => a.Id == hippo.Id);
        Assert.Equal("HIPPO", restored.Name);
    }

    [Fact]
    public async Task Ninja_SyncAsync_Reuses_An_Existing_Computer_Assets_Type()
    {
        var mcp = NinjaMcp();
        var (db, user, sync) = Create(mcp);
        var (_, connection) = await SeedNinjaCompactAsync(db, user);

        // Cased differently on purpose: the unique (TenantId, Name) index must not be tripped.
        var existing = new AssetType { TenantId = user.TenantId!.Value, Name = "computer assets" };
        db.AssetTypes.Add(existing);
        await db.SaveChangesAsync();

        await sync.SyncAsync(connection.Id);

        var assetType = Assert.Single(await db.AssetTypes.ToListAsync());
        Assert.Equal(existing.Id, assetType.Id);
        Assert.Equal("computer assets", assetType.Name);
        Assert.All(await db.Assets.ToListAsync(), a => Assert.Equal(existing.Id, a.AssetTypeId));
    }

    private static RecordingNinjaMcp NinjaMcp() => new()
    {
        OrganizationsJson = NinjaOrganizationMapperTests.LiveCompactListFixture,
        DevicesJson = LiveCompactDeviceListFixture,
    };

    private sealed class NoopAudit : IAuditService
    {
        public Task LogAsync(string action, string entityType, Guid? entityId = null, string? details = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>Serves both Ninja list tools off id-ordered arrays, honouring the after/pageSize cursor.</summary>
    private sealed class RecordingNinjaMcp : IMcpClient
    {
        public List<(Guid ServerId, string Tool, string? Args)> Calls { get; } = [];
        public string OrganizationsJson { get; init; } = "[]";
        public string DevicesJson { get; init; } = "[]";

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((mcpServerId, toolName, argumentsJson));
            int? after = null;
            var pageSize = NinjaDeviceMapper.DefaultPageSize;
            if (!string.IsNullOrWhiteSpace(argumentsJson))
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.TryGetProperty("after", out var a) && a.ValueKind == JsonValueKind.Number)
                    after = a.GetInt32();
                if (doc.RootElement.TryGetProperty("pageSize", out var s) && s.ValueKind == JsonValueKind.Number)
                    pageSize = s.GetInt32();
            }

            var source = toolName == NinjaDeviceMapper.ToolName ? DevicesJson : OrganizationsJson;
            var inner = SliceById(source, after, pageSize);
            var body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "1",
                result = new { content = new[] { new { type = "text", text = inner } } },
            });
            return Task.FromResult(body);
        }

        private static string SliceById(string json, int? after, int pageSize)
        {
            using var doc = JsonDocument.Parse(json);
            var items = new List<string>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var id = item.GetProperty("id").GetInt32();
                if (after is int afterId && id <= afterId)
                    continue;
                items.Add(item.GetRawText());
                if (items.Count >= pageSize)
                    break;
            }
            return "[" + string.Join(",", items) + "]";
        }
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

    private static async Task<(McpServer Server, IntegrationConnection Connection)> SeedNinjaCompactAsync(
        DocuEngAIneDbContext db,
        FakeCurrentUser user,
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
            Provider = IntegrationProvider.NinjaOne,
            DisplayName = "NinjaOne",
            McpServerId = server.Id,
            SkipAssets = skipAssets,
            AutoUpdateAssetNames = autoUpdateAssetNames,
        };
        db.IntegrationConnections.Add(connection);
        await db.SaveChangesAsync();
        return (server, connection);
    }
}
