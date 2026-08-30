using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class Action1OrganizationMapperTests
{
    // Live Compact action1_list_organizations ResultPage (pageSize 5, admin true). Field names exact.
    // total_items and limit are strings. next_page is an empty STRING when done, not a missing key.
    public const string LiveCompactListFixture = """
        {"id":"1","type":"ResultPage","items":[{"id":"4702a030-5f67-11f0-9cb3-e3f0bda36034","type":"Organization","name":"Adroc Capital","description":"","enterprise_id":"4fa9a577-6ec2-46a3-b3c4-144099fc4ab4"},{"id":"4fa9a577-6ec2-46a3-b3c4-144099fc4ab4","type":"Organization","name":"Masri Digital","description":"Default organization","enterprise_id":"4fa9a577-6ec2-46a3-b3c4-144099fc4ab4"}],"total_items":"5","limit":"5","next_page":"","prev_page":""}
        """;

    // Mapping fixture: one normal org, one default (id==enterprise_id), one empty-name skip.
    public const string MappingFixture = """
        {"id":"1","type":"ResultPage","items":[{"id":"4702a030-5f67-11f0-9cb3-e3f0bda36034","type":"Organization","name":"Adroc Capital","description":"","enterprise_id":"4fa9a577-6ec2-46a3-b3c4-144099fc4ab4"},{"id":"4fa9a577-6ec2-46a3-b3c4-144099fc4ab4","type":"Organization","name":"Masri Digital","description":"Default organization","enterprise_id":"4fa9a577-6ec2-46a3-b3c4-144099fc4ab4"},{"id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","type":"Organization","name":"","description":"","enterprise_id":"4fa9a577-6ec2-46a3-b3c4-144099fc4ab4"}],"total_items":"3","limit":"3","next_page":"","prev_page":""}
        """;

    // Same three rows, but next_page is an object with from (integer) — more pages exist.
    public const string NextPageFromFixture = """
        {"id":"1","type":"ResultPage","items":[{"id":"4702a030-5f67-11f0-9cb3-e3f0bda36034","type":"Organization","name":"Adroc Capital","description":"","enterprise_id":"4fa9a577-6ec2-46a3-b3c4-144099fc4ab4"},{"id":"4fa9a577-6ec2-46a3-b3c4-144099fc4ab4","type":"Organization","name":"Masri Digital","description":"Default organization","enterprise_id":"4fa9a577-6ec2-46a3-b3c4-144099fc4ab4"},{"id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","type":"Organization","name":"","description":"","enterprise_id":"4fa9a577-6ec2-46a3-b3c4-144099fc4ab4"}],"total_items":"5","limit":"3","next_page":{"from":3},"prev_page":""}
        """;

    public const string NextPageEmptyFixture = """
        {"id":"1","type":"ResultPage","items":[],"total_items":"5","limit":"3","next_page":"","prev_page":""}
        """;

    [Fact]
    public void MapOrganizations_LiveCompactList_MapsAdrocFromItemsIdName_SkipsDefault_DoesNotInventInactive()
    {
        var companies = Action1OrganizationMapper.MapOrganizations(LiveCompactListFixture, out var nextFrom, out var rowCount);

        Assert.Equal(2, rowCount);
        Assert.Null(nextFrom);
        var adroc = Assert.Single(companies);
        Assert.Equal("4702a030-5f67-11f0-9cb3-e3f0bda36034", adroc.ExternalId);
        Assert.Equal("Adroc Capital", adroc.Name);
        Assert.Null(adroc.IsInactive);
        Assert.Null(adroc.Slug);
        Assert.Null(adroc.PrimaryDomain);
        Assert.Null(adroc.Website);
        Assert.Null(adroc.City);
        Assert.Null(adroc.State);
        Assert.Null(adroc.Address);

        Assert.DoesNotContain(companies, c => c.Name == "Masri Digital");
        Assert.DoesNotContain(companies, c => c.ExternalId == "4fa9a577-6ec2-46a3-b3c4-144099fc4ab4");
    }

    [Fact]
    public void MapOrganizations_SkipsDefaultOrg_ByIdEqualsEnterpriseId_AndEmptyName()
    {
        var companies = Action1OrganizationMapper.MapOrganizations(MappingFixture, out _, out var rowCount);

        Assert.Equal(3, rowCount);
        var adroc = Assert.Single(companies);
        Assert.Equal("4702a030-5f67-11f0-9cb3-e3f0bda36034", adroc.ExternalId);
        Assert.Equal("Adroc Capital", adroc.Name);
        Assert.DoesNotContain(companies, c => c.Name == "Masri Digital");
        Assert.DoesNotContain(companies, c => c.ExternalId == "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    }

    [Fact]
    public void MapOrganizations_JsonRpcContentText_UnwrapsToResultPage()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = LiveCompactListFixture } } },
        });

        var companies = Action1OrganizationMapper.MapOrganizations(wrapped);
        var adroc = Assert.Single(companies);
        Assert.Equal("4702a030-5f67-11f0-9cb3-e3f0bda36034", adroc.ExternalId);
        Assert.Equal("Adroc Capital", adroc.Name);
        Assert.Null(adroc.IsInactive);
    }

    [Fact]
    public void BuildArgumentsJson_FirstPage_AdminTruePageSize50_OmitsFrom()
    {
        var args = Action1OrganizationMapper.BuildArgumentsJson(from: null);
        Assert.Contains("\"admin\":true", args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":50", args, StringComparison.Ordinal);
        Assert.DoesNotContain("from", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_ClampsPageSizeToMax100()
    {
        var args = Action1OrganizationMapper.BuildArgumentsJson(from: null, pageSize: 200);
        Assert.Contains("\"pageSize\":100", args, StringComparison.Ordinal);
        Assert.Contains("\"admin\":true", args, StringComparison.Ordinal);
    }

    [Fact]
    public void MapOrganizations_NextPageFromObject_VersusEmptyStringEnd()
    {
        Action1OrganizationMapper.MapOrganizations(NextPageFromFixture, out var from, out var rowCount);
        Assert.Equal(3, rowCount);
        Assert.Equal(3, from);
        var next = Action1OrganizationMapper.BuildArgumentsJson(from);
        Assert.Contains("\"from\":3", next, StringComparison.Ordinal);
        Assert.Contains("\"admin\":true", next, StringComparison.Ordinal);

        Action1OrganizationMapper.MapOrganizations(LiveCompactListFixture, out var emptyEnd, out _);
        Assert.Null(emptyEnd);

        Action1OrganizationMapper.MapOrganizations(NextPageEmptyFixture, out var emptyPage, out var emptyCount);
        Assert.Null(emptyPage);
        Assert.Equal(0, emptyCount);

        const string missingNext = """{"id":"1","type":"ResultPage","items":[]}""";
        Action1OrganizationMapper.MapOrganizations(missingNext, out var missing, out _);
        Assert.Null(missing);

        const string nullNext = """{"id":"1","type":"ResultPage","items":[],"next_page":null}""";
        Action1OrganizationMapper.MapOrganizations(nullNext, out var nulled, out _);
        Assert.Null(nulled);
    }

    [Fact]
    public async Task PullAsync_EmptyNextPageString_StopsPaging()
    {
        var mcp = new ScriptedMcp([LiveCompactListFixture]);
        var companies = await Action1OrganizationMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 5);

        var adroc = Assert.Single(companies);
        Assert.Equal("Adroc Capital", adroc.Name);
        Assert.Equal("4702a030-5f67-11f0-9cb3-e3f0bda36034", adroc.ExternalId);
        Assert.Single(mcp.Calls);
        Assert.Equal(Action1OrganizationMapper.ToolName, mcp.Calls[0].Tool);
        Assert.Contains("\"admin\":true", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":5", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("from", mcp.Calls[0].Args, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_NextPageFrom_ContinuesOnRawCount_NotMappedCount()
    {
        var mcp = new ScriptedMcp([NextPageFromFixture, NextPageEmptyFixture]);
        var companies = await Action1OrganizationMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 3);

        var adroc = Assert.Single(companies);
        Assert.Equal("Adroc Capital", adroc.Name);
        Assert.Equal(2, mcp.Calls.Count);
        Assert.All(mcp.Calls, c => Assert.Equal(Action1OrganizationMapper.ToolName, c.Tool));
        Assert.DoesNotContain("from", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"from\":3", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":3", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.Contains("\"admin\":true", mcp.Calls[1].Args, StringComparison.Ordinal);
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
            var inner = _pages.Count > 0 ? _pages.Dequeue() : NextPageEmptyFixture;
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
