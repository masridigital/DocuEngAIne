using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class DattoSiteMapperTests
{
    // Compact datto_list_sites list as [{uid,name}]. Field names from the catalog; no live Datto call.
    public const string SitesArrayFixture = """
        [{"uid":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","name":"Adroc Capital"},{"uid":"11111111-2222-3333-4444-555555555555","name":"Masri Digital"}]
        """;

    // Catalog-equivalent wrapper: the list lives in sites[].
    public const string SitesWrapperFixture = """
        {"sites":[{"uid":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","name":"Adroc Capital"},{"uid":"11111111-2222-3333-4444-555555555555","name":"Masri Digital"}]}
        """;

    // The live response shape (invented values): pageDetails beside sites[], extra site fields ignored.
    public const string LiveShapeFixture = """
        {"pageDetails":{"count":2,"totalCount":2,"prevPageUrl":null,"nextPageUrl":null},
         "sites":[{"id":1001,"uid":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","accountUid":"acct0001","name":"Adroc Capital","description":"","onDemand":false,"devicesStatus":{"numberOfDevices":3},"autotaskCompanyId":null,"portalUrl":"https://example.invalid/site/1001"},
                  {"id":1002,"uid":"11111111-2222-3333-4444-555555555555","accountUid":"acct0001","name":"Masri Digital","description":null,"onDemand":false,"devicesStatus":{"numberOfDevices":0},"autotaskCompanyId":null,"portalUrl":"https://example.invalid/site/1002"}]}
        """;

    [Fact]
    public void MapCompanies_LiveShape_Reads_Sites_Beside_PageDetails()
    {
        var companies = DattoSiteMapper.MapCompanies(LiveShapeFixture, out var rowCount);

        Assert.Equal(2, rowCount);
        Assert.Equal(new[] { "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "11111111-2222-3333-4444-555555555555" }, companies.Select(c => c.ExternalId));
        Assert.Equal("Adroc Capital", companies[0].Name);
        // The numeric id is not the identity: uid is.
        Assert.DoesNotContain(companies, c => c.ExternalId == "1001");
    }

    [Fact]
    public void BuildArguments_Pages_From_Zero()
    {
        // The live schema's first page is 0; the vendor's nextPageUrl for page one says page=1.
        Assert.Contains("\"pageNo\":0", DattoSiteMapper.BuildArgumentsJson(0), StringComparison.Ordinal);
        Assert.Contains("\"pageNo\":0", DattoSiteMapper.BuildArgumentsJson(-3), StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":250", DattoSiteMapper.BuildArgumentsJson(0, 1000), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_Starts_At_Page_Zero_And_Pages_On_Raw_Rows()
    {
        // Page 0 is full (one of its two rows unmappable), page 1 is short: two calls, nothing skipped.
        var mcp = new PagedDattoMcp(
            """[{"uid":"a","name":"Alpha"},{"uid":"","name":"No uid"}]""",
            """[{"uid":"c","name":"Charlie"}]""");

        var companies = await DattoSiteMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 2);

        Assert.Equal(new[] { "a", "c" }, companies.Select(c => c.ExternalId));
        Assert.Equal(2, mcp.Calls.Count);
        Assert.Contains("\"pageNo\":0", mcp.Calls[0], StringComparison.Ordinal);
        Assert.Contains("\"pageNo\":1", mcp.Calls[1], StringComparison.Ordinal);
    }

    private sealed class PagedDattoMcp(params string[] pages) : IMcpClient
    {
        public List<string> Calls { get; } = [];

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Assert.Equal(DattoSiteMapper.ToolName, toolName);
            Calls.Add(argumentsJson ?? "");
            using var args = JsonDocument.Parse(argumentsJson ?? "{}");
            var page = args.RootElement.GetProperty("pageNo").GetInt32();
            return Task.FromResult(page < pages.Length ? pages[page] : "[]");
        }
    }

    [Fact]
    public void MapCompanies_SitesArray_MapsUidAndName_DoesNotInventInactive()
    {
        var companies = DattoSiteMapper.MapCompanies(SitesArrayFixture);

        Assert.Equal(2, companies.Count);

        var adroc = companies[0];
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", adroc.ExternalId);
        Assert.Equal("Adroc Capital", adroc.Name);
        Assert.Null(adroc.IsInactive);
        Assert.Null(adroc.Slug);
        Assert.Null(adroc.PrimaryDomain);
        Assert.Null(adroc.Website);
        Assert.Null(adroc.City);
        Assert.Null(adroc.State);
        Assert.Null(adroc.Address);

        Assert.Equal("11111111-2222-3333-4444-555555555555", companies[1].ExternalId);
        Assert.Equal("Masri Digital", companies[1].Name);
        Assert.All(companies, c => Assert.Null(c.IsInactive));
    }

    [Fact]
    public void MapCompanies_SitesWrapper_MapsUidAndName()
    {
        var companies = DattoSiteMapper.MapCompanies(SitesWrapperFixture);

        Assert.Equal(2, companies.Count);
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", companies[0].ExternalId);
        Assert.Equal("Adroc Capital", companies[0].Name);
        Assert.Equal("11111111-2222-3333-4444-555555555555", companies[1].ExternalId);
        Assert.Equal("Masri Digital", companies[1].Name);
        Assert.All(companies, c => Assert.Null(c.IsInactive));
    }

    [Fact]
    public void MapCompanies_Skips_Empty_Uid_Or_Name()
    {
        const string json = """
            [{"uid":"","name":"No Uid"},{"uid":"bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee","name":""},{"name":"Missing Uid"},{"uid":"cccccccc-cccc-dddd-eeee-ffffffffffff"},{"uid":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","name":"Adroc Capital"}]
            """;

        var adroc = Assert.Single(DattoSiteMapper.MapCompanies(json));
        Assert.Equal("Adroc Capital", adroc.Name);
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", adroc.ExternalId);
    }

    [Fact]
    public void MapCompanies_JsonRpcContentTextArray_UnwrapsToSiteList()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = SitesArrayFixture } } },
        });

        var companies = DattoSiteMapper.MapCompanies(wrapped);
        Assert.Equal(2, companies.Count);
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", companies[0].ExternalId);
        Assert.Equal("Adroc Capital", companies[0].Name);
        Assert.Null(companies[0].IsInactive);
    }

    [Fact]
    public void MapCompanies_JsonRpcContentTextWrapper_UnwrapsToSites()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = SitesWrapperFixture } } },
        });

        var companies = DattoSiteMapper.MapCompanies(wrapped);
        Assert.Equal(2, companies.Count);
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", companies[0].ExternalId);
        Assert.Equal("Adroc Capital", companies[0].Name);
    }
}
