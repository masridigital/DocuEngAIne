using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class DefensXCustomerMapperTests
{
    // Live Compact dfx_list_customers JSON array. Field names exact; no pagination.
    public const string LiveCompactListFixture = """
        [{"id":"2db9e3bd-020b-4374-8c1d-c6b83d4cb7f4","name":"Adroc Capital","domains":["adroccap.com"],"enabled":true},{"id":"f1f4ad1e-6709-4f88-bf93-0d2c60abd5ec","name":"Masri Digital (Customer)","domains":[],"enabled":true}]
        """;

    // Same live fields plus enabled false so SkipInactive can be exercised without inventing keys.
    public const string SkipInactiveFixture = """
        [{"id":"2db9e3bd-020b-4374-8c1d-c6b83d4cb7f4","name":"Adroc Capital","domains":["adroccap.com"],"enabled":true},{"id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","name":"Disabled Co","domains":["disabled.example"],"enabled":false}]
        """;

    [Fact]
    public void MapCustomers_LiveCompactList_MapsAdrocIdNamePrimaryDomain()
    {
        var companies = DefensXCustomerMapper.MapCustomers(LiveCompactListFixture);

        Assert.Equal(2, companies.Count);

        var adroc = companies[0];
        Assert.Equal("2db9e3bd-020b-4374-8c1d-c6b83d4cb7f4", adroc.ExternalId);
        Assert.Equal("Adroc Capital", adroc.Name);
        Assert.Equal("adroccap.com", adroc.PrimaryDomain);
        Assert.False(adroc.IsInactive);
        Assert.Null(adroc.Slug);
        Assert.Null(adroc.Website);
        Assert.Null(adroc.City);
        Assert.Null(adroc.State);
        Assert.Null(adroc.Address);

        var masri = companies[1];
        Assert.Equal("f1f4ad1e-6709-4f88-bf93-0d2c60abd5ec", masri.ExternalId);
        Assert.Equal("Masri Digital (Customer)", masri.Name);
        Assert.Null(masri.PrimaryDomain);
        Assert.False(masri.IsInactive);
    }

    [Fact]
    public void MapCustomers_EnabledFalse_IsInactiveTrue()
    {
        var companies = DefensXCustomerMapper.MapCustomers(SkipInactiveFixture);

        Assert.Equal(2, companies.Count);
        var adroc = Assert.Single(companies, c => c.Name == "Adroc Capital");
        Assert.False(adroc.IsInactive);
        var disabled = Assert.Single(companies, c => c.Name == "Disabled Co");
        Assert.True(disabled.IsInactive);
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", disabled.ExternalId);
        Assert.Equal("disabled.example", disabled.PrimaryDomain);
    }

    [Fact]
    public void MapCustomers_Skips_Empty_Id_Or_Name()
    {
        const string json = """
            [{"id":"","name":"No Id","domains":["x.example"],"enabled":true},{"id":"bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee","name":"","domains":["y.example"],"enabled":true},{"id":"2db9e3bd-020b-4374-8c1d-c6b83d4cb7f4","name":"Adroc Capital","domains":["adroccap.com"],"enabled":true}]
            """;

        var adroc = Assert.Single(DefensXCustomerMapper.MapCustomers(json));
        Assert.Equal("Adroc Capital", adroc.Name);
        Assert.Equal("2db9e3bd-020b-4374-8c1d-c6b83d4cb7f4", adroc.ExternalId);
    }

    [Fact]
    public void MapCustomers_JsonRpcContentTextArray_UnwrapsToCustomerList()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = LiveCompactListFixture } } },
        });

        var companies = DefensXCustomerMapper.MapCustomers(wrapped);
        Assert.Equal(2, companies.Count);
        Assert.Equal("2db9e3bd-020b-4374-8c1d-c6b83d4cb7f4", companies[0].ExternalId);
        Assert.Equal("Adroc Capital", companies[0].Name);
        Assert.Equal("adroccap.com", companies[0].PrimaryDomain);
    }

    [Fact]
    public async Task PullAsync_Calls_DfxListCustomers_With_No_Arguments()
    {
        var mcp = new ScriptedMcp();
        var companies = await DefensXCustomerMapper.PullAsync(mcp, Guid.NewGuid());

        Assert.Equal(2, companies.Count);
        Assert.Equal("Adroc Capital", companies[0].Name);
        Assert.Equal("adroccap.com", companies[0].PrimaryDomain);
        var call = Assert.Single(mcp.Calls);
        Assert.Equal(DefensXCustomerMapper.ToolName, call.Tool);
        Assert.True(string.IsNullOrWhiteSpace(call.Args));
    }

    private sealed class ScriptedMcp : IMcpClient
    {
        public List<(string Tool, string? Args)> Calls { get; } = [];

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((toolName, argumentsJson));
            var body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "1",
                result = new { content = new[] { new { type = "text", text = LiveCompactListFixture } } },
            });
            return Task.FromResult(body);
        }
    }
}
