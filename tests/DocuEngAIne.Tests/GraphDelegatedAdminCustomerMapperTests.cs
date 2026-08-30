using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class GraphDelegatedAdminCustomerMapperTests
{
    // Microsoft Graph delegatedAdminCustomers shape (field names from the documented Compact /
    // Graph passthrough). Values are fixtures — no live Graph or Compact call.
    public const string HomeTenantId = "f7812296-5bce-41dc-8102-b1b270e7c4c7";

    public const string LiveNextLink =
        "https://graph.microsoft.com/v1.0/tenantRelationships/delegatedAdminCustomers?$skiptoken=RFNwdAIAAQAAAD8";

    public const string GraphListFixture = """
        {"@odata.context":"https://graph.microsoft.com/v1.0/tenantRelationships/$metadata#delegatedAdminCustomers","value":[{"@odata.type":"#microsoft.graph.delegatedAdminCustomer","id":"4fdbff88-9d6b-42e0-9713-45c922ba8001","tenantId":"4fdbff88-9d6b-42e0-9713-45c922ba8001","displayName":"Contoso Inc"},{"@odata.type":"#microsoft.graph.delegatedAdminCustomer","id":"1c0fa218-5dec-49db-8247-cfa457af8116","tenantId":"1c0fa218-5dec-49db-8247-cfa457af8116","displayName":"Contoso subsidiary Inc"},{"@odata.type":"#microsoft.graph.delegatedAdminCustomer","id":"f7812296-5bce-41dc-8102-b1b270e7c4c7","tenantId":"f7812296-5bce-41dc-8102-b1b270e7c4c7","displayName":"Masri Digital"}]}
        """;

    public const string LastPageFixture = """
        {"@odata.context":"https://graph.microsoft.com/v1.0/tenantRelationships/$metadata#delegatedAdminCustomers","value":[{"id":"4fdbff88-9d6b-42e0-9713-45c922ba8001","tenantId":"4fdbff88-9d6b-42e0-9713-45c922ba8001","displayName":"Contoso Inc"},{"id":"1c0fa218-5dec-49db-8247-cfa457af8116","tenantId":"1c0fa218-5dec-49db-8247-cfa457af8116","displayName":"Contoso subsidiary Inc"}]}
        """;

    // Two raw rows (one empty name), nextLink present. Mapped count is 1; raw count is 2.
    public const string NextPageContinuesFixture = """
        {"@odata.context":"https://graph.microsoft.com/v1.0/tenantRelationships/$metadata#delegatedAdminCustomers","@odata.nextLink":"https://graph.microsoft.com/v1.0/tenantRelationships/delegatedAdminCustomers?$skiptoken=RFNwdAIAAQAAAD8","value":[{"id":"4fdbff88-9d6b-42e0-9713-45c922ba8001","tenantId":"4fdbff88-9d6b-42e0-9713-45c922ba8001","displayName":"Contoso Inc"},{"id":"99999999-9999-9999-9999-999999999999","tenantId":"99999999-9999-9999-9999-999999999999","displayName":""}]}
        """;

    public const string EmptyValueFixture = """
        {"@odata.context":"https://graph.microsoft.com/v1.0/tenantRelationships/$metadata#delegatedAdminCustomers","value":[]}
        """;

    public const string GdapStatusFixture = """
        {"value":[{"id":"4fdbff88-9d6b-42e0-9713-45c922ba8001","tenantId":"4fdbff88-9d6b-42e0-9713-45c922ba8001","displayName":"Contoso Inc","status":"active"},{"id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","tenantId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","displayName":"Expired Co","status":"expired"},{"id":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","tenantId":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","displayName":"No Flag Co"}]}
        """;

    [Fact]
    public void MapCustomers_GraphValue_MapsTenantIdDisplayName_SkipsHomeTenant()
    {
        var companies = GraphDelegatedAdminCustomerMapper.MapCustomers(
            GraphListFixture, out var nextLink, out var rowCount, HomeTenantId);

        Assert.Equal(3, rowCount);
        Assert.Null(nextLink);
        Assert.Equal(2, companies.Count);

        var contoso = companies[0];
        Assert.Equal("4fdbff88-9d6b-42e0-9713-45c922ba8001", contoso.ExternalId);
        Assert.Equal("Contoso Inc", contoso.Name);
        Assert.Null(contoso.IsInactive);
        Assert.Null(contoso.Slug);
        Assert.Null(contoso.PrimaryDomain);
        Assert.Null(contoso.Website);
        Assert.Null(contoso.City);
        Assert.Null(contoso.State);
        Assert.Null(contoso.Address);

        var sub = companies[1];
        Assert.Equal("1c0fa218-5dec-49db-8247-cfa457af8116", sub.ExternalId);
        Assert.Equal("Contoso subsidiary Inc", sub.Name);
        Assert.Null(sub.IsInactive);

        Assert.DoesNotContain(companies, c => c.Name == "Masri Digital");
        Assert.DoesNotContain(companies, c => c.ExternalId == HomeTenantId);
    }

    [Fact]
    public void MapCustomers_PrefersTenantId_FallsBackToId()
    {
        const string json = """
            {"value":[{"id":"11111111-1111-1111-1111-111111111111","tenantId":"4fdbff88-9d6b-42e0-9713-45c922ba8001","displayName":"Contoso Inc"},{"id":"1c0fa218-5dec-49db-8247-cfa457af8116","displayName":"Id Only Co"}]}
            """;

        var companies = GraphDelegatedAdminCustomerMapper.MapCustomers(json);
        Assert.Equal(2, companies.Count);
        Assert.Equal("4fdbff88-9d6b-42e0-9713-45c922ba8001", companies[0].ExternalId);
        Assert.Equal("Contoso Inc", companies[0].Name);
        Assert.Equal("1c0fa218-5dec-49db-8247-cfa457af8116", companies[1].ExternalId);
        Assert.Equal("Id Only Co", companies[1].Name);
    }

    [Fact]
    public void MapCustomers_Skips_Missing_Id_Or_Name_But_Counts_Raw_Rows()
    {
        const string json = """
            {"value":[{"id":"","tenantId":"","displayName":"No Id"},{"id":"1c0fa218-5dec-49db-8247-cfa457af8116","tenantId":"1c0fa218-5dec-49db-8247-cfa457af8116","displayName":""},{"id":"4fdbff88-9d6b-42e0-9713-45c922ba8001","tenantId":"4fdbff88-9d6b-42e0-9713-45c922ba8001","displayName":"Contoso Inc"}]}
            """;

        var companies = GraphDelegatedAdminCustomerMapper.MapCustomers(json, out _, out var rowCount);
        Assert.Equal(3, rowCount);
        var contoso = Assert.Single(companies);
        Assert.Equal("Contoso Inc", contoso.Name);
        Assert.Equal("4fdbff88-9d6b-42e0-9713-45c922ba8001", contoso.ExternalId);
        Assert.DoesNotContain(companies, c => c.Name == "No Id");
    }

    [Fact]
    public void MapCustomers_SkipInactive_KeepsActiveGdap_WhenFlagPresent_OtherwiseMapsAll()
    {
        var all = GraphDelegatedAdminCustomerMapper.MapCustomers(
            GdapStatusFixture, out _, out var allCount, homeTenantId: null, skipInactive: false);
        Assert.Equal(3, allCount);
        Assert.Equal(3, all.Count);
        Assert.False(Assert.Single(all, c => c.Name == "Contoso Inc").IsInactive);
        Assert.True(Assert.Single(all, c => c.Name == "Expired Co").IsInactive);
        Assert.Null(Assert.Single(all, c => c.Name == "No Flag Co").IsInactive);

        var skipped = GraphDelegatedAdminCustomerMapper.MapCustomers(
            GdapStatusFixture, out _, out var skipCount, homeTenantId: null, skipInactive: true);
        Assert.Equal(3, skipCount);
        Assert.Equal(2, skipped.Count);
        Assert.Contains(skipped, c => c.Name == "Contoso Inc");
        Assert.Contains(skipped, c => c.Name == "No Flag Co");
        Assert.DoesNotContain(skipped, c => c.Name == "Expired Co");
    }

    [Fact]
    public void MapCustomers_NoGdapFlag_SkipInactiveStillMapsAll()
    {
        var companies = GraphDelegatedAdminCustomerMapper.MapCustomers(
            LastPageFixture, out _, out var rowCount, homeTenantId: null, skipInactive: true);

        Assert.Equal(2, rowCount);
        Assert.Equal(2, companies.Count);
        Assert.All(companies, c => Assert.Null(c.IsInactive));
    }

    [Fact]
    public void MapCustomers_JsonRpcContentText_UnwrapsToGraphValue()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = LastPageFixture } } },
        });

        var companies = GraphDelegatedAdminCustomerMapper.MapCustomers(wrapped);
        Assert.Equal(2, companies.Count);
        Assert.Equal("4fdbff88-9d6b-42e0-9713-45c922ba8001", companies[0].ExternalId);
        Assert.Equal("Contoso Inc", companies[0].Name);
    }

    [Fact]
    public void BuildArgumentsJson_FirstPage_MaxItems50_OmitsNextLink()
    {
        var args = GraphDelegatedAdminCustomerMapper.BuildArgumentsJson(nextLink: null);
        Assert.Contains("\"maxItems\":50", args, StringComparison.Ordinal);
        Assert.DoesNotContain("nextLink", args, StringComparison.Ordinal);
        Assert.DoesNotContain("filter", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_ClampsMaxItemsToApiMax1000_AndPassesFilter()
    {
        var args = GraphDelegatedAdminCustomerMapper.BuildArgumentsJson(
            nextLink: null, maxItems: 20000, filter: "startsWith(displayName,'Contoso')");
        Assert.Contains("\"maxItems\":1000", args, StringComparison.Ordinal);
        Assert.Contains("\"filter\":\"startsWith(displayName,'Contoso')\"", args, StringComparison.Ordinal);
        Assert.DoesNotContain("nextLink", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_NextLink_PassedVerbatim_OmitsMaxItemsAndFilter()
    {
        var args = GraphDelegatedAdminCustomerMapper.BuildArgumentsJson(
            LiveNextLink, maxItems: 50, filter: "startsWith(displayName,'Contoso')");
        Assert.Contains($"\"nextLink\":\"{LiveNextLink}\"", args, StringComparison.Ordinal);
        Assert.DoesNotContain("maxItems", args, StringComparison.Ordinal);
        Assert.DoesNotContain("filter", args, StringComparison.Ordinal);
    }

    [Fact]
    public void MapCustomers_NullAndEmptyNextLink_Stops()
    {
        GraphDelegatedAdminCustomerMapper.MapCustomers(LastPageFixture, out var missing, out var lastCount);
        Assert.Null(missing);
        Assert.Equal(2, lastCount);

        GraphDelegatedAdminCustomerMapper.MapCustomers(EmptyValueFixture, out var emptyUrl, out var emptyCount);
        Assert.Null(emptyUrl);
        Assert.Equal(0, emptyCount);

        const string emptyStringNext = """
            {"value":[{"id":"4fdbff88-9d6b-42e0-9713-45c922ba8001","tenantId":"4fdbff88-9d6b-42e0-9713-45c922ba8001","displayName":"Contoso Inc"}],"@odata.nextLink":""}
            """;
        GraphDelegatedAdminCustomerMapper.MapCustomers(emptyStringNext, out var emptyString, out _);
        Assert.Null(emptyString);

        GraphDelegatedAdminCustomerMapper.MapCustomers(NextPageContinuesFixture, out var next, out var raw);
        Assert.Equal(LiveNextLink, next);
        Assert.Equal(2, raw);
    }

    [Fact]
    public async Task PullAsync_NullNextLink_StopsPaging_DoesNotCallPartnerOrRelationships()
    {
        var mcp = new ScriptedMcp([LastPageFixture]);
        var companies = await GraphDelegatedAdminCustomerMapper.PullAsync(
            mcp, Guid.NewGuid(), pageSize: 5, homeTenantId: HomeTenantId);

        Assert.Equal(2, companies.Count);
        Assert.Equal("4fdbff88-9d6b-42e0-9713-45c922ba8001", companies[0].ExternalId);
        Assert.Equal("Contoso Inc", companies[0].Name);
        Assert.Single(mcp.Calls);
        Assert.Equal(GraphDelegatedAdminCustomerMapper.ToolName, mcp.Calls[0].Tool);
        Assert.Contains("\"maxItems\":5", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("nextLink", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("graph_list_partner_customers", mcp.Calls.Select(c => c.Tool));
        Assert.DoesNotContain("graph_list_delegated_admin_relationships", mcp.Calls.Select(c => c.Tool));
    }

    [Fact]
    public async Task PullAsync_NextLink_ContinuesOnRawCount_NotMappedCount()
    {
        var mcp = new ScriptedMcp([NextPageContinuesFixture, EmptyValueFixture]);
        var companies = await GraphDelegatedAdminCustomerMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 2);

        var contoso = Assert.Single(companies);
        Assert.Equal("Contoso Inc", contoso.Name);
        Assert.Equal("4fdbff88-9d6b-42e0-9713-45c922ba8001", contoso.ExternalId);
        Assert.Equal(2, mcp.Calls.Count);
        Assert.All(mcp.Calls, c => Assert.Equal(GraphDelegatedAdminCustomerMapper.ToolName, c.Tool));
        Assert.DoesNotContain("nextLink", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"maxItems\":2", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains($"\"nextLink\":\"{LiveNextLink}\"", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("maxItems", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("graph_list_partner_customers", mcp.Calls.Select(c => c.Tool));
    }

    [Fact]
    public async Task PullAsync_SkipInactive_DropsExpiredGdap_LeavesUnflagged()
    {
        var mcp = new ScriptedMcp([GdapStatusFixture]);
        var companies = await GraphDelegatedAdminCustomerMapper.PullAsync(
            mcp, Guid.NewGuid(), pageSize: 50, skipInactive: true);

        Assert.Equal(2, companies.Count);
        Assert.Contains(companies, c => c.Name == "Contoso Inc");
        Assert.Contains(companies, c => c.Name == "No Flag Co");
        Assert.DoesNotContain(companies, c => c.Name == "Expired Co");
        Assert.Single(mcp.Calls);
        Assert.Equal(GraphDelegatedAdminCustomerMapper.ToolName, mcp.Calls[0].Tool);
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
            var inner = _pages.Count > 0 ? _pages.Dequeue() : EmptyValueFixture;
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
