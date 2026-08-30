using System.Text.Json;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class UnifiHostMapperTests
{
    // Live Compact unifi_sm_list_hosts wrapper (hosts, not sites — every site is meta.name="default").
    public const string LiveCompactListFixture = """
        {"data":[{"id":"host-1","isBlocked":false,"reportedState":{"name":"Adroc Capital: 1425 RXR Plaza","hostname":"Adroc-Capital-1425-RXR-Plaza","location":{"text":"Wyandanch, NY, United States"}}},{"id":"host-2","isBlocked":true,"reportedState":{"name":"Blocked Co","hostname":"blocked"}}]}
        """;

    [Fact]
    public void MapHosts_LiveCompactList_MapsNameFromReportedState_CityFromLocationText()
    {
        var companies = UnifiHostMapper.MapHosts(LiveCompactListFixture);

        Assert.Equal(2, companies.Count);

        var adroc = companies[0];
        Assert.Equal("host-1", adroc.ExternalId);
        Assert.Equal("Adroc Capital: 1425 RXR Plaza", adroc.Name);
        Assert.Equal("Wyandanch, NY, United States", adroc.City);
        Assert.False(adroc.IsInactive);
        Assert.Null(adroc.Slug);
        Assert.Null(adroc.PrimaryDomain);
        Assert.Null(adroc.State);
        Assert.Null(adroc.Website);
        Assert.Null(adroc.Address);

        Assert.Equal("host-2", companies[1].ExternalId);
        Assert.Equal("Blocked Co", companies[1].Name);
        Assert.True(companies[1].IsInactive);
        Assert.Null(companies[1].City);
    }

    [Fact]
    public void MapHosts_JsonRpcContentTextWrapper_UnwrapsToHostList()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = LiveCompactListFixture } } },
        });

        var companies = UnifiHostMapper.MapHosts(wrapped);
        Assert.Equal(2, companies.Count);
        Assert.Equal("host-1", companies[0].ExternalId);
        Assert.Equal("Adroc Capital: 1425 RXR Plaza", companies[0].Name);
        Assert.Equal("Wyandanch, NY, United States", companies[0].City);
        Assert.False(companies[0].IsInactive);
    }

    [Fact]
    public void MapHosts_FallsBackToHostname_WhenReportedNameEmpty()
    {
        const string json = """
            {"data":[{"id":"host-3","isBlocked":false,"reportedState":{"hostname":"Adroc-Capital-1425-RXR-Plaza"}}]}
            """;
        var companies = UnifiHostMapper.MapHosts(json);
        var host = Assert.Single(companies);
        Assert.Equal("host-3", host.ExternalId);
        Assert.Equal("Adroc-Capital-1425-RXR-Plaza", host.Name);
    }

    [Fact]
    public void MapHosts_DoesNotSkipOwnerFalse()
    {
        const string json = """
            {"data":[{"id":"host-4","isBlocked":false,"owner":false,"reportedState":{"name":"Relay Console"}}]}
            """;
        var companies = UnifiHostMapper.MapHosts(json);
        var host = Assert.Single(companies);
        Assert.Equal("host-4", host.ExternalId);
        Assert.Equal("Relay Console", host.Name);
        Assert.False(host.IsInactive);
    }

    [Fact]
    public void BuildArgumentsJson_OmitsNextTokenOnFirstPage()
    {
        var args = UnifiHostMapper.BuildArgumentsJson(nextToken: null);
        Assert.Contains("\"pageSize\":50", args, StringComparison.Ordinal);
        Assert.DoesNotContain("nextToken", args, StringComparison.Ordinal);
    }

    [Fact]
    public void MapHosts_ReadsNextTokenFromWrapper()
    {
        const string json = """
            {"data":[{"id":"host-1","isBlocked":false,"reportedState":{"name":"Adroc Capital: 1425 RXR Plaza"}}],"nextToken":"tok-2","httpStatusCode":200}
            """;
        UnifiHostMapper.MapHosts(json, out var nextToken);
        Assert.Equal("tok-2", nextToken);
        var next = UnifiHostMapper.BuildArgumentsJson(nextToken: nextToken);
        Assert.Contains("\"nextToken\":\"tok-2\"", next, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":50", next, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_ClampsPageSizeToMax50()
    {
        var args = UnifiHostMapper.BuildArgumentsJson(nextToken: null, pageSize: 20000);
        Assert.Contains("\"pageSize\":50", args, StringComparison.Ordinal);
    }
}
