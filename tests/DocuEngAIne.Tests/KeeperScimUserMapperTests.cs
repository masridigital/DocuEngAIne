using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class KeeperScimUserMapperTests
{
    // Compact-shaped keeper_scim_list_users ListResponse (no live capture).
    // Resources[] is the list; startIndex is 1-based; count max 500.
    // id is the SCIM user id — not a vault record UID.
    public const string CompactListFixture = """
        {"schemas":["urn:ietf:params:scim:api:messages:2.0:ListResponse"],"totalResults":2,"startIndex":1,"itemsPerPage":2,"Resources":[{"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],"id":"184201","userName":"james@adroccap.com","displayName":"James Adroc","active":true,"name":{"givenName":"James","familyName":"Adroc"}},{"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],"id":"184202","userName":"robots@masri.tech","displayName":"Robots Masri","active":true}]}
        """;

    // Two raw rows (one empty userName/displayName), full page, more results exist.
    public const string NextPageContinuesFixture = """
        {"schemas":["urn:ietf:params:scim:api:messages:2.0:ListResponse"],"totalResults":4,"startIndex":1,"itemsPerPage":2,"Resources":[{"id":"184201","userName":"james@adroccap.com","displayName":"James Adroc","active":true},{"id":"skip-1","userName":"","displayName":"","active":true}]}
        """;

    public const string SecondPageFixture = """
        {"schemas":["urn:ietf:params:scim:api:messages:2.0:ListResponse"],"totalResults":4,"startIndex":3,"itemsPerPage":2,"Resources":[{"id":"184202","userName":"robots@masri.tech","displayName":"Robots Masri","active":true},{"id":"184203","userName":"it@masri.tech","displayName":"IT Masri","active":false}]}
        """;

    public const string EmptyResourcesFixture = """
        {"schemas":["urn:ietf:params:scim:api:messages:2.0:ListResponse"],"totalResults":4,"startIndex":5,"itemsPerPage":0,"Resources":[]}
        """;

    [Fact]
    public void MapUsers_CompactList_MapsScimIdAndUserNameHint_KeeperRecordUrlNull()
    {
        var links = KeeperScimUserMapper.MapUsers(CompactListFixture, out var startIndex, out var totalResults, out var rowCount);

        Assert.Equal(2, rowCount);
        Assert.Equal(1, startIndex);
        Assert.Equal(2, totalResults);
        Assert.Equal(2, links.Count);

        var james = links[0];
        Assert.Equal("184201", james.ExternalId);
        Assert.Equal("James Adroc", james.Name);
        Assert.Equal("james@adroccap.com", james.UsernameHint);
        Assert.Null(james.KeeperRecordUrl);

        var robots = links[1];
        Assert.Equal("184202", robots.ExternalId);
        Assert.Equal("Robots Masri", robots.Name);
        Assert.Equal("robots@masri.tech", robots.UsernameHint);
        Assert.Null(robots.KeeperRecordUrl);
    }

    [Fact]
    public void MapUsers_ExternalId_Is_ScimUserId_Not_VaultUid()
    {
        var james = Assert.Single(KeeperScimUserMapper.MapUsers(CompactListFixture), l => l.UsernameHint == "james@adroccap.com");

        // Compact has no vault record list. The mapped id is the SCIM user id.
        Assert.Equal("184201", james.ExternalId);
        Assert.Null(james.KeeperRecordUrl);
        Assert.Null(typeof(ExternalKeeperLinkDto).GetProperty("KeeperRecordUid"));
        Assert.DoesNotContain("vault", james.ExternalId, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MapUsers_FallsBackToUserName_For_Name_When_DisplayName_Missing()
    {
        const string json = """
            {"schemas":["urn:ietf:params:scim:api:messages:2.0:ListResponse"],"totalResults":1,"startIndex":1,"itemsPerPage":1,"Resources":[{"id":"184201","userName":"james@adroccap.com","active":true}]}
            """;

        var james = Assert.Single(KeeperScimUserMapper.MapUsers(json));
        Assert.Equal("james@adroccap.com", james.Name);
        Assert.Equal("james@adroccap.com", james.UsernameHint);
        Assert.Null(james.KeeperRecordUrl);
    }

    [Fact]
    public void MapUsers_SkipsEmptyIdOrName_StillCountsRawRows()
    {
        var links = KeeperScimUserMapper.MapUsers(NextPageContinuesFixture, out _, out _, out var rowCount);

        Assert.Equal(2, rowCount);
        var james = Assert.Single(links);
        Assert.Equal("184201", james.ExternalId);
        Assert.Equal("james@adroccap.com", james.UsernameHint);
        Assert.DoesNotContain(links, l => l.ExternalId == "skip-1");
    }

    [Fact]
    public void MapUsers_JsonRpcContentText_UnwrapsToListResponse()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = CompactListFixture } } },
        });

        var links = KeeperScimUserMapper.MapUsers(wrapped);
        Assert.Equal(2, links.Count);
        Assert.Equal("184201", links[0].ExternalId);
        Assert.Equal("james@adroccap.com", links[0].UsernameHint);
        Assert.Null(links[0].KeeperRecordUrl);
    }

    [Fact]
    public void MapUsers_ToolError_Throws()
    {
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            error = new { code = -32000, message = "keeper scim not configured" },
        });

        var ex = Assert.Throws<InvalidOperationException>(() => { KeeperScimUserMapper.MapUsers(body); });
        Assert.Contains("keeper scim not configured", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_FirstPage_StartIndex1_Count50()
    {
        var args = KeeperScimUserMapper.BuildArgumentsJson(startIndex: 1);
        Assert.Contains("\"startIndex\":1", args, StringComparison.Ordinal);
        Assert.Contains("\"count\":50", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_ClampsCountToMax500_AndStartIndexTo1()
    {
        var args = KeeperScimUserMapper.BuildArgumentsJson(startIndex: 0, count: 20000);
        Assert.Contains("\"startIndex\":1", args, StringComparison.Ordinal);
        Assert.Contains("\"count\":500", args, StringComparison.Ordinal);
        Assert.Equal(500, KeeperScimUserMapper.MaxPageSize);
    }

    [Fact]
    public void MapUsers_ListResponseMeta_VersusEmptyResources()
    {
        KeeperScimUserMapper.MapUsers(CompactListFixture, out var liveStart, out var liveTotal, out _);
        Assert.Equal(1, liveStart);
        Assert.Equal(2, liveTotal);

        KeeperScimUserMapper.MapUsers(EmptyResourcesFixture, out var emptyStart, out var emptyTotal, out var emptyCount);
        Assert.Equal(5, emptyStart);
        Assert.Equal(4, emptyTotal);
        Assert.Equal(0, emptyCount);

        const string missingMeta = """{"Resources":[{"id":"184201","userName":"james@adroccap.com","displayName":"James Adroc"}]}""";
        KeeperScimUserMapper.MapUsers(missingMeta, out var missingStart, out var missingTotal, out _);
        Assert.Null(missingStart);
        Assert.Null(missingTotal);
    }

    [Fact]
    public void Dto_And_Tool_Have_No_Password_Surface()
    {
        var type = typeof(ExternalKeeperLinkDto);
        Assert.Null(type.GetProperty("Password"));
        Assert.Null(type.GetProperty("Secret"));
        Assert.Null(type.GetProperty("EncryptedValue"));
        Assert.Null(type.GetProperty("KeeperRecordUid"));
        Assert.Equal(KeeperScimUserMapper.ToolName, "keeper_scim_list_users");
        Assert.DoesNotContain("password", KeeperScimUserMapper.ToolName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provision", KeeperScimUserMapper.ToolName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reveal", KeeperScimUserMapper.ToolName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PullAsync_LastPage_StopsPaging()
    {
        var mcp = new ScriptedMcp([CompactListFixture]);
        var links = await KeeperScimUserMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 2);

        Assert.Equal(2, links.Count);
        Assert.Equal("James Adroc", links[0].Name);
        Assert.Equal("james@adroccap.com", links[0].UsernameHint);
        Assert.Null(links[0].KeeperRecordUrl);
        Assert.Single(mcp.Calls);
        Assert.Equal(KeeperScimUserMapper.ToolName, mcp.Calls[0].Tool);
        Assert.Contains("\"startIndex\":1", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"count\":2", mcp.Calls[0].Args, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_TotalResults_ContinuesOnRawCount_NotMappedCount()
    {
        var mcp = new ScriptedMcp([NextPageContinuesFixture, SecondPageFixture]);
        var links = await KeeperScimUserMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 2);

        Assert.Equal(3, links.Count);
        Assert.Equal("184201", links[0].ExternalId);
        Assert.Equal("184202", links[1].ExternalId);
        Assert.Equal("184203", links[2].ExternalId);
        Assert.All(links, l => Assert.Null(l.KeeperRecordUrl));
        Assert.Equal(2, mcp.Calls.Count);
        Assert.All(mcp.Calls, c => Assert.Equal(KeeperScimUserMapper.ToolName, c.Tool));
        Assert.Contains("\"startIndex\":1", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"count\":2", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"startIndex\":3", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.DoesNotContain(mcp.Calls, c =>
            c.Tool.Contains("password", StringComparison.OrdinalIgnoreCase)
            || c.Tool.Contains("provision", StringComparison.OrdinalIgnoreCase)
            || c.Tool.Contains("reveal", StringComparison.OrdinalIgnoreCase));
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
            var inner = _pages.Count > 0 ? _pages.Dequeue() : EmptyResourcesFixture;
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
