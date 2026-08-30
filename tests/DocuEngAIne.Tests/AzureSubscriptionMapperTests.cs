using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class AzureSubscriptionMapperTests
{
    // Hand-built ARM {value:[...]} envelope — Compact azure_list_subscriptions field names
    // (subscriptionId, displayName, state, tenantId). No live Azure/Compact capture.
    public const string CompactSubscriptionListFixture = """
        {"value":[{"id":"/subscriptions/11111111-1111-1111-1111-111111111111","subscriptionId":"11111111-1111-1111-1111-111111111111","tenantId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","displayName":"Masri Digital","state":"Enabled","subscriptionPolicies":{"locationPlacementId":"Public_2014-09-01","quotaId":"PayAsYouGo_2014-09-01","spendingLimit":"Off"},"authorizationSource":"RoleBased"},{"id":"/subscriptions/22222222-2222-2222-2222-222222222222","subscriptionId":"22222222-2222-2222-2222-222222222222","tenantId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","displayName":"Adroc Capital","state":"Warned"},{"id":"/subscriptions/33333333-3333-3333-3333-333333333333","subscriptionId":"33333333-3333-3333-3333-333333333333","tenantId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","displayName":"Past Due Co","state":"PastDue"},{"id":"/subscriptions/44444444-4444-4444-4444-444444444444","subscriptionId":"44444444-4444-4444-4444-444444444444","tenantId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","displayName":"Disabled Sub","state":"Disabled"},{"id":"/subscriptions/55555555-5555-5555-5555-555555555555","subscriptionId":"55555555-5555-5555-5555-555555555555","tenantId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","displayName":"Deleted Sub","state":"Deleted"}]}
        """;

    public const string NextLinkFixture = """
        {"value":[{"subscriptionId":"11111111-1111-1111-1111-111111111111","displayName":"Masri Digital","state":"Enabled","tenantId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}],"nextLink":"https://management.azure.com/subscriptions?api-version=2022-12-01&$skiptoken=abc"}
        """;

    private const string DegenerateFixture = """
        {"value":[{"displayName":"No Id","state":"Enabled"},{"subscriptionId":"99999999-9999-9999-9999-999999999999","displayName":"","state":"Enabled"},{"subscriptionId":"11111111-1111-1111-1111-111111111111","displayName":"Masri Digital","state":"Enabled"}]}
        """;

    [Fact]
    public void MapSubscriptions_ArmValueEnvelope_MapsEnabledWarnedPastDue_SkipsDisabledDeleted()
    {
        var subscriptions = AzureSubscriptionMapper.MapSubscriptions(CompactSubscriptionListFixture, out var nextLink, out var rowCount);

        Assert.Equal(5, rowCount);
        Assert.Null(nextLink);
        Assert.Equal(3, subscriptions.Count);

        var masri = subscriptions[0];
        Assert.Equal("11111111-1111-1111-1111-111111111111", masri.ExternalId);
        Assert.Equal("Masri Digital", masri.Name);
        Assert.Equal("Enabled", masri.State);
        Assert.Equal("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", masri.TenantId);
        Assert.False(masri.IsInactive);

        var warned = Assert.Single(subscriptions, s => s.Name == "Adroc Capital");
        Assert.Equal("22222222-2222-2222-2222-222222222222", warned.ExternalId);
        Assert.Equal("Warned", warned.State);
        Assert.True(warned.IsInactive);

        var pastDue = Assert.Single(subscriptions, s => s.Name == "Past Due Co");
        Assert.Equal("33333333-3333-3333-3333-333333333333", pastDue.ExternalId);
        Assert.Equal("PastDue", pastDue.State);
        Assert.True(pastDue.IsInactive);

        Assert.DoesNotContain(subscriptions, s => s.Name == "Disabled Sub");
        Assert.DoesNotContain(subscriptions, s => s.ExternalId == "44444444-4444-4444-4444-444444444444");
        Assert.DoesNotContain(subscriptions, s => s.Name == "Deleted Sub");
        Assert.DoesNotContain(subscriptions, s => s.ExternalId == "55555555-5555-5555-5555-555555555555");
        Assert.DoesNotContain(subscriptions, s => s.State == "Disabled");
        Assert.DoesNotContain(subscriptions, s => s.State == "Deleted");
    }

    [Fact]
    public void MapSubscriptions_JsonRpcContentText_UnwrapsArmEnvelope()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = CompactSubscriptionListFixture } } },
        });

        var subscriptions = AzureSubscriptionMapper.MapSubscriptions(wrapped);
        Assert.Equal(3, subscriptions.Count);
        Assert.Equal("11111111-1111-1111-1111-111111111111", subscriptions[0].ExternalId);
        Assert.Equal("Masri Digital", subscriptions[0].Name);
        Assert.Equal("Enabled", subscriptions[0].State);
        Assert.False(subscriptions[0].IsInactive);
    }

    [Fact]
    public void MapSubscriptions_BareArray_StillMaps()
    {
        const string json = """
            [{"subscriptionId":"11111111-1111-1111-1111-111111111111","displayName":"Masri Digital","state":"Enabled"}]
            """;

        var only = Assert.Single(AzureSubscriptionMapper.MapSubscriptions(json));
        Assert.Equal("11111111-1111-1111-1111-111111111111", only.ExternalId);
        Assert.Equal("Masri Digital", only.Name);
        Assert.False(only.IsInactive);
    }

    [Fact]
    public void MapSubscriptions_ArmIdOnly_ExtractsSubscriptionId()
    {
        const string json = """
            {"value":[{"id":"/subscriptions/11111111-1111-1111-1111-111111111111","displayName":"Masri Digital","state":"Enabled"}]}
            """;

        var only = Assert.Single(AzureSubscriptionMapper.MapSubscriptions(json));
        Assert.Equal("11111111-1111-1111-1111-111111111111", only.ExternalId);
        Assert.Equal("Masri Digital", only.Name);
    }

    [Fact]
    public void MapSubscriptions_SkipsMissingIdOrName_ButCountsRawRows()
    {
        var subscriptions = AzureSubscriptionMapper.MapSubscriptions(DegenerateFixture, out _, out var rowCount);

        Assert.Equal(3, rowCount);
        var only = Assert.Single(subscriptions);
        Assert.Equal("Masri Digital", only.Name);
        Assert.Equal("11111111-1111-1111-1111-111111111111", only.ExternalId);
    }

    [Fact]
    public void MapSubscriptions_ReadsNextLink()
    {
        var subscriptions = AzureSubscriptionMapper.MapSubscriptions(NextLinkFixture, out var nextLink, out var rowCount);

        Assert.Equal(1, rowCount);
        Assert.Equal("Masri Digital", Assert.Single(subscriptions).Name);
        Assert.Equal("https://management.azure.com/subscriptions?api-version=2022-12-01&$skiptoken=abc", nextLink);
    }

    [Fact]
    public void MapSubscriptions_ToolError_Throws()
    {
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            error = new { code = -32000, message = "azure auth expired" },
        });

        var ex = Assert.Throws<InvalidOperationException>(() => { AzureSubscriptionMapper.MapSubscriptions(body); });
        Assert.Contains("azure auth expired", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MapSubscriptions_IsErrorContent_Throws()
    {
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { isError = true, content = new[] { new { type = "text", text = "subscription list failed" } } },
        });

        var ex = Assert.Throws<InvalidOperationException>(() => { AzureSubscriptionMapper.MapSubscriptions(body); });
        Assert.Contains("subscription list failed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_OmitsEntraTenantWhenUnset()
    {
        var args = AzureSubscriptionMapper.BuildArgumentsJson();
        Assert.Equal("{}", args);
        Assert.DoesNotContain("entraTenant", args, StringComparison.Ordinal);
        Assert.DoesNotContain("pageSize", args, StringComparison.Ordinal);
        Assert.DoesNotContain("subscriptionId", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_PassesEntraTenant()
    {
        var args = AzureSubscriptionMapper.BuildArgumentsJson(entraTenant: "contoso.onmicrosoft.com");
        Assert.Contains("\"entraTenant\":\"contoso.onmicrosoft.com\"", args, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_CallsAzureListSubscriptions_WithEmptyArgs()
    {
        var mcp = new ScriptedMcp(CompactSubscriptionListFixture);
        var serverId = Guid.NewGuid();

        var subscriptions = await AzureSubscriptionMapper.PullAsync(mcp, serverId);

        Assert.Equal(3, subscriptions.Count);
        Assert.Equal("Masri Digital", subscriptions[0].Name);
        var call = Assert.Single(mcp.Calls);
        Assert.Equal(AzureSubscriptionMapper.ToolName, call.Tool);
        Assert.Equal(serverId, call.ServerId);
        Assert.Equal("{}", call.Args);
    }

    [Fact]
    public async Task PullAsync_PassesEntraTenant()
    {
        var mcp = new ScriptedMcp(CompactSubscriptionListFixture);
        await AzureSubscriptionMapper.PullAsync(mcp, Guid.NewGuid(), entraTenant: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        var call = Assert.Single(mcp.Calls);
        Assert.Equal(AzureSubscriptionMapper.ToolName, call.Tool);
        Assert.Contains("\"entraTenant\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\"", call.Args, StringComparison.Ordinal);
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
