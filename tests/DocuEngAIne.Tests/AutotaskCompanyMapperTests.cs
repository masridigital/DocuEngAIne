using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class AutotaskCompanyMapperTests
{
    // Live Compact at_list_companies wrapper (maxRecords 5). Field names exact from the live tool.
    // id 0 is a real company. pageDetails.nextPageUrl is the cursor; count/requestCount are raw page size.
    public const string LiveNextPageUrl =
        "https://webservices15.autotask.net/ATServicesRest/V1.0/Companies/query/next?paging=eyJwYWdlIjoyfQ";

    public const string LiveCompactListFixture = """
        {"items":[{"id":0,"companyName":"Pacific Cloud Cyber","companyNumber":"PCC","companyType":8,"isActive":true,"city":"Salem","state":"Oregon","address1":"222 Comercial St","webAddress":null,"phone":"5034492009","parentCompanyID":203},{"id":174,"companyName":"Autotask Corporation","companyNumber":"AUTO","companyType":1,"isActive":false,"city":"East Greenbush","state":"NY","webAddress":"www.autotask.com"}],"pageDetails":{"count":5,"requestCount":5,"prevPageUrl":null,"nextPageUrl":"https://webservices15.autotask.net/ATServicesRest/V1.0/Companies/query/next?paging=eyJwYWdlIjoyfQ"}}
        """;

    // Same two live rows, last page — nextPageUrl null so a Recording MCP that always returns this body stops.
    public const string LastPageFixture = """
        {"items":[{"id":0,"companyName":"Pacific Cloud Cyber","companyNumber":"PCC","companyType":8,"isActive":true,"city":"Salem","state":"Oregon","address1":"222 Comercial St","webAddress":null,"phone":"5034492009","parentCompanyID":203},{"id":174,"companyName":"Autotask Corporation","companyNumber":"AUTO","companyType":1,"isActive":false,"city":"East Greenbush","state":"NY","webAddress":"www.autotask.com"}],"pageDetails":{"count":2,"requestCount":5,"prevPageUrl":null,"nextPageUrl":null}}
        """;

    // Two raw rows (one empty name), full page, more pages exist. Mapped count is 1; raw count is 2.
    public const string NextPageContinuesFixture = """
        {"items":[{"id":0,"companyName":"Pacific Cloud Cyber","companyNumber":"PCC","companyType":8,"isActive":true,"city":"Salem","state":"Oregon","address1":"222 Comercial St","webAddress":null},{"id":99,"companyName":"","companyNumber":"SKIP","companyType":8,"isActive":true}],"pageDetails":{"count":2,"requestCount":2,"prevPageUrl":null,"nextPageUrl":"https://webservices15.autotask.net/ATServicesRest/V1.0/Companies/query/next?paging=eyJwYWdlIjoyfQ"}}
        """;

    public const string EmptyItemsFixture = """
        {"items":[],"pageDetails":{"count":0,"requestCount":2,"prevPageUrl":null,"nextPageUrl":null}}
        """;

    [Fact]
    public void MapCompanies_LiveCompactList_MapsIdZeroPacificCloudCyber_CityStateAddressSlug()
    {
        var companies = AutotaskCompanyMapper.MapCompanies(LiveCompactListFixture, out var nextPageUrl, out var rowCount);

        Assert.Equal(2, rowCount);
        Assert.Equal(LiveNextPageUrl, nextPageUrl);
        Assert.Equal(2, companies.Count);

        var pcc = companies[0];
        Assert.Equal("0", pcc.ExternalId);
        Assert.Equal("Pacific Cloud Cyber", pcc.Name);
        Assert.Equal("PCC", pcc.Slug);
        Assert.Equal("Salem", pcc.City);
        Assert.Equal("Oregon", pcc.State);
        Assert.Equal("222 Comercial St", pcc.Address);
        Assert.Null(pcc.Website);
        Assert.False(pcc.IsInactive);
        Assert.Null(pcc.PrimaryDomain);

        var autotask = companies[1];
        Assert.Equal("174", autotask.ExternalId);
        Assert.Equal("Autotask Corporation", autotask.Name);
        Assert.Equal("AUTO", autotask.Slug);
        Assert.Equal("East Greenbush", autotask.City);
        Assert.Equal("NY", autotask.State);
        Assert.Null(autotask.Address);
        Assert.Equal("www.autotask.com", autotask.Website);
        Assert.True(autotask.IsInactive);
    }

    [Fact]
    public void MapCompanies_DoesNotFilterByCompanyType_AndSkipsEmptyName()
    {
        var companies = AutotaskCompanyMapper.MapCompanies(NextPageContinuesFixture, out _, out var rowCount);

        Assert.Equal(2, rowCount);
        var pcc = Assert.Single(companies);
        Assert.Equal("0", pcc.ExternalId);
        Assert.Equal("Pacific Cloud Cyber", pcc.Name);
        Assert.DoesNotContain(companies, c => c.ExternalId == "99");
    }

    [Fact]
    public void MapCompanies_JsonRpcContentText_UnwrapsToItems()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = LiveCompactListFixture } } },
        });

        var companies = AutotaskCompanyMapper.MapCompanies(wrapped);
        Assert.Equal(2, companies.Count);
        Assert.Equal("0", companies[0].ExternalId);
        Assert.Equal("Pacific Cloud Cyber", companies[0].Name);
        Assert.Equal("PCC", companies[0].Slug);
        Assert.False(companies[0].IsInactive);
    }

    [Fact]
    public void BuildArgumentsJson_FirstPage_MaxRecords50_OmitsNextPageUrl()
    {
        var args = AutotaskCompanyMapper.BuildArgumentsJson(nextPageUrl: null);
        Assert.Contains("\"maxRecords\":50", args, StringComparison.Ordinal);
        Assert.DoesNotContain("nextPageUrl", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_ClampsMaxRecordsToApiMax500()
    {
        var args = AutotaskCompanyMapper.BuildArgumentsJson(nextPageUrl: null, pageSize: 20000);
        Assert.Contains("\"maxRecords\":500", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_NextPageUrl_PassedVerbatim_OmitsMaxRecords()
    {
        var args = AutotaskCompanyMapper.BuildArgumentsJson(LiveNextPageUrl);
        Assert.Contains($"\"nextPageUrl\":\"{LiveNextPageUrl}\"", args, StringComparison.Ordinal);
        Assert.DoesNotContain("maxRecords", args, StringComparison.Ordinal);
    }

    [Fact]
    public void MapCompanies_NullAndEmptyNextPageUrl_Stops()
    {
        AutotaskCompanyMapper.MapCompanies(LastPageFixture, out var nulled, out var lastCount, out var lastShort);
        Assert.Null(nulled);
        Assert.Equal(2, lastCount);
        Assert.True(lastShort);

        AutotaskCompanyMapper.MapCompanies(EmptyItemsFixture, out var emptyUrl, out var emptyCount);
        Assert.Null(emptyUrl);
        Assert.Equal(0, emptyCount);

        const string emptyStringNext = """
            {"items":[{"id":0,"companyName":"Pacific Cloud Cyber","isActive":true}],"pageDetails":{"count":1,"requestCount":1,"nextPageUrl":""}}
            """;
        AutotaskCompanyMapper.MapCompanies(emptyStringNext, out var emptyString, out _);
        Assert.Null(emptyString);

        const string missingDetails = """{"items":[{"id":0,"companyName":"Pacific Cloud Cyber","isActive":true}]}""";
        AutotaskCompanyMapper.MapCompanies(missingDetails, out var missing, out _);
        Assert.Null(missing);
    }

    [Fact]
    public async Task PullAsync_NullNextPageUrl_StopsPaging()
    {
        var mcp = new ScriptedMcp([LastPageFixture]);
        var companies = await AutotaskCompanyMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 5);

        Assert.Equal(2, companies.Count);
        Assert.Equal("0", companies[0].ExternalId);
        Assert.Equal("Pacific Cloud Cyber", companies[0].Name);
        Assert.Single(mcp.Calls);
        Assert.Equal(AutotaskCompanyMapper.ToolName, mcp.Calls[0].Tool);
        Assert.Contains("\"maxRecords\":5", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("nextPageUrl", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("at_list_active_companies", mcp.Calls.Select(c => c.Tool));
        Assert.DoesNotContain("at_list_customer_companies", mcp.Calls.Select(c => c.Tool));
    }

    [Fact]
    public async Task PullAsync_NextPageUrl_ContinuesOnRawCount_NotMappedCount()
    {
        var mcp = new ScriptedMcp([NextPageContinuesFixture, EmptyItemsFixture]);
        var companies = await AutotaskCompanyMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 2);

        var pcc = Assert.Single(companies);
        Assert.Equal("Pacific Cloud Cyber", pcc.Name);
        Assert.Equal("0", pcc.ExternalId);
        Assert.Equal(2, mcp.Calls.Count);
        Assert.All(mcp.Calls, c => Assert.Equal(AutotaskCompanyMapper.ToolName, c.Tool));
        Assert.DoesNotContain("nextPageUrl", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"maxRecords\":2", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains($"\"nextPageUrl\":\"{LiveNextPageUrl}\"", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("maxRecords", mcp.Calls[1].Args, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_LivePage_ContinuesWhenCountEqualsRequestCount_AndNextPageUrlPresent()
    {
        var mcp = new ScriptedMcp([LiveCompactListFixture, EmptyItemsFixture]);
        var companies = await AutotaskCompanyMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 5);

        Assert.Equal(2, companies.Count);
        Assert.Equal("0", companies[0].ExternalId);
        Assert.Equal(2, mcp.Calls.Count);
        Assert.Contains($"\"nextPageUrl\":\"{LiveNextPageUrl}\"", mcp.Calls[1].Args, StringComparison.Ordinal);
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
}
