using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class KeeperMspAccountMapperTests
{
    // Compact-shaped keeper_msp_list_accounts fixture (no live capture). Field names from Compact docs:
    // vendorInternalId (MSP's own id), partnerId (Keeper's internal id), name. No pagination.
    public const string CompactListFixture = """
        [{"vendorInternalId":"halo-100","partnerId":"1842","name":"Adroc Capital","status":"ACTIVE"},{"vendorInternalId":"masri-1","partnerId":"1843","name":"Masri Digital","status":"TRIAL"}]
        """;

    // partnerId-only row plus skips: empty name, empty ids.
    public const string MappingFixture = """
        [{"partnerId":"1842","name":"Adroc Capital"},{"vendorInternalId":"","partnerId":"","name":"No Id"},{"vendorInternalId":"skip-1","partnerId":"9","name":""},{"vendorInternalId":"masri-1","partnerId":"1843","name":"Masri Digital"}]
        """;

    [Fact]
    public void MapAccounts_CompactList_MapsVendorInternalIdAndName_KeeperRecordUrlNull()
    {
        var links = KeeperMspAccountMapper.MapAccounts(CompactListFixture);

        Assert.Equal(2, links.Count);

        var adroc = links[0];
        Assert.Equal("halo-100", adroc.ExternalId);
        Assert.Equal("Adroc Capital", adroc.Name);
        Assert.Null(adroc.UsernameHint);
        Assert.Null(adroc.KeeperRecordUrl);

        var masri = links[1];
        Assert.Equal("masri-1", masri.ExternalId);
        Assert.Equal("Masri Digital", masri.Name);
        Assert.Null(masri.KeeperRecordUrl);
    }

    [Fact]
    public void MapAccounts_PrefersVendorInternalId_FallsBackToPartnerId_SkipsEmpty()
    {
        var links = KeeperMspAccountMapper.MapAccounts(MappingFixture);

        Assert.Equal(2, links.Count);
        Assert.Equal("1842", links[0].ExternalId);
        Assert.Equal("Adroc Capital", links[0].Name);
        Assert.Equal("masri-1", links[1].ExternalId);
        Assert.DoesNotContain(links, l => l.Name == "No Id");
        Assert.All(links, l => Assert.Null(l.KeeperRecordUrl));
    }

    [Fact]
    public void MapAccounts_AccountsWrapper_UnwrapsArray()
    {
        const string wrapped = """{"accounts":[{"vendorInternalId":"halo-100","partnerId":"1842","name":"Adroc Capital"}]}""";

        var adroc = Assert.Single(KeeperMspAccountMapper.MapAccounts(wrapped));
        Assert.Equal("halo-100", adroc.ExternalId);
        Assert.Equal("Adroc Capital", adroc.Name);
        Assert.Null(adroc.KeeperRecordUrl);
    }

    [Fact]
    public void MapAccounts_JsonRpcContentTextArray_UnwrapsToAccountList()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = CompactListFixture } } },
        });

        var links = KeeperMspAccountMapper.MapAccounts(wrapped);
        Assert.Equal(2, links.Count);
        Assert.Equal("halo-100", links[0].ExternalId);
        Assert.Equal("Adroc Capital", links[0].Name);
        Assert.Null(links[0].KeeperRecordUrl);
    }

    [Fact]
    public void MapAccounts_ToolError_Throws()
    {
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            error = new { code = -32000, message = "keeper msp not configured" },
        });

        var ex = Assert.Throws<InvalidOperationException>(() => { KeeperMspAccountMapper.MapAccounts(body); });
        Assert.Contains("keeper msp not configured", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dto_Has_No_Secret_Or_VaultUid_Properties()
    {
        var type = typeof(ExternalKeeperLinkDto);
        Assert.Null(type.GetProperty("Password"));
        Assert.Null(type.GetProperty("Secret"));
        Assert.Null(type.GetProperty("EncryptedValue"));
        Assert.Null(type.GetProperty("KeeperRecordUid"));
        Assert.Equal(KeeperMspAccountMapper.ToolName, "keeper_msp_list_accounts");
        Assert.DoesNotContain("password", KeeperMspAccountMapper.ToolName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provision", KeeperMspAccountMapper.ToolName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reveal", KeeperMspAccountMapper.ToolName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PullAsync_Calls_KeeperMspListAccounts_With_No_Arguments()
    {
        var mcp = new ScriptedMcp();
        var links = await KeeperMspAccountMapper.PullAsync(mcp, Guid.NewGuid());

        Assert.Equal(2, links.Count);
        Assert.Equal("Adroc Capital", links[0].Name);
        Assert.Equal("halo-100", links[0].ExternalId);
        Assert.Null(links[0].KeeperRecordUrl);
        var call = Assert.Single(mcp.Calls);
        Assert.Equal(KeeperMspAccountMapper.ToolName, call.Tool);
        Assert.True(string.IsNullOrWhiteSpace(call.Args));
        Assert.DoesNotContain(mcp.Calls, c =>
            c.Tool.Contains("password", StringComparison.OrdinalIgnoreCase)
            || c.Tool.Contains("provision", StringComparison.OrdinalIgnoreCase)
            || c.Tool.Contains("reveal", StringComparison.OrdinalIgnoreCase));
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
                result = new { content = new[] { new { type = "text", text = CompactListFixture } } },
            });
            return Task.FromResult(body);
        }
    }
}
