using System.Text.Json;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class MerakiOrganizationMapperTests
{
    // Live Compact meraki_get_organizations JSON array (field names exact; not a wrapper object).
    public const string LiveCompactListFixture = """
        [{"id":"1279651","name":"7 Compression","url":"https://n565.dashboard.meraki.com/o/T-0Fub/manage/organization/overview","samlConsumerUrls":null,"samlConsumerUrl":null,"api":{"enabled":true},"licensing":{"model":"co-term"},"cloud":{"region":{"name":"North America","host":{"name":"United States"}}},"management":{"details":[{"name":"customer number","value":"31640086"}]}},{"id":"1721429","name":"TTS Cyber","url":"https://n99.dashboard.meraki.com/o/MVoY9c/manage/organization/overview","samlConsumerUrls":null,"samlConsumerUrl":null,"api":{"enabled":true},"licensing":{"model":"co-term"},"cloud":{"region":{"name":"North America","host":{"name":"United States"}}},"management":{"details":[]}}]
        """;

    [Fact]
    public void MapOrganizations_LiveCompactList_MapsIdNameAndUrl_DoesNotInventInactive()
    {
        var companies = MerakiOrganizationMapper.MapOrganizations(LiveCompactListFixture);

        Assert.Equal(2, companies.Count);

        var compression = companies[0];
        Assert.Equal("1279651", compression.ExternalId);
        Assert.Equal("7 Compression", compression.Name);
        Assert.Equal("https://n565.dashboard.meraki.com/o/T-0Fub/manage/organization/overview", compression.Website);
        Assert.Null(compression.IsInactive);
        Assert.Null(compression.Slug);
        Assert.Null(compression.PrimaryDomain);
        Assert.Null(compression.City);
        Assert.Null(compression.State);
        Assert.Null(compression.Address);

        Assert.Equal("1721429", companies[1].ExternalId);
        Assert.Equal("TTS Cyber", companies[1].Name);
        Assert.Equal("https://n99.dashboard.meraki.com/o/MVoY9c/manage/organization/overview", companies[1].Website);
        Assert.All(companies, c => Assert.Null(c.IsInactive));
    }

    [Fact]
    public void MapOrganizations_JsonRpcContentTextArray_UnwrapsToOrgList()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = LiveCompactListFixture } } },
        });

        var companies = MerakiOrganizationMapper.MapOrganizations(wrapped);
        Assert.Equal(2, companies.Count);
        Assert.Equal("1279651", companies[0].ExternalId);
        Assert.Equal("7 Compression", companies[0].Name);
        Assert.Equal("https://n565.dashboard.meraki.com/o/T-0Fub/manage/organization/overview", companies[0].Website);
        Assert.Null(companies[0].IsInactive);
    }

    [Fact]
    public void BuildArgumentsJson_OmitsStartingAfterOnFirstPage()
    {
        var args = MerakiOrganizationMapper.BuildArgumentsJson(startingAfter: null);
        Assert.Contains("\"perPage\":50", args, StringComparison.Ordinal);
        Assert.DoesNotContain("startingAfter", args, StringComparison.Ordinal);
    }

    [Fact]
    public void MapOrganizations_LiveList_LastOrganizationIdIs1721429()
    {
        MerakiOrganizationMapper.MapOrganizations(LiveCompactListFixture, out var lastId);
        Assert.Equal("1721429", lastId);
        var next = MerakiOrganizationMapper.BuildArgumentsJson(startingAfter: lastId);
        Assert.Contains("\"startingAfter\":\"1721429\"", next, StringComparison.Ordinal);
        Assert.Contains("\"perPage\":50", next, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_ClampsPageSizeToMax9000()
    {
        var args = MerakiOrganizationMapper.BuildArgumentsJson(startingAfter: null, pageSize: 20000);
        Assert.Contains("\"perPage\":9000", args, StringComparison.Ordinal);
    }
}
