using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class SlideClientMapperTests
{
    // Sanitized Compact slide_list_clients envelope. Field names match Slide's list object
    // (client_id / name / comments) and pagination.next_offset. No live Compact; no PII.
    public const string SanitizedListFixture = """
        {"data":[{"client_id":"c_0123456789ab","name":"ExampleCo","comments":"Primary backup grouping"},{"client_id":"c_example00001","name":"Contoso Backup","comments":""}],"pagination":{}}
        """;

    // Two raw rows (one empty name), next_offset present. Mapped count is 1; raw count is 2.
    public const string NextOffsetContinuesFixture = """
        {"data":[{"client_id":"c_0123456789ab","name":"ExampleCo","comments":"Primary backup grouping"},{"client_id":"c_aaaaaaaaaaaa","name":"","comments":""}],"pagination":{"next_offset":2}}
        """;

    public const string EmptyDataFixture = """
        {"data":[],"pagination":{}}
        """;

    [Fact]
    public void MapClients_SanitizedEnvelope_MapsClientIdAndName_IgnoresComments_DoesNotInventInactive()
    {
        var companies = SlideClientMapper.MapClients(SanitizedListFixture, out var nextOffset, out var dataCount);

        Assert.Equal(2, dataCount);
        Assert.Null(nextOffset);
        Assert.Equal(2, companies.Count);

        var example = companies[0];
        Assert.Equal("c_0123456789ab", example.ExternalId);
        Assert.Equal("ExampleCo", example.Name);
        Assert.Null(example.IsInactive);
        Assert.Null(example.Slug);
        Assert.Null(example.PrimaryDomain);
        Assert.Null(example.Website);
        Assert.Null(example.City);
        Assert.Null(example.State);
        Assert.Null(example.Address);

        Assert.Equal("c_example00001", companies[1].ExternalId);
        Assert.Equal("Contoso Backup", companies[1].Name);
        Assert.Null(companies[1].IsInactive);
        Assert.DoesNotContain("Primary backup grouping", companies.Select(c => c.Name));
    }

    [Fact]
    public void MapClients_SkipsMissingClientIdOrName()
    {
        const string json = """
            {"data":[{"client_id":"","name":"No Id","comments":""},{"client_id":"c_bbbbbbbbbbbb","name":"","comments":""},{"name":"Missing Id","comments":""},{"client_id":"c_cccccccccccc","comments":"no name key"},{"client_id":"c_0123456789ab","name":"ExampleCo","comments":"Primary backup grouping"}],"pagination":{}}
            """;

        var companies = SlideClientMapper.MapClients(json, out _, out var dataCount);

        Assert.Equal(5, dataCount);
        var example = Assert.Single(companies);
        Assert.Equal("c_0123456789ab", example.ExternalId);
        Assert.Equal("ExampleCo", example.Name);
        Assert.DoesNotContain(companies, c => c.Name == "No Id");
        Assert.DoesNotContain(companies, c => c.Name == "Missing Id");
        Assert.DoesNotContain(companies, c => c.ExternalId == "c_bbbbbbbbbbbb");
        Assert.DoesNotContain(companies, c => c.ExternalId == "c_cccccccccccc");
    }

    [Fact]
    public void MapClients_JsonRpcContentText_UnwrapsToEnvelope()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = SanitizedListFixture } } },
        });

        var companies = SlideClientMapper.MapClients(wrapped);
        Assert.Equal(2, companies.Count);
        Assert.Equal("c_0123456789ab", companies[0].ExternalId);
        Assert.Equal("ExampleCo", companies[0].Name);
        Assert.Null(companies[0].IsInactive);
    }

    [Fact]
    public void BuildArgumentsJson_FirstPage_Limit50_OmitsOffset()
    {
        var args = SlideClientMapper.BuildArgumentsJson(offset: null);
        Assert.Contains("\"limit\":50", args, StringComparison.Ordinal);
        Assert.DoesNotContain("offset", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_ClampsLimitToMax50()
    {
        var args = SlideClientMapper.BuildArgumentsJson(offset: null, pageSize: 200);
        Assert.Contains("\"limit\":50", args, StringComparison.Ordinal);
        Assert.DoesNotContain("offset", args, StringComparison.Ordinal);
    }

    [Fact]
    public void MapClients_NextOffsetPresent_VersusAbsentAndEmptyData()
    {
        SlideClientMapper.MapClients(NextOffsetContinuesFixture, out var next, out var rowCount);
        Assert.Equal(2, rowCount);
        Assert.Equal(2, next);
        var pageArgs = SlideClientMapper.BuildArgumentsJson(next);
        Assert.Contains("\"offset\":2", pageArgs, StringComparison.Ordinal);
        Assert.Contains("\"limit\":50", pageArgs, StringComparison.Ordinal);

        SlideClientMapper.MapClients(SanitizedListFixture, out var absent, out _);
        Assert.Null(absent);

        SlideClientMapper.MapClients(EmptyDataFixture, out var emptyNext, out var emptyCount);
        Assert.Null(emptyNext);
        Assert.Equal(0, emptyCount);

        const string missingPagination = """{"data":[{"client_id":"c_0123456789ab","name":"ExampleCo","comments":""}]}""";
        SlideClientMapper.MapClients(missingPagination, out var missing, out _);
        Assert.Null(missing);

        const string nullNext = """{"data":[],"pagination":{"next_offset":null}}""";
        SlideClientMapper.MapClients(nullNext, out var nulled, out _);
        Assert.Null(nulled);
    }

    [Fact]
    public async Task PullAsync_AbsentNextOffset_StopsPaging()
    {
        var mcp = new ScriptedMcp([SanitizedListFixture]);
        var companies = await SlideClientMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 5);

        Assert.Equal(2, companies.Count);
        Assert.Equal("ExampleCo", companies[0].Name);
        Assert.Equal("c_0123456789ab", companies[0].ExternalId);
        Assert.Equal("Contoso Backup", companies[1].Name);
        Assert.Single(mcp.Calls);
        Assert.Equal(SlideClientMapper.ToolName, mcp.Calls[0].Tool);
        Assert.Contains("\"limit\":5", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("offset", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("slide_get_client", mcp.Calls.Select(c => c.Tool));
    }

    [Fact]
    public async Task PullAsync_NextOffset_ContinuesOnRawCount_NotMappedCount()
    {
        var mcp = new ScriptedMcp([NextOffsetContinuesFixture, EmptyDataFixture]);
        var companies = await SlideClientMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 2);

        var example = Assert.Single(companies);
        Assert.Equal("ExampleCo", example.Name);
        Assert.Equal("c_0123456789ab", example.ExternalId);
        Assert.Equal(2, mcp.Calls.Count);
        Assert.All(mcp.Calls, c => Assert.Equal(SlideClientMapper.ToolName, c.Tool));
        Assert.DoesNotContain("offset", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"limit\":2", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"offset\":2", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.Contains("\"limit\":2", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("slide_get_client", mcp.Calls.Select(c => c.Tool));
    }

    [Fact]
    public async Task PullAsync_EmptyData_StopsEvenWhenNextOffsetWouldContinue()
    {
        var mcp = new ScriptedMcp([EmptyDataFixture, SanitizedListFixture]);
        var companies = await SlideClientMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 5);

        Assert.Empty(companies);
        Assert.Single(mcp.Calls);
        Assert.Equal(SlideClientMapper.ToolName, mcp.Calls[0].Tool);
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
            var inner = _pages.Count > 0 ? _pages.Dequeue() : EmptyDataFixture;
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
