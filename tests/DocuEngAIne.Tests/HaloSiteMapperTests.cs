using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class HaloSiteMapperTests
{
    // Hand-built Halo Site_View envelope (field names from Halo GET /Site + includeaddress).
    // Compact halo_list_sites has no pageNo — the full list is one shot.
    public const string SitesEnvelopeFixture = """
        {
          "record_count": 2,
          "sites": [
            {
              "id": 4,
              "name": "HQ",
              "client_id": 12,
              "inactive": false,
              "delivery_address_line1": "123 Main St",
              "delivery_address_line3": "Austin"
            },
            {
              "id": 9,
              "name": "Closed Depot",
              "client_id": 29,
              "inactive": true,
              "delivery_address_line1": "50 Depot Rd",
              "delivery_address_line3": "Dallas"
            }
          ]
        }
        """;

    [Fact]
    public void MapSites_SitesEnvelope_MapsIdClientAddressCityAndInactive()
    {
        var locations = HaloSiteMapper.MapSites(SitesEnvelopeFixture);

        Assert.Equal(2, locations.Count);

        var hq = locations[0];
        Assert.Equal("4", hq.ExternalId);
        Assert.Equal("12", hq.ClientExternalId);
        Assert.Equal("HQ", hq.Name);
        Assert.Equal("123 Main St", hq.Address);
        Assert.Equal("Austin", hq.City);
        Assert.False(hq.IsInactive);

        var depot = locations[1];
        Assert.Equal("9", depot.ExternalId);
        Assert.Equal("29", depot.ClientExternalId);
        Assert.Equal("Closed Depot", depot.Name);
        Assert.Equal("50 Depot Rd", depot.Address);
        Assert.Equal("Dallas", depot.City);
        Assert.True(depot.IsInactive);
    }

    [Fact]
    public void MapSites_NestedDeliveryAddress_MapsLine1AndLine3City()
    {
        const string json = """
            {
              "sites": [
                {
                  "id": 4,
                  "name": "HQ",
                  "client_id": 12,
                  "inactive": false,
                  "delivery_address": {
                    "line1": "123 Main St",
                    "line2": "Suite 100",
                    "line3": "Austin",
                    "line4": "TX",
                    "postcode": "78701"
                  }
                }
              ]
            }
            """;

        var hq = Assert.Single(HaloSiteMapper.MapSites(json));
        Assert.Equal("4", hq.ExternalId);
        Assert.Equal("12", hq.ClientExternalId);
        Assert.Equal("123 Main St", hq.Address);
        Assert.Equal("Austin", hq.City);
        Assert.False(hq.IsInactive);
    }

    [Fact]
    public void MapSites_TopLevelAddressAndCity_MapsThoseFields()
    {
        const string json = """
            {
              "sites": [
                {
                  "id": 4,
                  "name": "HQ",
                  "client_id": 12,
                  "inactive": false,
                  "address": "123 Main St",
                  "city": "Austin"
                }
              ]
            }
            """;

        var hq = Assert.Single(HaloSiteMapper.MapSites(json));
        Assert.Equal("123 Main St", hq.Address);
        Assert.Equal("Austin", hq.City);
    }

    [Fact]
    public void MapSites_RawArray_MapsFullList()
    {
        const string json = """
            [
              {"id":4,"name":"HQ","client_id":12,"inactive":false},
              {"id":9,"name":"Closed Depot","client_id":29,"inactive":true}
            ]
            """;

        var locations = HaloSiteMapper.MapSites(json);
        Assert.Equal(2, locations.Count);
        Assert.Equal("4", locations[0].ExternalId);
        Assert.Equal("9", locations[1].ExternalId);
        Assert.Null(locations[0].Address);
        Assert.Null(locations[0].City);
    }

    [Fact]
    public void MapSites_DropsRowsWithoutIdClientOrName()
    {
        const string json = """
            {
              "sites": [
                {"name":"No Id","client_id":12},
                {"id":5,"name":"No Client"},
                {"id":6,"client_id":12},
                {"id":4,"name":"HQ","client_id":12,"inactive":false}
              ]
            }
            """;

        var hq = Assert.Single(HaloSiteMapper.MapSites(json));
        Assert.Equal("4", hq.ExternalId);
        Assert.Equal("12", hq.ClientExternalId);
        Assert.Equal("HQ", hq.Name);
    }

    [Fact]
    public void MapSites_StoppedWithoutInactive_IsInactive()
    {
        const string json = """
            {
              "sites": [
                {
                  "id": 8,
                  "name": "Stopped Site",
                  "client_id": 12,
                  "stopped": 1
                }
              ]
            }
            """;

        var site = Assert.Single(HaloSiteMapper.MapSites(json));
        Assert.True(site.IsInactive);
    }

    [Fact]
    public void MapSites_JsonRpcContentText_UnwrapsToSiteList()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = SitesEnvelopeFixture } } },
        });

        var locations = HaloSiteMapper.MapSites(wrapped);
        Assert.Equal(2, locations.Count);
        Assert.Equal("4", locations[0].ExternalId);
        Assert.Equal("HQ", locations[0].Name);
        Assert.Equal("123 Main St", locations[0].Address);
        Assert.Equal("Austin", locations[0].City);
    }

    [Fact]
    public void MapSites_ToolError_Throws()
    {
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            error = new { code = -32000, message = "halo auth expired" },
        });

        var ex = Assert.Throws<InvalidOperationException>(() => { HaloSiteMapper.MapSites(body); });
        Assert.Contains("halo auth expired", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_AlwaysIncludeAddress_OmitsPageNoAndClientId()
    {
        var args = HaloSiteMapper.BuildArgumentsJson();
        Assert.Contains("\"includeAddress\":true", args, StringComparison.Ordinal);
        Assert.Contains("\"includeInactive\":true", args, StringComparison.Ordinal);
        Assert.DoesNotContain("pageNo", args, StringComparison.Ordinal);
        Assert.DoesNotContain("pageSize", args, StringComparison.Ordinal);
        Assert.DoesNotContain("clientId", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_OptionalClientId_AndIncludeInactiveFalse()
    {
        var args = HaloSiteMapper.BuildArgumentsJson(clientId: 12, includeInactive: false);
        Assert.Contains("\"includeAddress\":true", args, StringComparison.Ordinal);
        Assert.Contains("\"includeInactive\":false", args, StringComparison.Ordinal);
        Assert.Contains("\"clientId\":12", args, StringComparison.Ordinal);
        Assert.DoesNotContain("pageNo", args, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_Calls_HaloListSites_Once_WithIncludeAddress()
    {
        var mcp = new ScriptedMcp();
        var serverId = Guid.NewGuid();

        var locations = await HaloSiteMapper.PullAsync(mcp, serverId, clientId: 12, includeInactive: false);

        Assert.Equal(2, locations.Count);
        Assert.Equal("HQ", locations[0].Name);
        var call = Assert.Single(mcp.Calls);
        Assert.Equal(HaloSiteMapper.ToolName, call.Tool);
        Assert.Equal(serverId, call.ServerId);
        Assert.Contains("\"includeAddress\":true", call.Args, StringComparison.Ordinal);
        Assert.Contains("\"includeInactive\":false", call.Args, StringComparison.Ordinal);
        Assert.Contains("\"clientId\":12", call.Args, StringComparison.Ordinal);
        Assert.DoesNotContain("pageNo", call.Args, StringComparison.Ordinal);
    }

    private sealed class ScriptedMcp : IMcpClient
    {
        public List<(Guid ServerId, string Tool, string? Args)> Calls { get; } = [];

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((mcpServerId, toolName, argumentsJson));
            var body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "1",
                result = new { content = new[] { new { type = "text", text = SitesEnvelopeFixture } } },
            });
            return Task.FromResult(body);
        }
    }
}
