using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class CompassOneTenantMapperTests
{
    // Live Compact compassone_list_tenants wrapper (pageSize 5). Field names exact from the live tool.
    // data[] is the list; meta.currentPage / totalPages drive the cursor. snapAgentUrl is ignored.
    public const string LiveCompactListFixture = """
        {"data":[{"id":"ce212a59-dab3-49ec-b6d7-546a2159b8ad","name":"Adroc Capital LLC","accountId":"1481d1c4-c81d-45c9-9ca4-58859b07f725","domain":"https://adroccap.com","type":"MDR","description":null,"snapAgentUrl":"https://installer.blackpointcyber.com/production/ce212a59-dab3-49ec-b6d7-546a2159b8ad/AdrocCapitalLLC_snap_installer.exe"}],"meta":{"currentPage":1,"totalItems":9,"pageSize":5,"totalPages":2}}
        """;

    // Same live row, last page — currentPage == totalPages so a Recording MCP that always returns this body stops.
    public const string LastPageFixture = """
        {"data":[{"id":"ce212a59-dab3-49ec-b6d7-546a2159b8ad","name":"Adroc Capital LLC","accountId":"1481d1c4-c81d-45c9-9ca4-58859b07f725","domain":"https://adroccap.com","type":"MDR","description":null,"snapAgentUrl":"https://installer.blackpointcyber.com/production/ce212a59-dab3-49ec-b6d7-546a2159b8ad/AdrocCapitalLLC_snap_installer.exe"}],"meta":{"currentPage":1,"totalItems":1,"pageSize":5,"totalPages":1}}
        """;

    // Two raw rows (one empty name), full page, more pages exist. Mapped count is 1; raw count is 2.
    public const string NextPageContinuesFixture = """
        {"data":[{"id":"ce212a59-dab3-49ec-b6d7-546a2159b8ad","name":"Adroc Capital LLC","domain":"https://adroccap.com","type":"MDR"},{"id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","name":"","domain":"https://skip.example"}],"meta":{"currentPage":1,"totalItems":9,"pageSize":2,"totalPages":2}}
        """;

    public const string EmptyDataFixture = """
        {"data":[],"meta":{"currentPage":2,"totalItems":9,"pageSize":2,"totalPages":2}}
        """;

    [Fact]
    public void MapTenants_LiveCompactList_MapsAdrocIdNameDomain_IgnoresSnapAgentUrl_DoesNotInventInactive()
    {
        var companies = CompassOneTenantMapper.MapTenants(LiveCompactListFixture, out var currentPage, out var totalPages, out var rowCount);

        Assert.Equal(1, rowCount);
        Assert.Equal(1, currentPage);
        Assert.Equal(2, totalPages);
        var adroc = Assert.Single(companies);
        Assert.Equal("ce212a59-dab3-49ec-b6d7-546a2159b8ad", adroc.ExternalId);
        Assert.Equal("Adroc Capital LLC", adroc.Name);
        Assert.Equal("https://adroccap.com", adroc.Website);
        Assert.Null(adroc.IsInactive);
        Assert.Null(adroc.Slug);
        Assert.Null(adroc.PrimaryDomain);
        Assert.Null(adroc.City);
        Assert.Null(adroc.State);
        Assert.Null(adroc.Address);
        Assert.DoesNotContain("installer.blackpointcyber.com", adroc.Website, StringComparison.Ordinal);
        Assert.DoesNotContain("snapAgentUrl", adroc.Website, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MapTenants_SkipsEmptyIdOrName_DoesNotMapIgnoredFields()
    {
        var companies = CompassOneTenantMapper.MapTenants(NextPageContinuesFixture, out _, out _, out var rowCount);

        Assert.Equal(2, rowCount);
        var adroc = Assert.Single(companies);
        Assert.Equal("ce212a59-dab3-49ec-b6d7-546a2159b8ad", adroc.ExternalId);
        Assert.Equal("Adroc Capital LLC", adroc.Name);
        Assert.DoesNotContain(companies, c => c.ExternalId == "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    }

    [Fact]
    public void MapTenants_JsonRpcContentText_UnwrapsToData()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = LiveCompactListFixture } } },
        });

        var companies = CompassOneTenantMapper.MapTenants(wrapped);
        var adroc = Assert.Single(companies);
        Assert.Equal("ce212a59-dab3-49ec-b6d7-546a2159b8ad", adroc.ExternalId);
        Assert.Equal("Adroc Capital LLC", adroc.Name);
        Assert.Equal("https://adroccap.com", adroc.Website);
        Assert.Null(adroc.IsInactive);
    }

    [Fact]
    public void BuildArgumentsJson_FirstPage_Page1PageSize50()
    {
        var args = CompassOneTenantMapper.BuildArgumentsJson(page: 1);
        Assert.Contains("\"page\":1", args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":50", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_ClampsPageSizeToMax1000()
    {
        var args = CompassOneTenantMapper.BuildArgumentsJson(page: 1, pageSize: 20000);
        Assert.Contains("\"pageSize\":1000", args, StringComparison.Ordinal);
        Assert.Contains("\"page\":1", args, StringComparison.Ordinal);
    }

    [Fact]
    public void MapTenants_MetaTotalPages_VersusLastPageAndEmptyData()
    {
        CompassOneTenantMapper.MapTenants(LiveCompactListFixture, out var livePage, out var liveTotal, out _);
        Assert.Equal(1, livePage);
        Assert.Equal(2, liveTotal);

        CompassOneTenantMapper.MapTenants(LastPageFixture, out var lastPage, out var lastTotal, out var lastCount);
        Assert.Equal(1, lastPage);
        Assert.Equal(1, lastTotal);
        Assert.Equal(1, lastCount);

        CompassOneTenantMapper.MapTenants(EmptyDataFixture, out var emptyPage, out var emptyTotal, out var emptyCount);
        Assert.Equal(2, emptyPage);
        Assert.Equal(2, emptyTotal);
        Assert.Equal(0, emptyCount);

        const string missingMeta = """{"data":[{"id":"ce212a59-dab3-49ec-b6d7-546a2159b8ad","name":"Adroc Capital LLC"}]}""";
        CompassOneTenantMapper.MapTenants(missingMeta, out var missingPage, out var missingTotal, out _);
        Assert.Null(missingPage);
        Assert.Null(missingTotal);
    }

    [Fact]
    public async Task PullAsync_LastPage_StopsPaging()
    {
        var mcp = new ScriptedMcp([LastPageFixture]);
        var companies = await CompassOneTenantMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 5);

        var adroc = Assert.Single(companies);
        Assert.Equal("Adroc Capital LLC", adroc.Name);
        Assert.Equal("ce212a59-dab3-49ec-b6d7-546a2159b8ad", adroc.ExternalId);
        Assert.Equal("https://adroccap.com", adroc.Website);
        Assert.Single(mcp.Calls);
        Assert.Equal(CompassOneTenantMapper.ToolName, mcp.Calls[0].Tool);
        Assert.Contains("\"page\":1", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":5", mcp.Calls[0].Args, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_MetaTotalPages_ContinuesOnRawCount_NotMappedCount()
    {
        var mcp = new ScriptedMcp([NextPageContinuesFixture, EmptyDataFixture]);
        var companies = await CompassOneTenantMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 2);

        var adroc = Assert.Single(companies);
        Assert.Equal("Adroc Capital LLC", adroc.Name);
        Assert.Equal("ce212a59-dab3-49ec-b6d7-546a2159b8ad", adroc.ExternalId);
        Assert.Equal(2, mcp.Calls.Count);
        Assert.All(mcp.Calls, c => Assert.Equal(CompassOneTenantMapper.ToolName, c.Tool));
        Assert.Contains("\"page\":1", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":2", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"page\":2", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":2", mcp.Calls[1].Args, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_LivePage_StopsOnShortPageEvenWhenTotalPagesSaysMore()
    {
        // Live wrapper has one row, meta.pageSize 5, totalPages 2. Page on raw length / pageSize.
        var mcp = new ScriptedMcp([LiveCompactListFixture, EmptyDataFixture]);
        var companies = await CompassOneTenantMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 5);

        var adroc = Assert.Single(companies);
        Assert.Equal("Adroc Capital LLC", adroc.Name);
        Assert.Single(mcp.Calls);
        Assert.Contains("\"page\":1", mcp.Calls[0].Args, StringComparison.Ordinal);
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
