using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class DnsFilterOrganizationMapperTests
{
    // Catalog / OpenAPI fixture for dnsfilter_list_organizations. JSON:API { data[], links.self }.
    // Field names from OrganizationSpec — no live Compact. Networks stay in relationships and are ignored.
    public const string CatalogListFixture = """
        {"data":[{"id":"1001","type":"organizations","uuid":"550e8400-e29b-41d4-a716-446655440001","attributes":{"id":1001,"name":"Adroc Capital","address":"1425 RXR Plaza","billing_address":"1425 Billing Ave","billing_contact_email":"billing@adroccap.example","canceled":false,"canceled_at":null,"external_id":"ext-adroc-1","feature_flags":["reporting"],"managed_by_msp_id":42,"owned_msp_id":null,"stripe_customer_id":"cus_EXAMPLE","unique_id":"adroc-capital"},"relationships":{"networks":{"data":[{"id":"501","type":"networks"}]}}},{"id":"1002","type":"organizations","attributes":{"id":1002,"name":"Canceled Co","address":null,"canceled":true,"external_id":"ext-canceled","unique_id":"canceled-co","managed_by_msp_id":42}}],"links":{"self":"https://api.dnsfilter.com/v1/organizations?page%5Bnumber%5D=1&page%5Bsize%5D=50"}}
        """;

    // Two raw rows (one canceled), full page so paging continues. Mapped count is 1; raw count is 2.
    public const string NextPageContinuesFixture = """
        {"data":[{"id":"1001","type":"organizations","attributes":{"id":1001,"name":"Adroc Capital","address":"1425 RXR Plaza","canceled":false,"unique_id":"adroc-capital"}},{"id":"1002","type":"organizations","attributes":{"id":1002,"name":"Canceled Co","canceled":true,"unique_id":"canceled-co"}}],"links":{"self":"https://api.dnsfilter.com/v1/organizations?page%5Bnumber%5D=1&page%5Bsize%5D=2"}}
        """;

    public const string EmptyDataFixture = """
        {"data":[],"links":{"self":"https://api.dnsfilter.com/v1/organizations?page%5Bnumber%5D=2&page%5Bsize%5D=2"}}
        """;

    [Fact]
    public void MapOrganizations_CatalogJsonApi_MapsIdNameAddressUniqueId_SkipsCanceled_IgnoresNetworksAndBilling()
    {
        var companies = DnsFilterOrganizationMapper.MapOrganizations(CatalogListFixture, out var rowCount);

        Assert.Equal(2, rowCount);
        var adroc = Assert.Single(companies);
        Assert.Equal("1001", adroc.ExternalId);
        Assert.Equal("Adroc Capital", adroc.Name);
        Assert.Equal("1425 RXR Plaza", adroc.Address);
        Assert.Equal("adroc-capital", adroc.Slug);
        Assert.Null(adroc.IsInactive);
        Assert.Null(adroc.PrimaryDomain);
        Assert.Null(adroc.Website);
        Assert.Null(adroc.City);
        Assert.Null(adroc.State);
        Assert.DoesNotContain("Billing", adroc.Address, StringComparison.Ordinal);
        Assert.DoesNotContain("cus_EXAMPLE", adroc.ExternalId, StringComparison.Ordinal);
        Assert.DoesNotContain("ext-adroc-1", adroc.ExternalId, StringComparison.Ordinal);
        Assert.DoesNotContain("501", adroc.ExternalId, StringComparison.Ordinal);
        Assert.DoesNotContain(companies, c => c.Name == "Canceled Co");
        Assert.DoesNotContain(companies, c => c.ExternalId == "1002");
    }

    [Fact]
    public void MapOrganizations_Skips_Empty_Id_Or_Name_And_Network_Type_But_Counts_Raw_Rows()
    {
        const string json = """
            {"data":[{"id":"","type":"organizations","attributes":{"name":"No Id","canceled":false}},{"id":"2002","type":"organizations","attributes":{"name":"","canceled":false}},{"id":"501","type":"networks","attributes":{"name":"HQ Network"}},{"id":"1001","type":"organizations","attributes":{"id":1001,"name":"Adroc Capital","canceled":false,"unique_id":"adroc-capital"}}]}
            """;

        var companies = DnsFilterOrganizationMapper.MapOrganizations(json, out var rowCount);
        Assert.Equal(4, rowCount);
        var adroc = Assert.Single(companies);
        Assert.Equal("Adroc Capital", adroc.Name);
        Assert.Equal("1001", adroc.ExternalId);
        Assert.DoesNotContain(companies, c => c.Name == "No Id");
        Assert.DoesNotContain(companies, c => c.Name == "HQ Network");
        Assert.DoesNotContain(companies, c => c.ExternalId == "501");
    }

    [Fact]
    public void MapOrganizations_JsonRpcContentText_UnwrapsToJsonApiData()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = CatalogListFixture } } },
        });

        var companies = DnsFilterOrganizationMapper.MapOrganizations(wrapped);
        var adroc = Assert.Single(companies);
        Assert.Equal("1001", adroc.ExternalId);
        Assert.Equal("Adroc Capital", adroc.Name);
        Assert.Equal("1425 RXR Plaza", adroc.Address);
        Assert.Equal("adroc-capital", adroc.Slug);
        Assert.Null(adroc.IsInactive);
        Assert.DoesNotContain(companies, c => c.Name == "Canceled Co");
    }

    [Fact]
    public void BuildArgumentsJson_FirstPage_PageSize50_OmitsPageNumberAndFilters()
    {
        var args = DnsFilterOrganizationMapper.BuildArgumentsJson(pageNumber: null);
        Assert.Contains("\"pageSize\":50", args, StringComparison.Ordinal);
        Assert.DoesNotContain("pageNumber", args, StringComparison.Ordinal);
        Assert.DoesNotContain("\"page\":", args, StringComparison.Ordinal);
        Assert.DoesNotContain("basicInfo", args, StringComparison.Ordinal);
        Assert.DoesNotContain("type", args, StringComparison.Ordinal);
        Assert.DoesNotContain("managedByMspId", args, StringComparison.Ordinal);
        Assert.DoesNotContain("ownedMspId", args, StringComparison.Ordinal);
        Assert.DoesNotContain("name", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_ClampsPageSizeToMax1000_AndSendsPageNumberAfterFirst()
    {
        var args = DnsFilterOrganizationMapper.BuildArgumentsJson(pageNumber: 2, pageSize: 20000);
        Assert.Contains("\"pageSize\":1000", args, StringComparison.Ordinal);
        Assert.Contains("\"pageNumber\":2", args, StringComparison.Ordinal);
        Assert.DoesNotContain("\"page\":", args, StringComparison.Ordinal);
        Assert.DoesNotContain("basicInfo", args, StringComparison.Ordinal);
        Assert.DoesNotContain("type", args, StringComparison.Ordinal);

        var first = DnsFilterOrganizationMapper.BuildArgumentsJson(pageNumber: 1, pageSize: 25);
        Assert.Contains("\"pageSize\":25", first, StringComparison.Ordinal);
        Assert.DoesNotContain("pageNumber", first, StringComparison.Ordinal);
    }

    [Fact]
    public void MapOrganizations_EmptyData_RowCountZero()
    {
        var companies = DnsFilterOrganizationMapper.MapOrganizations(EmptyDataFixture, out var rowCount);
        Assert.Empty(companies);
        Assert.Equal(0, rowCount);
    }

    [Fact]
    public async Task PullAsync_CatalogPage_StopsOnShortPage_CallsListOrganizations()
    {
        var mcp = new ScriptedMcp([CatalogListFixture]);
        var companies = await DnsFilterOrganizationMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 50);

        var adroc = Assert.Single(companies);
        Assert.Equal("1001", adroc.ExternalId);
        Assert.Equal("Adroc Capital", adroc.Name);
        Assert.Equal("1425 RXR Plaza", adroc.Address);
        Assert.Null(adroc.IsInactive);
        Assert.DoesNotContain(companies, c => c.Name == "Canceled Co");
        Assert.Single(mcp.Calls);
        Assert.Equal(DnsFilterOrganizationMapper.ToolName, mcp.Calls[0].Tool);
        Assert.Equal("dnsfilter_list_organizations", mcp.Calls[0].Tool);
        Assert.Contains("\"pageSize\":50", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("pageNumber", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("\"page\":", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("basicInfo", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("type", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("dnsfilter_list_networks", mcp.Calls[0].Tool, StringComparison.Ordinal);
        Assert.DoesNotContain("dnsfilter_get_organization", mcp.Calls[0].Tool, StringComparison.Ordinal);
        Assert.DoesNotContain("dnsfilter_list_all_organizations", mcp.Calls[0].Tool, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_ContinuesOnRawCount_NotMappedCount()
    {
        var mcp = new ScriptedMcp([NextPageContinuesFixture, EmptyDataFixture]);
        var companies = await DnsFilterOrganizationMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 2);

        var adroc = Assert.Single(companies);
        Assert.Equal("Adroc Capital", adroc.Name);
        Assert.Equal("1001", adroc.ExternalId);
        Assert.Equal(2, mcp.Calls.Count);
        Assert.All(mcp.Calls, c => Assert.Equal(DnsFilterOrganizationMapper.ToolName, c.Tool));
        Assert.DoesNotContain("pageNumber", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":2", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageNumber\":2", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":2", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.DoesNotContain(companies, c => c.Name == "Canceled Co");
        Assert.DoesNotContain(companies, c => c.ExternalId == "1002");
    }

    [Fact]
    public async Task PullAsync_EmptyData_Stops()
    {
        var mcp = new ScriptedMcp([EmptyDataFixture]);
        var companies = await DnsFilterOrganizationMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 5);

        Assert.Empty(companies);
        Assert.Single(mcp.Calls);
        Assert.Equal(DnsFilterOrganizationMapper.ToolName, mcp.Calls[0].Tool);
        Assert.Contains("\"pageSize\":5", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("pageNumber", mcp.Calls[0].Args, StringComparison.Ordinal);
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
            var inner = _pages.Count > 0 ? _pages.Dequeue() : EmptyDataFixture;
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
