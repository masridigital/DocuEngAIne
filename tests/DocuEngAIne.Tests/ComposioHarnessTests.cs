using System.Text.Json;
using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Mcp;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

/// <summary>
/// Composio is the second MCP harness (not a Compact PSA/RMM stand-in). Registration accepts
/// <see cref="McpServerKind.Composio"/> and fills the Connect URL. Toolkits are an allowlist —
/// github / cloudflare / outlook / notion — and ads/social are skipped. No live Composio calls.
/// </summary>
public class ComposioHarnessTests
{
    private static (DocuEngAIneDbContext Db, FakeCurrentUser User) Create()
    {
        var user = new FakeCurrentUser { TenantId = Guid.NewGuid(), ObjectId = Guid.NewGuid().ToString(), Role = UserRole.Owner };
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return (new DocuEngAIneDbContext(options, user), user);
    }

    private static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0;

    private static JsonElement BodyOf(IResult result, out JsonDocument document)
    {
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement;
    }

    [Fact]
    public async Task Create_McpServer_Accepts_Kind_Composio_And_Fills_The_Connect_Default()
    {
        var (db, user) = Create();
        await using (db)
        {
            var result = await IntegrationEndpoints.CreateMcpServerAsync(
                new CreateMcpServerRequest(McpServerDefaults.ComposioName, McpServerKind.Composio, AuthSecretName: "kv-composio"),
                db, user);

            Assert.Equal(StatusCodes.Status201Created, StatusOf(result));

            var body = BodyOf(result, out var document);
            using (document)
            {
                Assert.Equal("Composio", body.GetProperty("Kind").GetString());
                Assert.Equal(McpServerDefaults.ComposioEndpoint, body.GetProperty("EndpointUrl").GetString());
                Assert.Equal("https://connect.composio.dev/mcp", body.GetProperty("EndpointUrl").GetString());
                Assert.Equal("kv-composio", body.GetProperty("AuthSecretName").GetString());
                Assert.Equal("Http", body.GetProperty("Transport").GetString());
            }

            var stored = Assert.Single(await db.McpServers.ForTenant(user).ToListAsync());
            Assert.Equal(McpServerKind.Composio, stored.Kind);
            Assert.Equal(McpServerDefaults.ComposioEndpoint, stored.EndpointUrl);
            Assert.DoesNotContain("secret-value", stored.AuthSecretName, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ResolveEndpoint_Rewrites_Composio_Origin_Without_Mcp()
    {
        Assert.Equal(
            McpServerDefaults.ComposioEndpoint,
            McpServerDefaults.ResolveEndpoint(McpServerKind.Composio, "https://connect.composio.dev"));
        Assert.Equal(
            McpServerDefaults.ComposioEndpoint,
            McpServerDefaults.ResolveEndpoint(McpServerKind.Composio, null));
        Assert.Equal(
            McpServerDefaults.ComposioEndpoint,
            McpServerDefaults.EndpointFor(McpServerKind.Composio));
    }

    [Theory]
    [InlineData("github")]
    [InlineData("cloudflare")]
    [InlineData("outlook")]
    [InlineData("notion")]
    [InlineData("GitHub")]
    public void Allowed_Toolkits_Are_Accepted(string toolkit)
        => Assert.True(McpServerDefaults.IsAllowedComposioToolkit(toolkit));

    [Theory]
    [InlineData("googleads")]
    [InlineData("facebook")]
    [InlineData("instagram")]
    [InlineData("linkedin")]
    [InlineData("reddit")]
    public void Ads_And_Social_Toolkits_Are_Skipped(string toolkit)
    {
        Assert.True(McpServerDefaults.IsSkippedComposioToolkit(toolkit));
        Assert.False(McpServerDefaults.IsAllowedComposioToolkit(toolkit));
    }

    [Theory]
    [InlineData("GITHUB_LIST_REPOS", true)]
    [InlineData("CLOUDFLARE_LIST_ZONES", true)]
    [InlineData("OUTLOOK_LIST_MESSAGES", true)]
    [InlineData("NOTION_CREATE_A_PAGE", true)]
    [InlineData("github/list_repos", true)]
    [InlineData("FACEBOOK_CREATE_POST", false)]
    [InlineData("GOOGLEADS_CREATE_CAMPAIGN", false)]
    [InlineData("INSTAGRAM_CREATE_MEDIA", false)]
    [InlineData("LINKEDIN_CREATE_POST", false)]
    [InlineData("REDDIT_SUBMIT_POST", false)]
    [InlineData("COMPOSIO_MULTI_EXECUTE_TOOL", false)]
    [InlineData("twitter_create_tweet", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Tool_Names_Follow_The_Toolkit_Allowlist(string? toolName, bool allowed)
        => Assert.Equal(allowed, McpServerDefaults.IsAllowedComposioTool(toolName));

    [Fact]
    public void Tools_List_Drops_Ads_Social_And_Keeps_Allowed()
    {
        var raw = """
            {"jsonrpc":"2.0","id":"1","result":{"tools":[
              {"name":"GITHUB_LIST_REPOS","description":"list repos"},
              {"name":"NOTION_SEARCH","description":"search"},
              {"name":"FACEBOOK_CREATE_POST","description":"post"},
              {"name":"GOOGLEADS_CREATE_CAMPAIGN","description":"ads"},
              {"name":"LINKEDIN_CREATE_POST","description":"social"},
              {"name":"COMPOSIO_MULTI_EXECUTE_TOOL","description":"bypass"}
            ]}}
            """;

        using var doc = JsonDocument.Parse(McpServerDefaults.FilterComposioToolsList(raw));
        var names = doc.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray()
            .Select(t => t.GetProperty("name").GetString() ?? "")
            .ToArray();

        Assert.Equal(["GITHUB_LIST_REPOS", "NOTION_SEARCH"], names);
    }
}
