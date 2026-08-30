using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class Pax8CompanyMapperTests
{
    // Sanitized Compact pax8_list_companies envelope. Field names match the live tool; values are fixtures.
    // status Active / Inactive / Deleted; page is 0-indexed; last page so a Recording MCP stops.
    public const string LiveCompactListFixture = """
        {"content":[{"id":"11111111-1111-1111-1111-111111111111","name":"Acme Partner LLC","website":"https://acme-partner.example","status":"Active","address":{"city":"Austin","stateOrProvince":"TX"}},{"id":"22222222-2222-2222-2222-222222222222","name":"Inactive Client Inc","website":"https://inactive-client.example","status":"Inactive","address":{"city":"Denver","stateOrProvince":"CO"}},{"id":"33333333-3333-3333-3333-333333333333","name":"Deleted Client Co","website":null,"status":"Deleted","address":{"city":"Seattle","stateOrProvince":"WA"}}],"page":{"size":50,"totalElements":3,"totalPages":1,"number":0}}
        """;

    // Two raw rows (one empty name), not last — mapped count is 1; raw count is 2. Page 0 of 2.
    public const string NextPageContinuesFixture = """
        {"content":[{"id":"11111111-1111-1111-1111-111111111111","name":"Acme Partner LLC","website":"https://acme-partner.example","status":"Active","address":{"city":"Austin","stateOrProvince":"TX"}},{"id":"99999999-9999-9999-9999-999999999999","name":"","website":null,"status":"Active","address":{"city":"Nowhere","stateOrProvince":"NA"}}],"page":{"size":2,"totalElements":3,"totalPages":2,"number":0}}
        """;

    public const string LastPageFixture = """
        {"content":[{"id":"44444444-4444-4444-4444-444444444444","name":"Last Page Co","website":"https://last-page.example","status":"Active","address":{"city":"Boise","stateOrProvince":"ID"}}],"page":{"size":2,"totalElements":3,"totalPages":2,"number":1}}
        """;

    public const string EmptyContentFixture = """
        {"content":[],"page":{"size":50,"totalElements":0,"totalPages":0,"number":0}}
        """;

    [Fact]
    public void MapCompanies_SanitizedEnvelope_MapsActiveInactiveDeleted_WebsiteCityState()
    {
        var companies = Pax8CompanyMapper.MapCompanies(LiveCompactListFixture, out var pageNumber, out var totalPages, out var rowCount);

        Assert.Equal(3, rowCount);
        Assert.Equal(0, pageNumber);
        Assert.Equal(1, totalPages);
        Assert.Equal(3, companies.Count);

        var acme = companies[0];
        Assert.Equal("11111111-1111-1111-1111-111111111111", acme.ExternalId);
        Assert.Equal("Acme Partner LLC", acme.Name);
        Assert.Equal("https://acme-partner.example", acme.Website);
        Assert.Equal("Austin", acme.City);
        Assert.Equal("TX", acme.State);
        Assert.False(acme.IsInactive);
        Assert.Null(acme.Slug);
        Assert.Null(acme.PrimaryDomain);
        Assert.Null(acme.Address);

        var inactive = companies[1];
        Assert.Equal("22222222-2222-2222-2222-222222222222", inactive.ExternalId);
        Assert.Equal("Inactive Client Inc", inactive.Name);
        Assert.Equal("https://inactive-client.example", inactive.Website);
        Assert.Equal("Denver", inactive.City);
        Assert.Equal("CO", inactive.State);
        Assert.True(inactive.IsInactive);

        var deleted = companies[2];
        Assert.Equal("33333333-3333-3333-3333-333333333333", deleted.ExternalId);
        Assert.Equal("Deleted Client Co", deleted.Name);
        Assert.Null(deleted.Website);
        Assert.Equal("Seattle", deleted.City);
        Assert.Equal("WA", deleted.State);
        Assert.True(deleted.IsInactive);
    }

    [Fact]
    public void MapCompanies_Skips_Missing_Id_Or_Name_But_Counts_Raw_Rows()
    {
        const string json = """
            {"content":[{"id":"","name":"No Id","website":"https://noid.example","status":"Active","address":{"city":"X","stateOrProvince":"Y"}},{"id":"55555555-5555-5555-5555-555555555555","name":"","status":"Active"},{"id":"11111111-1111-1111-1111-111111111111","name":"Acme Partner LLC","website":"https://acme-partner.example","status":"Active","address":{"city":"Austin","stateOrProvince":"TX"}}],"page":{"size":50,"totalElements":3,"totalPages":1,"number":0}}
            """;

        var companies = Pax8CompanyMapper.MapCompanies(json, out _, out _, out var rowCount);
        Assert.Equal(3, rowCount);
        var acme = Assert.Single(companies);
        Assert.Equal("Acme Partner LLC", acme.Name);
        Assert.Equal("11111111-1111-1111-1111-111111111111", acme.ExternalId);
        Assert.DoesNotContain(companies, c => c.Name == "No Id");
    }

    [Fact]
    public void MapCompanies_JsonRpcContentText_UnwrapsToPax8Envelope()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = LiveCompactListFixture } } },
        });

        var companies = Pax8CompanyMapper.MapCompanies(wrapped);
        Assert.Equal(3, companies.Count);
        Assert.Equal("11111111-1111-1111-1111-111111111111", companies[0].ExternalId);
        Assert.Equal("Acme Partner LLC", companies[0].Name);
        Assert.Equal("https://acme-partner.example", companies[0].Website);
        Assert.False(companies[0].IsInactive);
        Assert.True(companies[1].IsInactive);
        Assert.True(companies[2].IsInactive);
    }

    [Fact]
    public void BuildArgumentsJson_FirstPage_Size50_OmitsStatus()
    {
        var args = Pax8CompanyMapper.BuildArgumentsJson(page: 0);
        Assert.Contains("\"page\":0", args, StringComparison.Ordinal);
        Assert.Contains("\"size\":50", args, StringComparison.Ordinal);
        Assert.DoesNotContain("status", args, StringComparison.Ordinal);
        Assert.DoesNotContain("city", args, StringComparison.Ordinal);
        Assert.DoesNotContain("sort", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_ClampsSizeToApiMax200_AndNegativePageToZero()
    {
        var args = Pax8CompanyMapper.BuildArgumentsJson(page: -3, pageSize: 20000);
        Assert.Contains("\"page\":0", args, StringComparison.Ordinal);
        Assert.Contains("\"size\":200", args, StringComparison.Ordinal);
        Assert.DoesNotContain("status", args, StringComparison.Ordinal);
    }

    [Fact]
    public void MapCompanies_EmptyAndLastPage_Stop()
    {
        Pax8CompanyMapper.MapCompanies(LiveCompactListFixture, out var lastNumber, out var lastTotal, out var lastCount);
        Assert.Equal(0, lastNumber);
        Assert.Equal(1, lastTotal);
        Assert.Equal(3, lastCount);
        Assert.True(lastNumber + 1 >= lastTotal);

        Pax8CompanyMapper.MapCompanies(LastPageFixture, out var page1, out var pages, out var shortCount);
        Assert.Equal(1, page1);
        Assert.Equal(2, pages);
        Assert.Equal(1, shortCount);
        Assert.True(page1 + 1 >= pages);

        Pax8CompanyMapper.MapCompanies(EmptyContentFixture, out _, out _, out var emptyCount);
        Assert.Equal(0, emptyCount);
    }

    [Fact]
    public async Task PullAsync_LastPage_StopsPaging_DoesNotPassStatus()
    {
        var mcp = new ScriptedMcp([LiveCompactListFixture]);
        var companies = await Pax8CompanyMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 50);

        Assert.Equal(3, companies.Count);
        Assert.Equal("11111111-1111-1111-1111-111111111111", companies[0].ExternalId);
        Assert.Equal("Acme Partner LLC", companies[0].Name);
        Assert.Single(mcp.Calls);
        Assert.Equal(Pax8CompanyMapper.ToolName, mcp.Calls[0].Tool);
        Assert.Contains("\"page\":0", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"size\":50", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("status", mcp.Calls[0].Args, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_ContinuesOnRawCount_NotMappedCount()
    {
        var mcp = new ScriptedMcp([NextPageContinuesFixture, LastPageFixture]);
        var companies = await Pax8CompanyMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 2);

        Assert.Equal(2, companies.Count);
        Assert.Equal("Acme Partner LLC", companies[0].Name);
        Assert.Equal("Last Page Co", companies[1].Name);
        Assert.Equal(2, mcp.Calls.Count);
        Assert.All(mcp.Calls, c => Assert.Equal(Pax8CompanyMapper.ToolName, c.Tool));
        Assert.Contains("\"page\":0", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"size\":2", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("status", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"page\":1", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.Contains("\"size\":2", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("status", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.DoesNotContain(companies, c => c.ExternalId == "99999999-9999-9999-9999-999999999999");
    }

    [Fact]
    public async Task PullAsync_EmptyContent_Stops()
    {
        var mcp = new ScriptedMcp([EmptyContentFixture]);
        var companies = await Pax8CompanyMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 5);

        Assert.Empty(companies);
        Assert.Single(mcp.Calls);
        Assert.Contains("\"page\":0", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"size\":5", mcp.Calls[0].Args, StringComparison.Ordinal);
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
            var inner = _pages.Count > 0 ? _pages.Dequeue() : EmptyContentFixture;
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
