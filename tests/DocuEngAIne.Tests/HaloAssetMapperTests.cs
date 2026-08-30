using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class HaloAssetMapperTests
{
    // Compact halo_list_assets wrapper. Field names match Halo Asset list passthrough
    // (id, inventory_number, name, client_id, inactive). Values are fixtures, not a live capture.
    public const string CompactListFixture = """
        {
          "page_no": 1,
          "page_size": 2,
          "record_count": 54,
          "assets": [
            {
              "id": 101,
              "inventory_number": "LAP-001",
              "name": "Hippo Laptop",
              "client_id": 12,
              "inactive": false
            },
            {
              "id": 202,
              "inventory_number": "DSK-009",
              "name": "Spare Desktop",
              "client_id": 29,
              "inactive": true
            }
          ]
        }
        """;

    // Two raw rows (one empty name), full page. Mapped count is 1; raw count is 2.
    public const string NextPageContinuesFixture = """
        {
          "page_no": 1,
          "page_size": 2,
          "record_count": 3,
          "assets": [
            {
              "id": 101,
              "inventory_number": "LAP-001",
              "name": "Hippo Laptop",
              "client_id": 12,
              "inactive": false
            },
            {
              "id": 99,
              "inventory_number": "",
              "name": "",
              "client_id": 12,
              "inactive": false
            }
          ]
        }
        """;

    public const string EmptyAssetsFixture = """
        {
          "page_no": 2,
          "page_size": 2,
          "record_count": 54,
          "assets": []
        }
        """;

    [Fact]
    public void MapAssets_CompactList_MapsIdInventoryNumberClientAndInactive()
    {
        var devices = HaloAssetMapper.MapAssets(CompactListFixture, out var rowCount);

        Assert.Equal(2, rowCount);
        Assert.Equal(2, devices.Count);
        Assert.All(devices, d => Assert.IsAssignableFrom<ExternalDeviceDto>(d));

        var laptop = devices[0];
        Assert.Equal("101", laptop.ExternalId);
        Assert.Equal("12", laptop.OrganizationExternalId);
        Assert.Equal("LAP-001", laptop.Name);
        Assert.False(laptop.IsInactive);
        Assert.Null(laptop.NodeClass);
        Assert.Null(laptop.SystemName);
        Assert.Null(laptop.DnsName);

        var desktop = devices[1];
        Assert.Equal("202", desktop.ExternalId);
        Assert.Equal("29", desktop.OrganizationExternalId);
        Assert.Equal("DSK-009", desktop.Name);
        Assert.True(desktop.IsInactive);
    }

    [Fact]
    public void MapAssets_EmptyInventoryNumber_FallsBackToName()
    {
        const string json = """
            {
              "record_count": 1,
              "assets": [
                {
                  "id": 101,
                  "inventory_number": "",
                  "name": "Hippo Laptop",
                  "client_id": 12,
                  "inactive": false
                }
              ]
            }
            """;

        var laptop = Assert.Single(HaloAssetMapper.MapAssets(json));
        Assert.Equal("101", laptop.ExternalId);
        Assert.Equal("Hippo Laptop", laptop.Name);
        Assert.Equal("12", laptop.OrganizationExternalId);
    }

    [Fact]
    public void MapAssets_PrefersInventoryNumberOverName()
    {
        var laptop = Assert.Single(
            HaloAssetMapper.MapAssets(CompactListFixture).Where(d => d.ExternalId == "101"));
        Assert.Equal("LAP-001", laptop.Name);
        Assert.NotEqual("Hippo Laptop", laptop.Name);
    }

    [Fact]
    public void MapAssets_MissingInactive_IsNull()
    {
        const string json = """
            {
              "assets": [
                {
                  "id": 101,
                  "inventory_number": "LAP-001",
                  "name": "Hippo Laptop",
                  "client_id": 12
                }
              ]
            }
            """;

        var laptop = Assert.Single(HaloAssetMapper.MapAssets(json));
        Assert.Null(laptop.IsInactive);
    }

    [Fact]
    public void MapAssets_Skips_Missing_Id_Or_Name()
    {
        const string json = """
            {
              "assets": [
                { "id": "", "inventory_number": "NO-ID", "name": "No Id", "client_id": 12, "inactive": false },
                { "id": 99, "inventory_number": "", "name": "", "client_id": 12, "inactive": false },
                { "inventory_number": "NO-ID-KEY", "name": "No Id Key", "client_id": 12, "inactive": false },
                { "id": 100, "inventory_number": "NO-CLIENT", "name": "No Client", "inactive": false },
                { "id": 101, "inventory_number": "LAP-001", "name": "Hippo Laptop", "client_id": 12, "inactive": false }
              ]
            }
            """;

        var laptop = Assert.Single(HaloAssetMapper.MapAssets(json, out var rowCount));
        Assert.Equal(5, rowCount);
        Assert.Equal("101", laptop.ExternalId);
        Assert.Equal("LAP-001", laptop.Name);
        Assert.DoesNotContain(HaloAssetMapper.MapAssets(json), d => d.ExternalId == "99");
        Assert.DoesNotContain(HaloAssetMapper.MapAssets(json), d => d.ExternalId == "100");
    }

    [Fact]
    public void MapAssets_RecordCount_IsNotRowCount()
    {
        HaloAssetMapper.MapAssets(CompactListFixture, out var rowCount);
        Assert.Equal(2, rowCount);
        Assert.NotEqual(54, rowCount);
    }

    [Fact]
    public void MapAssets_JsonRpcContentText_UnwrapsToAssets()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = CompactListFixture } } },
        });

        var devices = HaloAssetMapper.MapAssets(wrapped);
        Assert.Equal(2, devices.Count);
        Assert.Equal("101", devices[0].ExternalId);
        Assert.Equal("LAP-001", devices[0].Name);
        Assert.Equal("12", devices[0].OrganizationExternalId);
        Assert.False(devices[0].IsInactive);
    }

    [Fact]
    public void MapAssets_ToolError_Throws()
    {
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            error = new { code = -32000, message = "halo auth expired" },
        });

        var ex = Assert.Throws<InvalidOperationException>(() => { HaloAssetMapper.MapAssets(body); });
        Assert.Contains("halo auth expired", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_FirstPage_OmitsClientId_AndActiveInactive()
    {
        var args = HaloAssetMapper.BuildArgumentsJson(pageNo: 1);
        Assert.Contains("\"pageNo\":1", args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":50", args, StringComparison.Ordinal);
        Assert.DoesNotContain("clientId", args, StringComparison.Ordinal);
        Assert.DoesNotContain("activeInactive", args, StringComparison.Ordinal);
        Assert.DoesNotContain("includeInactive", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_ClampsPageSizeToMax200_AndPageNoTo1()
    {
        var args = HaloAssetMapper.BuildArgumentsJson(pageNo: 0, pageSize: 20000, clientId: 12);
        Assert.Contains("\"pageNo\":1", args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":200", args, StringComparison.Ordinal);
        Assert.Contains("\"clientId\":12", args, StringComparison.Ordinal);
        Assert.DoesNotContain("activeInactive", args, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_EmptyPage_StopsPaging()
    {
        var mcp = new ScriptedMcp([CompactListFixture, EmptyAssetsFixture]);
        var devices = await HaloAssetMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 2);

        Assert.Equal(2, devices.Count);
        Assert.Equal("101", devices[0].ExternalId);
        Assert.Equal("LAP-001", devices[0].Name);
        Assert.Equal(2, mcp.Calls.Count);
        Assert.All(mcp.Calls, c => Assert.Equal(HaloAssetMapper.ToolName, c.Tool));
        Assert.Contains("\"pageNo\":1", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":2", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageNo\":2", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("halo_get_asset", mcp.Calls.Select(c => c.Tool));
        Assert.DoesNotContain("halo_search_assets", mcp.Calls.Select(c => c.Tool));
        Assert.DoesNotContain("activeInactive", mcp.Calls[0].Args, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_ContinuesOnRawCount_NotMappedCount()
    {
        var mcp = new ScriptedMcp([NextPageContinuesFixture, EmptyAssetsFixture]);
        var devices = await HaloAssetMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 2, clientId: 12);

        var laptop = Assert.Single(devices);
        Assert.Equal("LAP-001", laptop.Name);
        Assert.Equal("101", laptop.ExternalId);
        Assert.Equal(2, mcp.Calls.Count);
        Assert.All(mcp.Calls, c => Assert.Equal(HaloAssetMapper.ToolName, c.Tool));
        Assert.Contains("\"pageNo\":1", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":2", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"clientId\":12", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageNo\":2", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.Contains("\"clientId\":12", mcp.Calls[1].Args, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_ShortPage_StopsWithoutSecondCall()
    {
        var mcp = new ScriptedMcp([CompactListFixture, EmptyAssetsFixture]);
        var devices = await HaloAssetMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 50);

        Assert.Equal(2, devices.Count);
        Assert.Single(mcp.Calls);
        Assert.Contains("\"pageSize\":50", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageNo\":1", mcp.Calls[0].Args, StringComparison.Ordinal);
    }

    private sealed class ScriptedMcp : IMcpClient
    {
        private readonly Queue<string> _pages;
        public List<(string Tool, string? Args)> Calls { get; } = [];

        public ScriptedMcp(IEnumerable<string> pages) => _pages = new Queue<string>(pages);

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((toolName, argumentsJson));
            var inner = _pages.Count > 0 ? _pages.Dequeue() : EmptyAssetsFixture;
            var body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "1",
                result = new { content = new[] { new { type = "text", text = inner } } },
            });
            return Task.FromResult(body);
        }
    }
}
