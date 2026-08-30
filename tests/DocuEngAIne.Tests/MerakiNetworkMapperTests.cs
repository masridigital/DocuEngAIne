using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class MerakiNetworkMapperTests
{
    // Dashboard API / Compact meraki_get_organization_networks field names (id, name, organizationId,
    // productTypes, tags). Hand-built fixture — no live Compact capture. Organization 1279651 is
    // "7 Compression" in MerakiOrganizationMapperTests.LiveCompactListFixture.
    public const string CompactNetworkListFixture = """
        [{"id":"N_24329156","organizationId":"1279651","name":"Main Office","productTypes":["appliance","switch","wireless"],"timeZone":"America/Los_Angeles","tags":["tag1","tag2"],"enrollmentString":"my-enrollment-string","url":"https://n1.meraki.com//n//manage/nodes/list","notes":"Additional description of the network","isBoundToConfigTemplate":false},{"id":"L_646829496481105433","organizationId":"1279651","name":"Long Island Office","productTypes":["appliance","switch"],"timeZone":"America/New_York","tags":["hq"],"url":"https://n565.dashboard.meraki.com/Long-Island-Offi/n/xxxx/manage/usage/list","notes":"","isBoundToConfigTemplate":false}]
        """;

    // Hand-built, not captured: missing id, missing name, missing organizationId, then one valid row.
    private const string DegenerateNetworkListFixture = """
        [{"name":"NO-ID","organizationId":"1279651","productTypes":["switch"]},{"id":"N_MISSING_NAME","organizationId":"1279651"},{"id":"N_NO_ORG","name":"No Org"},{"id":"N_OK","organizationId":"1279651","name":"OK Network","productTypes":["wireless"],"tags":[]}]
        """;

    [Fact]
    public void MapNetworks_Fixture_MapsIdNameOrgProductTypesAndTags()
    {
        var networks = MerakiNetworkMapper.MapNetworks(CompactNetworkListFixture);

        Assert.Equal(2, networks.Count);

        var main = networks[0];
        Assert.Equal("N_24329156", main.ExternalId);
        Assert.Equal("1279651", main.OrganizationExternalId);
        Assert.Equal("Main Office", main.Name);
        Assert.Equal(["appliance", "switch", "wireless"], main.ProductTypes);
        Assert.Equal(["tag1", "tag2"], main.Tags);

        var island = networks[1];
        Assert.Equal("L_646829496481105433", island.ExternalId);
        Assert.Equal("1279651", island.OrganizationExternalId);
        Assert.Equal("Long Island Office", island.Name);
        Assert.Equal(["appliance", "switch"], island.ProductTypes);
        Assert.Equal(["hq"], island.Tags);
    }

    [Fact]
    public void MapNetworks_JsonRpcContentTextArray_UnwrapsToNetworkList()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = CompactNetworkListFixture } } },
        });

        var networks = MerakiNetworkMapper.MapNetworks(wrapped);
        Assert.Equal(2, networks.Count);
        Assert.Equal("N_24329156", networks[0].ExternalId);
        Assert.Equal("Main Office", networks[0].Name);
        Assert.Equal("1279651", networks[0].OrganizationExternalId);
        Assert.Equal(["appliance", "switch", "wireless"], networks[0].ProductTypes);
    }

    [Fact]
    public void MapNetworks_SkipsMissingIdOrName()
    {
        var networks = MerakiNetworkMapper.MapNetworks(DegenerateNetworkListFixture, out var lastId, out var rowCount);

        Assert.Equal(4, rowCount);
        var only = Assert.Single(networks);
        Assert.Equal("N_OK", only.ExternalId);
        Assert.Equal("1279651", only.OrganizationExternalId);
        Assert.Equal("OK Network", only.Name);
        Assert.Equal(["wireless"], only.ProductTypes);
        Assert.Empty(only.Tags!);

        // Cursor still advances past dropped rows (including the no-org row that has an id).
        Assert.Equal("N_OK", lastId);
    }

    [Fact]
    public void MapNetworks_UsesFallbackOrganizationId_WhenRowOmitsIt()
    {
        const string json = """[{"id":"N_NO_ORG","name":"No Org Row"}]""";

        var mapped = MerakiNetworkMapper.MapNetworks(json, organizationId: "1279651", out _, out _);
        var only = Assert.Single(mapped);
        Assert.Equal("N_NO_ORG", only.ExternalId);
        Assert.Equal("1279651", only.OrganizationExternalId);
        Assert.Equal("No Org Row", only.Name);
    }

    [Fact]
    public void MapNetworks_ToolError_Throws()
    {
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            error = new { code = -32000, message = "meraki auth expired" },
        });

        var ex = Assert.Throws<InvalidOperationException>(() => { MerakiNetworkMapper.MapNetworks(body); });
        Assert.Contains("meraki auth expired", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_RequiresOrganizationId()
    {
        Assert.Throws<ArgumentException>(() => MerakiNetworkMapper.BuildArgumentsJson(organizationId: "", startingAfter: null));
        Assert.Throws<ArgumentException>(() => MerakiNetworkMapper.BuildArgumentsJson(organizationId: "   ", startingAfter: null));
    }

    [Fact]
    public void BuildArgumentsJson_OmitsStartingAfterOnFirstPage()
    {
        var args = MerakiNetworkMapper.BuildArgumentsJson(organizationId: "1279651", startingAfter: null);
        Assert.Contains("\"organizationId\":\"1279651\"", args, StringComparison.Ordinal);
        Assert.Contains("\"perPage\":1000", args, StringComparison.Ordinal);
        Assert.DoesNotContain("startingAfter", args, StringComparison.Ordinal);
    }

    [Fact]
    public void MapNetworks_Fixture_LastNetworkIdIsL646829496481105433()
    {
        MerakiNetworkMapper.MapNetworks(CompactNetworkListFixture, out var lastId);
        Assert.Equal("L_646829496481105433", lastId);
        var next = MerakiNetworkMapper.BuildArgumentsJson(organizationId: "1279651", startingAfter: lastId);
        Assert.Contains("\"startingAfter\":\"L_646829496481105433\"", next, StringComparison.Ordinal);
        Assert.Contains("\"perPage\":1000", next, StringComparison.Ordinal);
        Assert.Contains("\"organizationId\":\"1279651\"", next, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_ClampsPageSizeToMin3AndMax1000()
    {
        var low = MerakiNetworkMapper.BuildArgumentsJson(organizationId: "1279651", startingAfter: null, pageSize: 1);
        Assert.Contains("\"perPage\":3", low, StringComparison.Ordinal);

        var high = MerakiNetworkMapper.BuildArgumentsJson(organizationId: "1279651", startingAfter: null, pageSize: 20000);
        Assert.Contains("\"perPage\":1000", high, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_SecondCall_Receives_StartingAfter_From_First_Page()
    {
        var mcp = new RecordingMerakiNetworkMcp { NetworksJson = CompactNetworkListFixture };
        var serverId = Guid.NewGuid();

        var networks = await MerakiNetworkMapper.PullAsync(mcp, serverId, organizationId: "1279651", pageSize: 3);

        Assert.Equal(2, networks.Count);
        var first = Assert.Single(mcp.Calls);
        Assert.Equal(MerakiNetworkMapper.ToolName, first.Tool);
        Assert.Equal(serverId, first.ServerId);
        Assert.Contains("\"organizationId\":\"1279651\"", first.Args, StringComparison.Ordinal);
        Assert.Contains("\"perPage\":3", first.Args, StringComparison.Ordinal);
        Assert.DoesNotContain("startingAfter", first.Args, StringComparison.Ordinal);
    }

    /// <summary>
    /// A full page containing one droppable row must not end the pull. Paging is decided on the rows
    /// the vendor returned, not the rows that mapped.
    /// </summary>
    [Fact]
    public async Task PullAsync_KeepsPaging_When_A_Full_Page_Contains_A_Dropped_Row()
    {
        const string networks = """
            [{"id":"N_1","organizationId":"1279651","name":"net-one"},
             {"id":"N_2","organizationId":"1279651"},
             {"id":"N_3","organizationId":"1279651","name":"net-three"},
             {"id":"N_4","organizationId":"1279651","name":"net-four"},
             {"id":"N_5","organizationId":"1279651","name":"net-five"},
             {"id":"N_6","organizationId":"1279651","name":"net-six"}]
            """;

        var mcp = new RecordingMerakiNetworkMcp { NetworksJson = networks };

        var pulled = await MerakiNetworkMapper.PullAsync(mcp, Guid.NewGuid(), organizationId: "1279651", pageSize: 3);

        Assert.Equal(5, pulled.Count);
        Assert.Equal(3, mcp.Calls.Count);
        Assert.DoesNotContain(pulled, n => n.ExternalId == "N_2");
        Assert.Contains(pulled, n => n.ExternalId == "N_6");
        Assert.Contains("\"startingAfter\":\"N_3\"", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.Contains("\"startingAfter\":\"N_6\"", mcp.Calls[2].Args, StringComparison.Ordinal);
        Assert.All(mcp.Calls, c =>
        {
            Assert.Equal(MerakiNetworkMapper.ToolName, c.Tool);
            Assert.Contains("\"organizationId\":\"1279651\"", c.Args, StringComparison.Ordinal);
            Assert.Contains("\"perPage\":3", c.Args, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task PullAsync_RequiresOrganizationId()
    {
        var mcp = new RecordingMerakiNetworkMcp();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            MerakiNetworkMapper.PullAsync(mcp, Guid.NewGuid(), organizationId: ""));
        Assert.Empty(mcp.Calls);
    }

    /// <summary>Serves meraki_get_organization_networks off an id-ordered array, honouring startingAfter/perPage.</summary>
    private sealed class RecordingMerakiNetworkMcp : IMcpClient
    {
        public List<(Guid ServerId, string Tool, string? Args)> Calls { get; } = [];
        public string NetworksJson { get; init; } = "[]";

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((mcpServerId, toolName, argumentsJson));
            string? startingAfter = null;
            var perPage = MerakiNetworkMapper.DefaultPageSize;
            if (!string.IsNullOrWhiteSpace(argumentsJson))
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.TryGetProperty("startingAfter", out var a) && a.ValueKind == JsonValueKind.String)
                    startingAfter = a.GetString();
                if (doc.RootElement.TryGetProperty("perPage", out var s) && s.ValueKind == JsonValueKind.Number)
                    perPage = s.GetInt32();
            }

            var inner = SliceById(NetworksJson, startingAfter, perPage);
            var body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "1",
                result = new { content = new[] { new { type = "text", text = inner } } },
            });
            return Task.FromResult(body);
        }

        private static string SliceById(string json, string? startingAfter, int perPage)
        {
            using var doc = JsonDocument.Parse(json);
            var items = new List<string>();
            var skip = startingAfter is not null;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();
                if (skip)
                {
                    if (id == startingAfter)
                        skip = false;
                    continue;
                }
                items.Add(item.GetRawText());
                if (items.Count >= perPage)
                    break;
            }
            return "[" + string.Join(",", items) + "]";
        }
    }
}
