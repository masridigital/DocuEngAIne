using System.Text.Json;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class NinjaOrganizationMapperTests
{
    // Live Compact ninja_list_organizations JSON array (field names exact; not a wrapper object).
    public const string LiveCompactListFixture = """
        [{"name":"Masri Digital","nodeApprovalMode":"MANUAL","id":2},{"name":"Dawn James CPA","nodeApprovalMode":"AUTOMATIC","id":11},{"name":"P4P Team LLC","nodeApprovalMode":"AUTOMATIC","id":15},{"name":"Adroc Capital LLC","nodeApprovalMode":"AUTOMATIC","id":22},{"name":"The Title Network LLC","nodeApprovalMode":"AUTOMATIC","id":23}]
        """;

    [Fact]
    public void MapOrganizations_LiveCompactList_MapsIdAndName_DoesNotInventInactive()
    {
        var companies = NinjaOrganizationMapper.MapOrganizations(LiveCompactListFixture);

        Assert.Equal(5, companies.Count);

        var masri = companies[0];
        Assert.Equal("2", masri.ExternalId);
        Assert.Equal("Masri Digital", masri.Name);
        Assert.Null(masri.IsInactive);
        Assert.Null(masri.Website);
        Assert.Null(masri.Slug);
        Assert.Null(masri.PrimaryDomain);
        Assert.Null(masri.City);
        Assert.Null(masri.State);
        Assert.Null(masri.Address);

        Assert.All(companies, c => Assert.Null(c.IsInactive));
        Assert.Equal("23", companies[^1].ExternalId);
        Assert.Equal("The Title Network LLC", companies[^1].Name);
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

        var companies = NinjaOrganizationMapper.MapOrganizations(wrapped);
        Assert.Equal(5, companies.Count);
        Assert.Equal("2", companies[0].ExternalId);
        Assert.Equal("Masri Digital", companies[0].Name);
        Assert.Null(companies[0].IsInactive);
    }

    [Fact]
    public void BuildArgumentsJson_OmitsAfterOnFirstPage()
    {
        var args = NinjaOrganizationMapper.BuildArgumentsJson(afterOrganizationId: null);
        Assert.Contains("\"pageSize\":50", args, StringComparison.Ordinal);
        Assert.DoesNotContain("after", args, StringComparison.Ordinal);
    }

    [Fact]
    public void MapOrganizations_LiveList_LastOrganizationIdIs23()
    {
        NinjaOrganizationMapper.MapOrganizations(LiveCompactListFixture, out var lastId);
        Assert.Equal(23, lastId);
        var next = NinjaOrganizationMapper.BuildArgumentsJson(afterOrganizationId: lastId);
        Assert.Contains("\"after\":23", next, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":50", next, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_ClampsPageSizeToMax1000()
    {
        var args = NinjaOrganizationMapper.BuildArgumentsJson(afterOrganizationId: null, pageSize: 5000);
        Assert.Contains("\"pageSize\":1000", args, StringComparison.Ordinal);
    }
}
