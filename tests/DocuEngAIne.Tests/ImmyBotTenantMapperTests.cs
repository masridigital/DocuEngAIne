using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class ImmyBotTenantMapperTests
{
    // Compact immy_list_tenants list as [{id,name}]. Field names from the catalog; no live Compact.
    public const string TenantsArrayFixture = """
        [{"id":12,"name":"ExampleCo"},{"id":29,"name":"Contoso"}]
        """;

    // Catalog-equivalent wrappers: tenants[] (company list) and clients[] (same id/name shape).
    public const string TenantsWrapperFixture = """
        {"tenants":[{"id":12,"name":"ExampleCo"},{"id":29,"name":"Contoso"}]}
        """;

    public const string ClientsWrapperFixture = """
        {"clients":[{"id":12,"name":"ExampleCo"},{"id":29,"name":"Contoso"}]}
        """;

    // Two raw rows (one empty name), full page. Mapped count is 1; raw count is 2.
    public const string NextPageContinuesFixture = """
        [{"id":12,"name":"ExampleCo"},{"id":99,"name":""}]
        """;

    public const string EmptyArrayFixture = """
        []
        """;

    [Fact]
    public void MapTenants_CatalogArray_MapsIdAndName_DoesNotInventInactive()
    {
        var companies = ImmyBotTenantMapper.MapTenants(TenantsArrayFixture, out var rowCount);

        Assert.Equal(2, rowCount);
        Assert.Equal(2, companies.Count);

        var example = companies[0];
        Assert.Equal("12", example.ExternalId);
        Assert.Equal("ExampleCo", example.Name);
        Assert.Null(example.IsInactive);
        Assert.Null(example.Slug);
        Assert.Null(example.PrimaryDomain);
        Assert.Null(example.Website);
        Assert.Null(example.City);
        Assert.Null(example.State);
        Assert.Null(example.Address);

        Assert.Equal("29", companies[1].ExternalId);
        Assert.Equal("Contoso", companies[1].Name);
        Assert.All(companies, c => Assert.Null(c.IsInactive));
    }

    [Fact]
    public void MapTenants_TenantsWrapper_MapsIdAndName()
    {
        var companies = ImmyBotTenantMapper.MapTenants(TenantsWrapperFixture);

        Assert.Equal(2, companies.Count);
        Assert.Equal("12", companies[0].ExternalId);
        Assert.Equal("ExampleCo", companies[0].Name);
        Assert.Equal("29", companies[1].ExternalId);
        Assert.Equal("Contoso", companies[1].Name);
        Assert.All(companies, c => Assert.Null(c.IsInactive));
    }

    [Fact]
    public void MapTenants_ClientsWrapper_MapsIdAndName()
    {
        var companies = ImmyBotTenantMapper.MapTenants(ClientsWrapperFixture);

        Assert.Equal(2, companies.Count);
        Assert.Equal("12", companies[0].ExternalId);
        Assert.Equal("ExampleCo", companies[0].Name);
        Assert.Equal("29", companies[1].ExternalId);
        Assert.Equal("Contoso", companies[1].Name);
        Assert.All(companies, c => Assert.Null(c.IsInactive));
    }

    [Fact]
    public void MapTenants_IgnoresParentAzureAndComputerFields()
    {
        const string json = """
            [{"id":12,"name":"ExampleCo","parentTenantId":1,"azureTenantId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","computerCount":4}]
            """;

        var example = Assert.Single(ImmyBotTenantMapper.MapTenants(json));
        Assert.Equal("12", example.ExternalId);
        Assert.Equal("ExampleCo", example.Name);
        Assert.Null(example.IsInactive);
        Assert.Null(example.Slug);
        Assert.Null(example.PrimaryDomain);
        Assert.Null(example.Website);
    }

    [Fact]
    public void MapTenants_Skips_Empty_Id_Or_Name()
    {
        const string json = """
            [{"id":"","name":"No Id"},{"id":99,"name":""},{"name":"Missing Id"},{"id":100},{"id":12,"name":"ExampleCo"}]
            """;

        var companies = ImmyBotTenantMapper.MapTenants(json, out var rowCount);

        Assert.Equal(5, rowCount);
        var example = Assert.Single(companies);
        Assert.Equal("ExampleCo", example.Name);
        Assert.Equal("12", example.ExternalId);
        Assert.DoesNotContain(companies, c => c.Name == "No Id");
        Assert.DoesNotContain(companies, c => c.Name == "Missing Id");
        Assert.DoesNotContain(companies, c => c.ExternalId == "99");
        Assert.DoesNotContain(companies, c => c.ExternalId == "100");
    }

    [Fact]
    public void MapTenants_JsonRpcContentTextArray_UnwrapsToTenantList()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = TenantsArrayFixture } } },
        });

        var companies = ImmyBotTenantMapper.MapTenants(wrapped);
        Assert.Equal(2, companies.Count);
        Assert.Equal("12", companies[0].ExternalId);
        Assert.Equal("ExampleCo", companies[0].Name);
        Assert.Null(companies[0].IsInactive);
    }

    [Fact]
    public void MapTenants_JsonRpcContentTextWrapper_UnwrapsToTenants()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = TenantsWrapperFixture } } },
        });

        var companies = ImmyBotTenantMapper.MapTenants(wrapped);
        Assert.Equal(2, companies.Count);
        Assert.Equal("12", companies[0].ExternalId);
        Assert.Equal("ExampleCo", companies[0].Name);
    }

    [Fact]
    public void BuildArgumentsJson_FirstPage_Page1PageSize50_OmitsFiltersSorts()
    {
        var args = ImmyBotTenantMapper.BuildArgumentsJson(page: 1);
        Assert.Contains("\"page\":1", args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":50", args, StringComparison.Ordinal);
        Assert.DoesNotContain("filters", args, StringComparison.Ordinal);
        Assert.DoesNotContain("sorts", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_ClampsPageSizeToMax100_AndPageToAtLeast1()
    {
        var args = ImmyBotTenantMapper.BuildArgumentsJson(page: 0, pageSize: 200);
        Assert.Contains("\"page\":1", args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":100", args, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_ShortPage_StopsPaging()
    {
        var mcp = new ScriptedMcp([TenantsArrayFixture]);
        var companies = await ImmyBotTenantMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 5);

        Assert.Equal(2, companies.Count);
        Assert.Equal("ExampleCo", companies[0].Name);
        Assert.Equal("12", companies[0].ExternalId);
        Assert.Equal("Contoso", companies[1].Name);
        Assert.Single(mcp.Calls);
        Assert.Equal(ImmyBotTenantMapper.ToolName, mcp.Calls[0].Tool);
        Assert.Contains("\"page\":1", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":5", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain(mcp.Calls, c => c.Tool == "immy_get_tenants");
        Assert.DoesNotContain(mcp.Calls, c => c.Tool == "immy_list_computers");
        Assert.DoesNotContain(mcp.Calls, c => c.Tool == "immy_list_provider_links_clients");
    }

    [Fact]
    public async Task PullAsync_FullPage_ContinuesOnRawCount_NotMappedCount()
    {
        var mcp = new ScriptedMcp([NextPageContinuesFixture, EmptyArrayFixture]);
        var companies = await ImmyBotTenantMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 2);

        var example = Assert.Single(companies);
        Assert.Equal("ExampleCo", example.Name);
        Assert.Equal("12", example.ExternalId);
        Assert.Equal(2, mcp.Calls.Count);
        Assert.All(mcp.Calls, c => Assert.Equal(ImmyBotTenantMapper.ToolName, c.Tool));
        Assert.Contains("\"page\":1", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":2", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"page\":2", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":2", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.DoesNotContain(mcp.Calls, c => c.Tool == "immy_get_tenants");
        Assert.DoesNotContain(mcp.Calls, c => c.Tool == "immy_list_computers");
    }

    [Fact]
    public async Task PullAsync_EmptyArray_StopsEvenWhenMorePagesExist()
    {
        var mcp = new ScriptedMcp([EmptyArrayFixture, TenantsArrayFixture]);
        var companies = await ImmyBotTenantMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 5);

        Assert.Empty(companies);
        Assert.Single(mcp.Calls);
        Assert.Equal(ImmyBotTenantMapper.ToolName, mcp.Calls[0].Tool);
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
            var inner = _pages.Count > 0 ? _pages.Dequeue() : EmptyArrayFixture;
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
