using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class UnifiSiteMapperTests
{
    // Compact unifi_sm_list_sites wrapper (account-wide Site Manager sites, NOT unifi_net_list_sites).
    // UniFi Network sites are often named default — still map them. statistics is present and must be dropped.
    public const string LiveCompactListFixture = """
        {"data":[{"siteId":"site-1","hostId":"host-1","meta":{"name":"default","description":"Adroc Capital: 1425 RXR Plaza","timezone":"America/New_York"},"statistics":{"counts":{"gateway":1,"wifiClient":12,"wiredClient":8,"offlineDevice":0},"isp":{"wan1":{"uptime":99.9,"latencyMs":12,"bytesRx":1048576,"bytesTx":524288}}}},{"siteId":"site-2","hostId":"host-2","meta":{"name":"Warehouse","timezone":"America/Chicago"}}],"nextToken":"tok-2","httpStatusCode":200}
        """;

    [Fact]
    public void ToolName_IsAccountWideSiteManagerList_NotNetworkSites()
    {
        Assert.Equal("unifi_sm_list_sites", UnifiSiteMapper.ToolName);
        Assert.NotEqual("unifi_net_list_sites", UnifiSiteMapper.ToolName);
    }

    [Fact]
    public void MapSites_LiveCompactList_MapsDefaultNamedSite_AndNamedSite_DropsStatistics()
    {
        var sites = UnifiSiteMapper.MapSites(LiveCompactListFixture, out var nextToken, out var dataCount);

        Assert.Equal(2, sites.Count);
        Assert.Equal(2, dataCount);
        Assert.Equal("tok-2", nextToken);

        var defaultSite = sites[0];
        Assert.Equal("site-1", defaultSite.ExternalId);
        Assert.Equal("host-1", defaultSite.HostExternalId);
        Assert.Equal("default", defaultSite.Name);
        Assert.Equal("America/New_York", defaultSite.Timezone);

        var warehouse = sites[1];
        Assert.Equal("site-2", warehouse.ExternalId);
        Assert.Equal("host-2", warehouse.HostExternalId);
        Assert.Equal("Warehouse", warehouse.Name);
        Assert.Equal("America/Chicago", warehouse.Timezone);

        var json = JsonSerializer.Serialize(sites);
        Assert.DoesNotContain("statistics", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wifiClient", json, StringComparison.Ordinal);
        Assert.DoesNotContain("bytesRx", json, StringComparison.Ordinal);
        Assert.DoesNotContain("description", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MapSites_JsonRpcContentTextWrapper_UnwrapsToSiteList()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = LiveCompactListFixture } } },
        });

        var sites = UnifiSiteMapper.MapSites(wrapped);
        Assert.Equal(2, sites.Count);
        Assert.Equal("site-1", sites[0].ExternalId);
        Assert.Equal("default", sites[0].Name);
        Assert.Equal("host-1", sites[0].HostExternalId);
        Assert.Equal("America/New_York", sites[0].Timezone);
    }

    [Fact]
    public void MapSites_StillMaps_WhenMetaNameIsDefault()
    {
        const string json = """
            {"data":[{"siteId":"site-default","hostId":"host-1","meta":{"name":"default","timezone":"UTC"}}]}
            """;

        var site = Assert.Single(UnifiSiteMapper.MapSites(json));
        Assert.Equal("site-default", site.ExternalId);
        Assert.Equal("default", site.Name);
        Assert.Equal("host-1", site.HostExternalId);
        Assert.Equal("UTC", site.Timezone);
    }

    [Fact]
    public void MapSites_FallsBackToDefault_WhenMetaNameMissing()
    {
        const string json = """
            {"data":[{"siteId":"site-3","hostId":"host-3","meta":{"timezone":"America/Denver"}}]}
            """;

        var site = Assert.Single(UnifiSiteMapper.MapSites(json));
        Assert.Equal("site-3", site.ExternalId);
        Assert.Equal("host-3", site.HostExternalId);
        Assert.Equal("default", site.Name);
        Assert.Equal("America/Denver", site.Timezone);
    }

    [Fact]
    public void MapSites_SkipsMissingSiteId_CountsRawRowsForPaging()
    {
        const string json = """
            {"data":[{"hostId":"host-x","meta":{"name":"Orphan"}},{"siteId":"site-ok","hostId":"host-ok","meta":{"name":"Office","timezone":"America/Los_Angeles"}}],"nextToken":"tok-next"}
            """;

        var sites = UnifiSiteMapper.MapSites(json, out var nextToken, out var dataCount);

        var only = Assert.Single(sites);
        Assert.Equal("site-ok", only.ExternalId);
        Assert.Equal("host-ok", only.HostExternalId);
        Assert.Equal("Office", only.Name);
        Assert.Equal(2, dataCount);
        Assert.Equal("tok-next", nextToken);
    }

    [Fact]
    public void MapSites_ToolError_Throws()
    {
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            error = new { code = -32000, message = "unifi auth expired" },
        });

        var ex = Assert.Throws<InvalidOperationException>(() => { UnifiSiteMapper.MapSites(body); });
        Assert.Contains("unifi auth expired", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_OmitsNextTokenOnFirstPage()
    {
        var args = UnifiSiteMapper.BuildArgumentsJson(nextToken: null);
        Assert.Contains("\"pageSize\":200", args, StringComparison.Ordinal);
        Assert.DoesNotContain("nextToken", args, StringComparison.Ordinal);
    }

    [Fact]
    public void MapSites_ReadsNextTokenFromWrapper()
    {
        UnifiSiteMapper.MapSites(LiveCompactListFixture, out var nextToken);
        Assert.Equal("tok-2", nextToken);
        var next = UnifiSiteMapper.BuildArgumentsJson(nextToken: nextToken);
        Assert.Contains("\"nextToken\":\"tok-2\"", next, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":200", next, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_ClampsPageSizeToMax200()
    {
        var args = UnifiSiteMapper.BuildArgumentsJson(nextToken: null, pageSize: 20000);
        Assert.Contains("\"pageSize\":200", args, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_Calls_UnifiSmListSites_NotNetworkSites()
    {
        var mcp = new RecordingUnifiSiteMcp { SitesJson = LiveCompactListFixture };
        var serverId = Guid.NewGuid();

        var sites = await UnifiSiteMapper.PullAsync(mcp, serverId);

        Assert.Equal(2, sites.Count);
        Assert.Equal("default", sites[0].Name);
        var call = Assert.Single(mcp.Calls);
        Assert.Equal("unifi_sm_list_sites", call.Tool);
        Assert.Equal(serverId, call.ServerId);
        Assert.DoesNotContain("unifi_net_list_sites", mcp.Calls.Select(c => c.Tool));
        Assert.DoesNotContain("nextToken", call.Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":200", call.Args, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_SecondCall_Receives_NextToken_From_First_Page()
    {
        var mcp = new RecordingUnifiSiteMcp { SitesJson = LiveCompactListFixture };
        var serverId = Guid.NewGuid();

        var sites = await UnifiSiteMapper.PullAsync(mcp, serverId, pageSize: 1);

        Assert.Equal(2, sites.Count);
        Assert.Equal(2, mcp.Calls.Count);
        Assert.All(mcp.Calls, c => Assert.Equal("unifi_sm_list_sites", c.Tool));
        Assert.DoesNotContain("unifi_net_list_sites", mcp.Calls.Select(c => c.Tool));
        Assert.DoesNotContain("nextToken", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":1", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"nextToken\":\"site-2\"", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":1", mcp.Calls[1].Args, StringComparison.Ordinal);
    }

    private sealed class RecordingUnifiSiteMcp : IMcpClient
    {
        public List<(Guid ServerId, string Tool, string? Args)> Calls { get; } = [];
        public string SitesJson { get; init; } = """{"data":[]}""";

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((mcpServerId, toolName, argumentsJson));
            string? nextToken = null;
            var pageSize = UnifiSiteMapper.DefaultPageSize;
            if (!string.IsNullOrWhiteSpace(argumentsJson))
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.TryGetProperty("nextToken", out var t) && t.ValueKind == JsonValueKind.String)
                    nextToken = t.GetString();
                if (doc.RootElement.TryGetProperty("pageSize", out var s) && s.ValueKind == JsonValueKind.Number)
                    pageSize = s.GetInt32();
            }

            var inner = SliceSitesJson(SitesJson, nextToken, pageSize);
            var body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "1",
                result = new { content = new[] { new { type = "text", text = inner } } },
            });
            return Task.FromResult(body);
        }

        private static string SliceSitesJson(string json, string? nextToken, int pageSize)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return """{"data":[]}""";

            var all = data.EnumerateArray().ToList();
            var start = 0;
            if (nextToken is not null)
            {
                start = all.FindIndex(s =>
                    s.ValueKind == JsonValueKind.Object
                    && s.TryGetProperty("siteId", out var id)
                    && id.GetString() == nextToken);
                if (start < 0)
                    start = all.Count;
            }

            var page = all.Skip(start).Take(pageSize).ToList();
            var dataJson = "[" + string.Join(",", page.Select(p => p.GetRawText())) + "]";
            if (start + page.Count < all.Count)
            {
                var outgoing = all[start + page.Count].GetProperty("siteId").GetString();
                return $$"""{"data":{{dataJson}},"nextToken":"{{outgoing}}","httpStatusCode":200}""";
            }

            return $$"""{"data":{{dataJson}},"httpStatusCode":200}""";
        }
    }
}
