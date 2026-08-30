using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class HaloUserMapperTests
{
    // Compact halo_list_users wrapper. Field names match Halo Users list passthrough
    // (id, name, emailaddress, client_id, site_id, inactive). Values are fixtures, not a live capture.
    public const string CompactListFixture = """
        {
          "page_no": 1,
          "page_size": 2,
          "record_count": 80,
          "users": [
            {
              "id": 41,
              "name": "James Masri",
              "emailaddress": "james@example.com",
              "client_id": 12,
              "site_id": 3,
              "inactive": false
            },
            {
              "id": 88,
              "name": "Inactive User",
              "emailaddress": "gone@example.com",
              "client_id": 29,
              "site_id": 4,
              "inactive": true
            }
          ]
        }
        """;

    // Two raw rows (one empty name), full page. Mapped count is 1; raw count is 2.
    public const string NextPageContinuesFixture = """
        {
          "page_no": 1,
          "page_size": 2,
          "record_count": 3,
          "users": [
            {
              "id": 41,
              "name": "James Masri",
              "emailaddress": "james@example.com",
              "client_id": 12,
              "site_id": 3,
              "inactive": false
            },
            {
              "id": 99,
              "name": "",
              "emailaddress": "skip@example.com",
              "client_id": 12,
              "inactive": false
            }
          ]
        }
        """;

    public const string EmptyUsersFixture = """
        {
          "page_no": 2,
          "page_size": 2,
          "record_count": 80,
          "users": []
        }
        """;

    [Fact]
    public void MapUsers_CompactList_MapsIdClientEmailSiteAndInactive()
    {
        var contacts = HaloUserMapper.MapUsers(CompactListFixture, out var rowCount);

        Assert.Equal(2, rowCount);
        Assert.Equal(2, contacts.Count);

        var james = contacts[0];
        Assert.Equal("41", james.ExternalId);
        Assert.Equal("12", james.ClientExternalId);
        Assert.Equal("James Masri", james.Name);
        Assert.Equal("james@example.com", james.Email);
        Assert.Equal("3", james.SiteExternalId);
        Assert.False(james.IsInactive);

        var inactive = contacts[1];
        Assert.Equal("88", inactive.ExternalId);
        Assert.Equal("29", inactive.ClientExternalId);
        Assert.Equal("Inactive User", inactive.Name);
        Assert.Equal("gone@example.com", inactive.Email);
        Assert.Equal("4", inactive.SiteExternalId);
        Assert.True(inactive.IsInactive);
    }

    [Fact]
    public void MapUsers_Skips_Missing_Id_Or_Name()
    {
        const string json = """
            {
              "users": [
                { "id": "", "name": "No Id", "emailaddress": "x@example.com", "client_id": 12, "inactive": false },
                { "id": 99, "name": "", "emailaddress": "y@example.com", "client_id": 12, "inactive": false },
                { "name": "No Id Key", "emailaddress": "z@example.com", "client_id": 12, "inactive": false },
                { "id": 41, "name": "James Masri", "emailaddress": "james@example.com", "client_id": 12, "site_id": 3, "inactive": false }
              ]
            }
            """;

        var james = Assert.Single(HaloUserMapper.MapUsers(json, out var rowCount));
        Assert.Equal(4, rowCount);
        Assert.Equal("41", james.ExternalId);
        Assert.Equal("James Masri", james.Name);
        Assert.DoesNotContain(HaloUserMapper.MapUsers(json), c => c.ExternalId == "99");
    }

    [Fact]
    public void MapUsers_EmptyEmail_IsNull_MissingSite_IsNull()
    {
        const string json = """
            {
              "users": [
                {
                  "id": 41,
                  "name": "James Masri",
                  "emailaddress": "",
                  "client_id": 12,
                  "inactive": false
                }
              ]
            }
            """;

        var james = Assert.Single(HaloUserMapper.MapUsers(json));
        Assert.Null(james.Email);
        Assert.Null(james.SiteExternalId);
        Assert.Equal("12", james.ClientExternalId);
    }

    [Fact]
    public void MapUsers_JsonRpcContentText_UnwrapsToUsers()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = CompactListFixture } } },
        });

        var contacts = HaloUserMapper.MapUsers(wrapped);
        Assert.Equal(2, contacts.Count);
        Assert.Equal("41", contacts[0].ExternalId);
        Assert.Equal("James Masri", contacts[0].Name);
        Assert.Equal("james@example.com", contacts[0].Email);
        Assert.False(contacts[0].IsInactive);
    }

    [Fact]
    public void MapUsers_ToolError_Throws()
    {
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            error = new { code = -32000, message = "halo auth expired" },
        });

        var ex = Assert.Throws<InvalidOperationException>(() => { HaloUserMapper.MapUsers(body); });
        Assert.Contains("halo auth expired", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_FirstPage_IncludeInactiveTrue_OmitsClientId()
    {
        var args = HaloUserMapper.BuildArgumentsJson(pageNo: 1);
        Assert.Contains("\"pageNo\":1", args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":50", args, StringComparison.Ordinal);
        Assert.Contains("\"includeInactive\":true", args, StringComparison.Ordinal);
        Assert.DoesNotContain("clientId", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_ClampsPageSizeToMax200_AndPageNoTo1()
    {
        var args = HaloUserMapper.BuildArgumentsJson(pageNo: 0, pageSize: 20000, clientId: 12);
        Assert.Contains("\"pageNo\":1", args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":200", args, StringComparison.Ordinal);
        Assert.Contains("\"includeInactive\":true", args, StringComparison.Ordinal);
        Assert.Contains("\"clientId\":12", args, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_EmptyPage_StopsPaging_AlwaysIncludeInactive()
    {
        var mcp = new ScriptedMcp([CompactListFixture, EmptyUsersFixture]);
        var contacts = await HaloUserMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 2);

        Assert.Equal(2, contacts.Count);
        Assert.Equal("41", contacts[0].ExternalId);
        Assert.Equal("James Masri", contacts[0].Name);
        Assert.Equal(2, mcp.Calls.Count);
        Assert.All(mcp.Calls, c => Assert.Equal(HaloUserMapper.ToolName, c.Tool));
        Assert.Contains("\"pageNo\":1", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":2", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"includeInactive\":true", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageNo\":2", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("halo_get_user", mcp.Calls.Select(c => c.Tool));
        Assert.DoesNotContain("halo_search_users", mcp.Calls.Select(c => c.Tool));
    }

    [Fact]
    public async Task PullAsync_ContinuesOnRawCount_NotMappedCount()
    {
        var mcp = new ScriptedMcp([NextPageContinuesFixture, EmptyUsersFixture]);
        var contacts = await HaloUserMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 2, clientId: 12);

        var james = Assert.Single(contacts);
        Assert.Equal("James Masri", james.Name);
        Assert.Equal("41", james.ExternalId);
        Assert.Equal(2, mcp.Calls.Count);
        Assert.All(mcp.Calls, c => Assert.Equal(HaloUserMapper.ToolName, c.Tool));
        Assert.Contains("\"pageNo\":1", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":2", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"includeInactive\":true", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"clientId\":12", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageNo\":2", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.Contains("\"clientId\":12", mcp.Calls[1].Args, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_ShortPage_StopsWithoutSecondCall()
    {
        var mcp = new ScriptedMcp([CompactListFixture, EmptyUsersFixture]);
        var contacts = await HaloUserMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 50);

        Assert.Equal(2, contacts.Count);
        Assert.Single(mcp.Calls);
        Assert.Contains("\"pageSize\":50", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"includeInactive\":true", mcp.Calls[0].Args, StringComparison.Ordinal);
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
            var inner = _pages.Count > 0 ? _pages.Dequeue() : EmptyUsersFixture;
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
