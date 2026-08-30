using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class Action1EndpointMapperTests
{
    public const string AdrocOrgId = "4702a030-5f67-11f0-9cb3-e3f0bda36034";

    // Hand-built Compact action1_list_endpoints ResultPage (no live vendor call).
    // Envelope is {items, next?} like Action1 Organization ResultPage. Field names: id (GUID),
    // hostname (Action1 also uses name), OS, status Active/Inactive.
    public const string CompactListFixture = """
        {"id":"1","type":"ResultPage","items":[{"id":"11111111-1111-1111-1111-111111111111","type":"Endpoint","hostname":"WKS-ADROC-01","OS":"Windows 11 Pro","status":"Active"},{"id":"22222222-2222-2222-2222-222222222222","type":"Endpoint","hostname":"WKS-ADROC-02","OS":"Windows 10 Pro","status":"Inactive"},{"id":"33333333-3333-3333-3333-333333333333","type":"Endpoint","name":"MAC-ADROC-01","OS":"macOS 14","status":"Active"}],"total_items":"3","limit":"50","next":null}
        """;

    // Mapping fixture: one good row, one missing id, one missing hostname, one empty hostname.
    public const string DegenerateFixture = """
        {"items":[{"id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","hostname":"OK-01","OS":"Windows 11"},{"hostname":"NO-ID","OS":"Windows 10"},{"id":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","OS":"Windows 10"},{"id":"cccccccc-cccc-cccc-cccc-cccccccccccc","hostname":"","OS":"Linux"}],"next":null}
        """;

    public const string EmptyItemsFixture = """
        {"id":"1","type":"ResultPage","items":[],"total_items":"3","limit":"2","next":null}
        """;

    // Six raw rows so a pageSize of 3 must request a second page. Row 2 has no hostname and is dropped.
    public const string PagingFixture = """
        {"items":[{"id":"00000000-0000-0000-0000-000000000001","hostname":"ep-one","OS":"Windows 11"},{"id":"00000000-0000-0000-0000-000000000002","OS":"Windows 10"},{"id":"00000000-0000-0000-0000-000000000003","hostname":"ep-three","OS":"Windows 11"},{"id":"00000000-0000-0000-0000-000000000004","hostname":"ep-four","OS":"Windows 10"},{"id":"00000000-0000-0000-0000-000000000005","hostname":"ep-five","OS":"Linux"},{"id":"00000000-0000-0000-0000-000000000006","hostname":"ep-six","OS":"macOS"}]}
        """;

    [Fact]
    public void MapEndpoints_CompactList_MapsIdHostnameOsAndOrgId_IncludesInactive()
    {
        var devices = Action1EndpointMapper.MapEndpoints(CompactListFixture, AdrocOrgId, out var rowCount);

        Assert.Equal(3, rowCount);
        Assert.Equal(3, devices.Count);

        var first = devices[0];
        Assert.Equal("11111111-1111-1111-1111-111111111111", first.ExternalId);
        Assert.Equal(AdrocOrgId, first.OrganizationExternalId);
        Assert.Equal("WKS-ADROC-01", first.Name);
        Assert.Equal("Windows 11 Pro", first.NodeClass);

        var inactive = Assert.Single(devices, d => d.ExternalId == "22222222-2222-2222-2222-222222222222");
        Assert.Equal("WKS-ADROC-02", inactive.Name);
        Assert.Equal("Windows 10 Pro", inactive.NodeClass);
        Assert.Equal(AdrocOrgId, inactive.OrganizationExternalId);

        var named = Assert.Single(devices, d => d.ExternalId == "33333333-3333-3333-3333-333333333333");
        Assert.Equal("MAC-ADROC-01", named.Name);
        Assert.Equal("macOS 14", named.NodeClass);
    }

    [Fact]
    public void MapEndpoints_DropsRowsWithoutIdOrHostname()
    {
        var devices = Action1EndpointMapper.MapEndpoints(DegenerateFixture, AdrocOrgId, out var rowCount);

        Assert.Equal(4, rowCount);
        var only = Assert.Single(devices);
        Assert.Equal("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", only.ExternalId);
        Assert.Equal("OK-01", only.Name);
        Assert.Equal("Windows 11", only.NodeClass);
        Assert.Equal(AdrocOrgId, only.OrganizationExternalId);
    }

    [Fact]
    public void MapEndpoints_JsonRpcContentText_UnwrapsToResultPage()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = CompactListFixture } } },
        });

        var devices = Action1EndpointMapper.MapEndpoints(wrapped, AdrocOrgId);
        Assert.Equal(3, devices.Count);
        Assert.Equal("11111111-1111-1111-1111-111111111111", devices[0].ExternalId);
        Assert.Equal("WKS-ADROC-01", devices[0].Name);
        Assert.Equal(AdrocOrgId, devices[0].OrganizationExternalId);
    }

    [Fact]
    public void MapEndpoints_ToolError_Throws()
    {
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            error = new { code = -32000, message = "action1 auth expired" },
        });

        var ex = Assert.Throws<InvalidOperationException>(() => Action1EndpointMapper.MapEndpoints(body, AdrocOrgId));
        Assert.Contains("action1 auth expired", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_FirstPage_RequiresOrgId_OmitsFromAndStatus()
    {
        var args = Action1EndpointMapper.BuildArgumentsJson(AdrocOrgId, from: null);
        Assert.Contains($"\"orgId\":\"{AdrocOrgId}\"", args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":50", args, StringComparison.Ordinal);
        Assert.DoesNotContain("from", args, StringComparison.Ordinal);
        Assert.DoesNotContain("status", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_ClampsPageSizeToMax100()
    {
        var args = Action1EndpointMapper.BuildArgumentsJson(AdrocOrgId, from: null, pageSize: 200);
        Assert.Contains("\"pageSize\":100", args, StringComparison.Ordinal);
        Assert.Contains($"\"orgId\":\"{AdrocOrgId}\"", args, StringComparison.Ordinal);
        Assert.DoesNotContain("status", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_SubsequentPage_IncludesFromOffset()
    {
        var args = Action1EndpointMapper.BuildArgumentsJson(AdrocOrgId, from: 50, pageSize: 50);
        Assert.Contains("\"from\":50", args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":50", args, StringComparison.Ordinal);
        Assert.Contains($"\"orgId\":\"{AdrocOrgId}\"", args, StringComparison.Ordinal);
        Assert.DoesNotContain("status", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_MissingOrgId_Throws()
    {
        Assert.Throws<ArgumentException>(() => Action1EndpointMapper.BuildArgumentsJson("", from: null));
    }

    [Fact]
    public async Task PullAsync_EmptyItems_StopsPaging()
    {
        var mcp = new ScriptedMcp([EmptyItemsFixture]);
        var serverId = Guid.NewGuid();

        var devices = await Action1EndpointMapper.PullAsync(mcp, serverId, AdrocOrgId, pageSize: 50);

        Assert.Empty(devices);
        Assert.Single(mcp.Calls);
        Assert.Equal(Action1EndpointMapper.ToolName, mcp.Calls[0].Tool);
        Assert.Equal(serverId, mcp.Calls[0].ServerId);
        Assert.Contains($"\"orgId\":\"{AdrocOrgId}\"", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("from", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("status", mcp.Calls[0].Args, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_ShortPage_StopsWithoutSecondCall()
    {
        var mcp = new ScriptedMcp([CompactListFixture]);

        var devices = await Action1EndpointMapper.PullAsync(mcp, Guid.NewGuid(), AdrocOrgId, pageSize: 50);

        Assert.Equal(3, devices.Count);
        Assert.Single(mcp.Calls);
        Assert.Equal(Action1EndpointMapper.ToolName, mcp.Calls[0].Tool);
        Assert.DoesNotContain("from", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("status", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.All(devices, d => Assert.Equal(AdrocOrgId, d.OrganizationExternalId));
    }

    [Fact]
    public async Task PullAsync_KeepsPaging_When_A_Full_Page_Contains_A_Dropped_Row()
    {
        var mcp = new RecordingOffsetMcp { EndpointsJson = PagingFixture };
        var serverId = Guid.NewGuid();

        var pulled = await Action1EndpointMapper.PullAsync(mcp, serverId, AdrocOrgId, pageSize: 3);

        // Five of six map; the third call is the empty page that terminates the loop.
        Assert.Equal(5, pulled.Count);
        Assert.Equal(3, mcp.Calls.Count);
        Assert.All(mcp.Calls, c => Assert.Equal(Action1EndpointMapper.ToolName, c.Tool));
        Assert.All(mcp.Calls, c => Assert.Equal(serverId, c.ServerId));
        Assert.All(mcp.Calls, c => Assert.Contains($"\"orgId\":\"{AdrocOrgId}\"", c.Args, StringComparison.Ordinal));
        Assert.All(mcp.Calls, c => Assert.DoesNotContain("status", c.Args, StringComparison.Ordinal));
        Assert.DoesNotContain("from", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"from\":3", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.Contains("\"from\":6", mcp.Calls[2].Args, StringComparison.Ordinal);
        Assert.DoesNotContain(pulled, d => d.ExternalId == "00000000-0000-0000-0000-000000000002");
        Assert.Contains(pulled, d => d.ExternalId == "00000000-0000-0000-0000-000000000006");
        Assert.All(pulled, d => Assert.Equal(AdrocOrgId, d.OrganizationExternalId));
    }

    [Fact]
    public async Task PullAsync_MissingOrgId_Throws()
    {
        var mcp = new ScriptedMcp([CompactListFixture]);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Action1EndpointMapper.PullAsync(mcp, Guid.NewGuid(), ""));
        Assert.Empty(mcp.Calls);
    }

    private sealed class ScriptedMcp : IMcpClient
    {
        private readonly Queue<string> _pages;
        public List<(Guid ServerId, string Tool, string? Args)> Calls { get; } = [];

        public ScriptedMcp(IEnumerable<string> pages) => _pages = new Queue<string>(pages);

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((mcpServerId, toolName, argumentsJson));
            var inner = _pages.Count > 0 ? _pages.Dequeue() : EmptyItemsFixture;
            var body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "1",
                result = new { content = new[] { new { type = "text", text = inner } } },
            });
            return Task.FromResult(body);
        }
    }

    /// <summary>Serves action1_list_endpoints off a ResultPage items array, honouring from/pageSize.</summary>
    private sealed class RecordingOffsetMcp : IMcpClient
    {
        public List<(Guid ServerId, string Tool, string? Args)> Calls { get; } = [];
        public string EndpointsJson { get; init; } = """{"items":[]}""";

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((mcpServerId, toolName, argumentsJson));
            var from = 0;
            var pageSize = Action1EndpointMapper.DefaultPageSize;
            if (!string.IsNullOrWhiteSpace(argumentsJson))
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.Number)
                    from = f.GetInt32();
                if (doc.RootElement.TryGetProperty("pageSize", out var s) && s.ValueKind == JsonValueKind.Number)
                    pageSize = s.GetInt32();
            }

            var inner = SliceItems(EndpointsJson, from, pageSize);
            var body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "1",
                result = new { content = new[] { new { type = "text", text = inner } } },
            });
            return Task.FromResult(body);
        }

        private static string SliceItems(string json, int from, int pageSize)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return """{"items":[]}""";

            var sliced = new List<string>();
            var index = 0;
            foreach (var item in items.EnumerateArray())
            {
                if (index++ < from)
                    continue;
                sliced.Add(item.GetRawText());
                if (sliced.Count >= pageSize)
                    break;
            }

            return "{\"items\":[" + string.Join(",", sliced) + "]}";
        }
    }
}
