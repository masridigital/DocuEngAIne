using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class NinjaLocationMapperTests
{
    // Hand-built Compact ninja_list_locations JSON array (field names from catalog; not a live call).
    // Location ids 18/24/37/43 match NinjaDeviceMapperTests.LiveCompactDeviceListFixture locationId values.
    public const string LocationsArrayFixture = """
        [{"id":18,"name":"Main Office","address":"100 Dawn Way","description":"Primary office","organizationId":11},{"id":24,"name":"HQ","address":"200 Masri St","description":"Headquarters","organizationId":2},{"id":37,"name":"Dallas","address":"50 Commerce Blvd","description":"","organizationId":22},{"id":43,"name":"Property Intel","organizationId":24}]
        """;

    [Fact]
    public void MapLocations_LocationsArray_MapsIdNameAddressAndOrganization_DoesNotInventCityOrInactive()
    {
        var locations = NinjaLocationMapper.MapLocations(LocationsArrayFixture);

        Assert.Equal(4, locations.Count);

        var main = locations[0];
        Assert.Equal("18", main.ExternalId);
        Assert.Equal("11", main.ClientExternalId);
        Assert.Equal("Main Office", main.Name);
        Assert.Equal("100 Dawn Way", main.Address);
        Assert.Null(main.City);
        Assert.Null(main.IsInactive);

        var hq = locations[1];
        Assert.Equal("24", hq.ExternalId);
        Assert.Equal("2", hq.ClientExternalId);
        Assert.Equal("HQ", hq.Name);
        Assert.Equal("200 Masri St", hq.Address);

        var dallas = locations[2];
        Assert.Equal("37", dallas.ExternalId);
        Assert.Equal("50 Commerce Blvd", dallas.Address);

        var intel = locations[3];
        Assert.Equal("43", intel.ExternalId);
        Assert.Equal("24", intel.ClientExternalId);
        Assert.Equal("Property Intel", intel.Name);
        Assert.Null(intel.Address);

        Assert.All(locations, l => Assert.Null(l.City));
        Assert.All(locations, l => Assert.Null(l.IsInactive));
    }

    [Fact]
    public void MapLocations_DescriptionIsNotCopiedIntoAddress()
    {
        const string json = """
            [{"id":18,"name":"Main Office","description":"Primary office","organizationId":11}]
            """;

        var main = Assert.Single(NinjaLocationMapper.MapLocations(json));
        Assert.Equal("Main Office", main.Name);
        Assert.Null(main.Address);
    }

    [Fact]
    public void MapLocations_DropsRowsWithoutIdNameOrOrganization()
    {
        const string json = """
            [{"name":"No Id","address":"1 Missing Id","organizationId":11},{"id":19,"address":"1 Missing Name","organizationId":11},{"id":20,"name":"No Org","address":"1 Missing Org"},{"id":18,"name":"Main Office","address":"100 Dawn Way","organizationId":11}]
            """;

        var only = Assert.Single(NinjaLocationMapper.MapLocations(json, out var lastId));
        Assert.Equal("18", only.ExternalId);
        Assert.Equal("11", only.ClientExternalId);
        Assert.Equal("Main Office", only.Name);

        // The cursor still advances past dropped rows, or PullAsync would loop on the same page.
        Assert.Equal(18, lastId);
    }

    [Fact]
    public void MapLocations_JsonRpcContentTextArray_UnwrapsToLocationList()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = LocationsArrayFixture } } },
        });

        var locations = NinjaLocationMapper.MapLocations(wrapped);
        Assert.Equal(4, locations.Count);
        Assert.Equal("18", locations[0].ExternalId);
        Assert.Equal("Main Office", locations[0].Name);
        Assert.Equal("100 Dawn Way", locations[0].Address);
        Assert.Null(locations[0].City);
        Assert.Null(locations[0].IsInactive);
    }

    [Fact]
    public void MapLocations_ToolError_Throws()
    {
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            error = new { code = -32000, message = "ninja auth expired" },
        });

        var ex = Assert.Throws<InvalidOperationException>(() => { NinjaLocationMapper.MapLocations(body); });
        Assert.Contains("ninja auth expired", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_OmitsAfterOnFirstPage()
    {
        var args = NinjaLocationMapper.BuildArgumentsJson(afterLocationId: null);
        Assert.Contains("\"pageSize\":50", args, StringComparison.Ordinal);
        Assert.DoesNotContain("after", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_ClampsPageSizeToMax1000()
    {
        var args = NinjaLocationMapper.BuildArgumentsJson(afterLocationId: null, pageSize: 5000);
        Assert.Contains("\"pageSize\":1000", args, StringComparison.Ordinal);
    }

    [Fact]
    public void MapLocations_LocationsArray_LastLocationIdIs43()
    {
        NinjaLocationMapper.MapLocations(LocationsArrayFixture, out var lastId);
        Assert.Equal(43, lastId);
        var next = NinjaLocationMapper.BuildArgumentsJson(afterLocationId: lastId);
        Assert.Contains("\"after\":43", next, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":50", next, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_SecondCall_Receives_After_Cursor_From_First_Page()
    {
        var mcp = new RecordingNinjaLocationMcp { LocationsJson = LocationsArrayFixture };
        var serverId = Guid.NewGuid();

        var locations = await NinjaLocationMapper.PullAsync(mcp, serverId, pageSize: 3);

        Assert.Equal(4, locations.Count);
        Assert.Equal(2, mcp.Calls.Count);
        Assert.All(mcp.Calls, c => Assert.Equal(NinjaLocationMapper.ToolName, c.Tool));
        Assert.DoesNotContain(mcp.Calls, c => c.Tool == "ninja_get_organization_locations");
        Assert.Equal(serverId, mcp.Calls[0].ServerId);
        Assert.DoesNotContain("after", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":3", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"after\":37", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":3", mcp.Calls[1].Args, StringComparison.Ordinal);
    }

    /// <summary>
    /// A full page containing one droppable row must not end the pull. Paging is decided on the rows
    /// the vendor returned, not the rows that mapped — otherwise a single location with no name
    /// silently abandons every location after it, while the run still reports Succeeded.
    /// </summary>
    [Fact]
    public async Task PullAsync_KeepsPaging_When_A_Full_Page_Contains_A_Dropped_Row()
    {
        // Location 2 has an id (so the cursor can advance) but no name, so MapLocation drops it.
        // Locations 4-6 are only reachable if paging continues past that page.
        const string locations = """
            [{"id":1,"name":"one","organizationId":10},
             {"id":2,"organizationId":10},
             {"id":3,"name":"three","organizationId":10},
             {"id":4,"name":"four","organizationId":10},
             {"id":5,"name":"five","organizationId":10},
             {"id":6,"name":"six","organizationId":10}]
            """;

        var mcp = new RecordingNinjaLocationMcp { LocationsJson = locations };

        var pulled = await NinjaLocationMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 3);

        Assert.Equal(5, pulled.Count);
        Assert.Equal(3, mcp.Calls.Count);
        Assert.DoesNotContain(pulled, l => l.ExternalId == "2");
        Assert.Contains(pulled, l => l.ExternalId == "6");
        Assert.DoesNotContain(mcp.Calls, c => c.Tool == "ninja_get_organization_locations");
    }

    /// <summary>Serves ninja_list_locations off an id-ordered array, honouring the after/pageSize cursor.</summary>
    private sealed class RecordingNinjaLocationMcp : IMcpClient
    {
        public List<(Guid ServerId, string Tool, string? Args)> Calls { get; } = [];
        public string LocationsJson { get; init; } = "[]";

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((mcpServerId, toolName, argumentsJson));
            int? after = null;
            var pageSize = NinjaLocationMapper.DefaultPageSize;
            if (!string.IsNullOrWhiteSpace(argumentsJson))
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.TryGetProperty("after", out var a) && a.ValueKind == JsonValueKind.Number)
                    after = a.GetInt32();
                if (doc.RootElement.TryGetProperty("pageSize", out var s) && s.ValueKind == JsonValueKind.Number)
                    pageSize = s.GetInt32();
            }

            var inner = SliceById(LocationsJson, after, pageSize);
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
}
