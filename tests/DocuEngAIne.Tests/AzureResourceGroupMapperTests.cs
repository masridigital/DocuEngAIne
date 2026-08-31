using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class AzureResourceGroupMapperTests
{
    // Hand-built ARM {value:[...]} envelope — Compact azure_list_resource_groups field names
    // (id, name, location, properties.provisioningState). No live Azure/Compact capture.
    // Parent subscription 11111111-1111-1111-1111-111111111111 is Masri Digital in
    // AzureSubscriptionMapperTests.CompactSubscriptionListFixture.
    public const string CompactResourceGroupListFixture = """
        {"value":[{"id":"/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-masri-prod","name":"rg-masri-prod","type":"Microsoft.Resources/resourceGroups","location":"eastus","properties":{"provisioningState":"Succeeded"}},{"id":"/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-adroc","name":"rg-adroc","type":"Microsoft.Resources/resourceGroups","location":"westus2","tags":{"env":"prod"},"managedBy":"someone","properties":{"provisioningState":"Succeeded"}}]}
        """;

    public const string NextLinkFixture = """
        {"value":[{"id":"/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-masri-prod","name":"rg-masri-prod","location":"eastus","properties":{"provisioningState":"Succeeded"}}],"nextLink":"https://management.azure.com/subscriptions/11111111-1111-1111-1111-111111111111/resourcegroups?api-version=2021-04-01&$skiptoken=abc"}
        """;

    private const string DegenerateFixture = """
        {"value":[{"id":"/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/no-name","location":"eastus"},{"name":"orphan-no-sub","location":"eastus"},{"id":"/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-ok","name":"rg-ok","location":"eastus","properties":{"provisioningState":"Succeeded"}}]}
        """;

    [Fact]
    public void MapResourceGroups_ArmValueEnvelope_MapsIdNameLocationAndProvisioningState()
    {
        var groups = AzureResourceGroupMapper.MapResourceGroups(
            CompactResourceGroupListFixture,
            subscriptionId: "11111111-1111-1111-1111-111111111111",
            out var nextLink,
            out var rowCount);

        Assert.Equal(2, rowCount);
        Assert.Null(nextLink);
        Assert.Equal(2, groups.Count);

        var prod = groups[0];
        Assert.Equal("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-masri-prod", prod.ExternalId);
        Assert.Equal("11111111-1111-1111-1111-111111111111", prod.SubscriptionExternalId);
        Assert.Equal("rg-masri-prod", prod.Name);
        Assert.Equal("eastus", prod.Location);
        Assert.Equal("Succeeded", prod.ProvisioningState);

        var adroc = groups[1];
        Assert.Equal("/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-adroc", adroc.ExternalId);
        Assert.Equal("rg-adroc", adroc.Name);
        Assert.Equal("westus2", adroc.Location);
        Assert.Equal("Succeeded", adroc.ProvisioningState);
    }

    [Fact]
    public void MapResourceGroups_JsonRpcContentText_UnwrapsArmEnvelope()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = CompactResourceGroupListFixture } } },
        });

        var groups = AzureResourceGroupMapper.MapResourceGroups(
            wrapped,
            subscriptionId: "11111111-1111-1111-1111-111111111111",
            out _,
            out _);

        Assert.Equal(2, groups.Count);
        Assert.Equal("rg-masri-prod", groups[0].Name);
        Assert.Equal("eastus", groups[0].Location);
        Assert.Equal("11111111-1111-1111-1111-111111111111", groups[0].SubscriptionExternalId);
    }

    [Fact]
    public void MapResourceGroups_UsesFallbackSubscriptionId_WhenArmIdOmitsIt()
    {
        const string json = """{"value":[{"name":"rg-no-arm-id","location":"centralus"}]}""";

        var only = Assert.Single(AzureResourceGroupMapper.MapResourceGroups(
            json,
            subscriptionId: "11111111-1111-1111-1111-111111111111",
            out _,
            out _));
        Assert.Equal("rg-no-arm-id", only.ExternalId);
        Assert.Equal("11111111-1111-1111-1111-111111111111", only.SubscriptionExternalId);
        Assert.Equal("rg-no-arm-id", only.Name);
        Assert.Equal("centralus", only.Location);
        Assert.Null(only.ProvisioningState);
    }

    [Fact]
    public void MapResourceGroups_SkipsMissingNameOrSubscription_ButCountsRawRows()
    {
        var groups = AzureResourceGroupMapper.MapResourceGroups(
            DegenerateFixture,
            subscriptionId: null,
            out _,
            out var rowCount);

        Assert.Equal(3, rowCount);
        var only = Assert.Single(groups);
        Assert.Equal("rg-ok", only.Name);
        Assert.Equal("11111111-1111-1111-1111-111111111111", only.SubscriptionExternalId);
        Assert.DoesNotContain(groups, g => g.Name == "orphan-no-sub");
    }

    [Fact]
    public void MapResourceGroups_ReadsNextLink()
    {
        var groups = AzureResourceGroupMapper.MapResourceGroups(
            NextLinkFixture,
            subscriptionId: "11111111-1111-1111-1111-111111111111",
            out var nextLink,
            out var rowCount);

        Assert.Equal(1, rowCount);
        Assert.Equal("rg-masri-prod", Assert.Single(groups).Name);
        Assert.Equal(
            "https://management.azure.com/subscriptions/11111111-1111-1111-1111-111111111111/resourcegroups?api-version=2021-04-01&$skiptoken=abc",
            nextLink);
    }

    [Fact]
    public void MapResourceGroups_ToolError_Throws()
    {
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            error = new { code = -32000, message = "resource group list failed" },
        });

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            AzureResourceGroupMapper.MapResourceGroups(body, subscriptionId: "11111111-1111-1111-1111-111111111111", out _, out _);
        });
        Assert.Contains("resource group list failed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_RequiresSubscriptionId()
    {
        Assert.Throws<ArgumentException>(() => AzureResourceGroupMapper.BuildArgumentsJson(subscriptionId: ""));
        Assert.Throws<ArgumentException>(() => AzureResourceGroupMapper.BuildArgumentsJson(subscriptionId: "   "));
    }

    [Fact]
    public void BuildArgumentsJson_PassesSubscriptionIdAndDefaultMaxItems_OmitsOptional()
    {
        var args = AzureResourceGroupMapper.BuildArgumentsJson("11111111-1111-1111-1111-111111111111");
        Assert.Contains("\"subscriptionId\":\"11111111-1111-1111-1111-111111111111\"", args, StringComparison.Ordinal);
        Assert.Contains("\"maxItems\":1000", args, StringComparison.Ordinal);
        Assert.DoesNotContain("entraTenant", args, StringComparison.Ordinal);
        Assert.DoesNotContain("filter", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_ClampsMaxItemsAndPassesOptionalArgs()
    {
        var low = AzureResourceGroupMapper.BuildArgumentsJson(
            "11111111-1111-1111-1111-111111111111",
            entraTenant: "contoso.onmicrosoft.com",
            filter: "tagName eq 'environment' and tagValue eq 'production'",
            maxItems: 0);
        Assert.Contains("\"maxItems\":1", low, StringComparison.Ordinal);
        Assert.Contains("\"entraTenant\":\"contoso.onmicrosoft.com\"", low, StringComparison.Ordinal);
        using (var doc = JsonDocument.Parse(low))
        {
            Assert.Equal(
                "tagName eq 'environment' and tagValue eq 'production'",
                doc.RootElement.GetProperty("filter").GetString());
        }

        var high = AzureResourceGroupMapper.BuildArgumentsJson(
            "11111111-1111-1111-1111-111111111111",
            maxItems: 5000);
        Assert.Contains("\"maxItems\":1000", high, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_CallsAzureListResourceGroups_WithSubscriptionId()
    {
        var mcp = new ScriptedMcp(CompactResourceGroupListFixture);
        var serverId = Guid.NewGuid();

        var groups = await AzureResourceGroupMapper.PullAsync(
            mcp,
            serverId,
            subscriptionId: "11111111-1111-1111-1111-111111111111");

        Assert.Equal(2, groups.Count);
        Assert.Equal("rg-masri-prod", groups[0].Name);
        var call = Assert.Single(mcp.Calls);
        Assert.Equal(AzureResourceGroupMapper.ToolName, call.Tool);
        Assert.Equal(serverId, call.ServerId);
        Assert.Contains("\"subscriptionId\":\"11111111-1111-1111-1111-111111111111\"", call.Args, StringComparison.Ordinal);
        Assert.Contains("\"maxItems\":1000", call.Args, StringComparison.Ordinal);
        Assert.DoesNotContain("entraTenant", call.Args, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_RequiresSubscriptionId()
    {
        var mcp = new ScriptedMcp(CompactResourceGroupListFixture);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            AzureResourceGroupMapper.PullAsync(mcp, Guid.NewGuid(), subscriptionId: ""));
        Assert.Empty(mcp.Calls);
    }

    private sealed class ScriptedMcp : IMcpClient
    {
        private readonly string _inner;
        public List<(Guid ServerId, string Tool, string? Args)> Calls { get; } = [];

        public ScriptedMcp(string inner) => _inner = inner;

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((mcpServerId, toolName, argumentsJson));
            var body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "1",
                result = new { content = new[] { new { type = "text", text = _inner } } },
            });
            return Task.FromResult(body);
        }
    }
}
