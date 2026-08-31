using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class GraphPartnerCustomerMapperTests
{
    // Partner Center customers shape used for enrich (domain / companyName). Fixtures only.
    public const string HomeTenantId = "f7812296-5bce-41dc-8102-b1b270e7c4c7";

    public const string PartnerListFixture = """
        {"totalCount":3,"items":[{"id":"b44bb1fb-c595-45b0-9e09-d657365580bf","companyProfile":{"tenantId":"4fdbff88-9d6b-42e0-9713-45c922ba8001","domain":"contoso.onmicrosoft.com","companyName":"Contoso Inc"},"relationshipToPartner":"reseller"},{"id":"45c44870-ef77-4fdd-b6fe-3dacb075cff2","companyProfile":{"tenantId":"1c0fa218-5dec-49db-8247-cfa457af8116","domain":"subsidiary.contoso.com","companyName":"Contoso subsidiary Inc"},"relationshipToPartner":"reseller"},{"id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","companyProfile":{"tenantId":"f7812296-5bce-41dc-8102-b1b270e7c4c7","domain":"masridigital.com","companyName":"Masri Digital"},"relationshipToPartner":"reseller"}],"continuationToken":null}
        """;

    public const string NextPageContinuesFixture = """
        {"totalCount":2,"items":[{"id":"b44bb1fb-c595-45b0-9e09-d657365580bf","companyProfile":{"tenantId":"4fdbff88-9d6b-42e0-9713-45c922ba8001","domain":"contoso.onmicrosoft.com","companyName":"Contoso Inc"}},{"id":"99999999-9999-9999-9999-999999999999","companyProfile":{"tenantId":"99999999-9999-9999-9999-999999999999","domain":"skip.example","companyName":""}}],"continuationToken":"page-2-token"}
        """;

    public const string EmptyItemsFixture = """
        {"totalCount":0,"items":[],"continuationToken":null}
        """;

    [Fact]
    public void MapCustomers_PartnerItems_MapsTenantIdNameDomain_SkipsHome_EnrichFieldsOnly()
    {
        var companies = GraphPartnerCustomerMapper.MapCustomers(
            PartnerListFixture, out var token, out var rowCount, HomeTenantId);

        Assert.Equal(3, rowCount);
        Assert.Null(token);
        Assert.Equal(2, companies.Count);

        var contoso = companies[0];
        Assert.Equal("4fdbff88-9d6b-42e0-9713-45c922ba8001", contoso.ExternalId);
        Assert.Equal("Contoso Inc", contoso.Name);
        Assert.Equal("contoso.onmicrosoft.com", contoso.PrimaryDomain);
        Assert.Null(contoso.IsInactive);
        Assert.Null(contoso.Website);

        var sub = companies[1];
        Assert.Equal("1c0fa218-5dec-49db-8247-cfa457af8116", sub.ExternalId);
        Assert.Equal("subsidiary.contoso.com", sub.PrimaryDomain);

        Assert.DoesNotContain(companies, c => c.ExternalId == HomeTenantId);
        Assert.DoesNotContain(companies, c => c.Name == "Masri Digital");
    }

    [Fact]
    public void MapCustomers_Skips_Missing_Id_Or_Name()
    {
        const string json = """
            {"items":[{"id":"","companyProfile":{"tenantId":"","domain":"x.example","companyName":"No Id"}},{"id":"45c44870-ef77-4fdd-b6fe-3dacb075cff2","companyProfile":{"tenantId":"1c0fa218-5dec-49db-8247-cfa457af8116","domain":"y.example","companyName":""}},{"id":"b44bb1fb-c595-45b0-9e09-d657365580bf","companyProfile":{"tenantId":"4fdbff88-9d6b-42e0-9713-45c922ba8001","domain":"contoso.onmicrosoft.com","companyName":"Contoso Inc"}}]}
            """;

        var contoso = Assert.Single(GraphPartnerCustomerMapper.MapCustomers(json));
        Assert.Equal("Contoso Inc", contoso.Name);
        Assert.Equal("4fdbff88-9d6b-42e0-9713-45c922ba8001", contoso.ExternalId);
        Assert.Equal("contoso.onmicrosoft.com", contoso.PrimaryDomain);
    }

    [Fact]
    public void MapCustomers_JsonRpcContentText_UnwrapsToItems()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = PartnerListFixture } } },
        });

        var companies = GraphPartnerCustomerMapper.MapCustomers(wrapped, out _, out _, HomeTenantId);
        Assert.Equal(2, companies.Count);
        Assert.Equal("contoso.onmicrosoft.com", companies[0].PrimaryDomain);
    }

    [Fact]
    public void BuildArgumentsJson_FirstPage_Size100_OmitsContinuationToken()
    {
        var args = GraphPartnerCustomerMapper.BuildArgumentsJson(continuationToken: null);
        Assert.Contains("\"size\":100", args, StringComparison.Ordinal);
        Assert.DoesNotContain("continuationToken", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_ClampsSizeToApiMax500_AndPassesToken()
    {
        var args = GraphPartnerCustomerMapper.BuildArgumentsJson("page-2-token", pageSize: 20000);
        Assert.Contains("\"size\":500", args, StringComparison.Ordinal);
        Assert.Contains("\"continuationToken\":\"page-2-token\"", args, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_CallsPartnerCustomers_DoesNotCallDelegatedAdmin()
    {
        var mcp = new ScriptedMcp([PartnerListFixture]);
        var companies = await GraphPartnerCustomerMapper.PullAsync(
            mcp, Guid.NewGuid(), pageSize: 40, homeTenantId: HomeTenantId);

        Assert.Equal(2, companies.Count);
        Assert.Equal("Contoso Inc", companies[0].Name);
        Assert.Equal("contoso.onmicrosoft.com", companies[0].PrimaryDomain);
        var call = Assert.Single(mcp.Calls);
        Assert.Equal(GraphPartnerCustomerMapper.ToolName, call.Tool);
        Assert.Contains("\"size\":40", call.Args, StringComparison.Ordinal);
        Assert.DoesNotContain("continuationToken", call.Args, StringComparison.Ordinal);
        Assert.DoesNotContain(GraphDelegatedAdminCustomerMapper.ToolName, mcp.Calls.Select(c => c.Tool));
    }

    [Fact]
    public async Task PullAsync_ContinuationToken_ContinuesOnRawCount()
    {
        var mcp = new ScriptedMcp([NextPageContinuesFixture, EmptyItemsFixture]);
        var companies = await GraphPartnerCustomerMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 2);

        var contoso = Assert.Single(companies);
        Assert.Equal("Contoso Inc", contoso.Name);
        Assert.Equal(2, mcp.Calls.Count);
        Assert.All(mcp.Calls, c => Assert.Equal(GraphPartnerCustomerMapper.ToolName, c.Tool));
        Assert.DoesNotContain("continuationToken", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"continuationToken\":\"page-2-token\"", mcp.Calls[1].Args, StringComparison.Ordinal);
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
