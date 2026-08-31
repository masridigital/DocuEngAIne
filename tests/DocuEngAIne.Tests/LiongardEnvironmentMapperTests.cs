using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class LiongardEnvironmentMapperTests
{
    // Catalog/docs fixture for liongard_list_environments (v2 GET). Field names match Liongard's
    // EnvironmentResponse: Data[] + Pagination. No live Compact; no PII.
    public const string CatalogListFixture = """
        {"Success":true,"Data":[{"ID":8888,"ServiceProviderID":1,"Name":"Contoso Nation","ShortName":"CN","Description":"Example Description","Status":1,"Visible":true,"Website":"contoso.com","Tier":{"Type":"Core","CanDowngrade":false,"AssociatedInspectors":14}},{"ID":12,"ServiceProviderID":1,"Name":"Inactive Co","ShortName":"INC","Description":"","Status":0,"Visible":true,"Website":""},{"ID":13,"ServiceProviderID":1,"Name":"Archive Co","ShortName":"ARC","Description":"","Status":"Archive","Visible":true,"Website":""}],"Pagination":{"TotalRows":126,"HasMoreRows":true,"CurrentPage":1,"TotalPages":3,"PageSize":50}}
        """;

    // Same two documented rows, last page — HasMoreRows false so a Recording MCP that always returns this body stops.
    public const string LastPageFixture = """
        {"Success":true,"Data":[{"ID":8888,"ServiceProviderID":1,"Name":"Contoso Nation","ShortName":"CN","Description":"Example Description","Status":1,"Visible":true,"Website":"contoso.com","Tier":{"Type":"Core","CanDowngrade":false,"AssociatedInspectors":14}},{"ID":12,"ServiceProviderID":1,"Name":"Inactive Co","ShortName":"INC","Description":"","Status":0,"Visible":true,"Website":""}],"Pagination":{"TotalRows":2,"HasMoreRows":false,"CurrentPage":1,"TotalPages":1,"PageSize":25}}
        """;

    // Two raw rows (one empty name), full page, more pages exist. Mapped count is 1; raw count is 2.
    public const string NextPageContinuesFixture = """
        {"Success":true,"Data":[{"ID":8888,"Name":"Contoso Nation","ShortName":"CN","Status":1,"Website":"contoso.com"},{"ID":99,"Name":"","ShortName":"SKIP","Status":1}],"Pagination":{"TotalRows":9,"HasMoreRows":true,"CurrentPage":1,"TotalPages":2,"PageSize":2}}
        """;

    public const string EmptyDataFixture = """
        {"Success":true,"Data":[],"Pagination":{"TotalRows":9,"HasMoreRows":false,"CurrentPage":2,"TotalPages":2,"PageSize":2}}
        """;

    [Fact]
    public void ToolNames_AreLiongardPrefixed_ListEnvironmentsAndInspectors()
    {
        Assert.Equal("liongard_list_environments", LiongardEnvironmentMapper.ToolName);
        Assert.Equal("liongard_list_inspectors_v1", LiongardEnvironmentMapper.InspectorToolName);
        Assert.StartsWith("liongard_", LiongardEnvironmentMapper.ToolName, StringComparison.Ordinal);
        Assert.StartsWith("liongard_", LiongardEnvironmentMapper.InspectorToolName, StringComparison.Ordinal);
    }

    [Fact]
    public void MapEnvironments_CatalogList_MapsIdNameSlugWebsiteStatus_IgnoresTierParentInspectors()
    {
        var companies = LiongardEnvironmentMapper.MapEnvironments(
            CatalogListFixture, out var hasMore, out var currentPage, out var totalPages, out var rowCount);

        Assert.Equal(3, rowCount);
        Assert.True(hasMore);
        Assert.Equal(1, currentPage);
        Assert.Equal(3, totalPages);
        var contoso = Assert.Single(companies);
        Assert.Equal("8888", contoso.ExternalId);
        Assert.Equal("Contoso Nation", contoso.Name);
        Assert.Equal("CN", contoso.Slug);
        Assert.Equal("contoso.com", contoso.Website);
        Assert.False(contoso.IsInactive);
        Assert.Null(contoso.PrimaryDomain);
        Assert.Null(contoso.City);
        Assert.Null(contoso.State);
        Assert.Null(contoso.Address);
        Assert.DoesNotContain(companies, c => c.Name == "Inactive Co");
        Assert.DoesNotContain(companies, c => c.Name == "Archive Co");
        Assert.DoesNotContain(companies, c => string.Equals(c.Name, "Core", StringComparison.Ordinal));
        Assert.DoesNotContain("Example Description", companies.Select(c => c.Name));
    }

    [Fact]
    public void MapEnvironments_SkipsArchiveAndInactive_KeepsActive()
    {
        const string json = """
            {"Success":true,"Data":[{"ID":1,"Name":"Active Co","Status":"Active"},{"ID":2,"Name":"Inactive Co","Status":"Inactive"},{"ID":3,"Name":"Archive Co","Status":"Archive"},{"ID":4,"Name":"Archived Co","Status":"Archived"},{"ID":5,"Name":"Zero Status","Status":0}]}
            """;

        var companies = LiongardEnvironmentMapper.MapEnvironments(json, out _, out _, out _, out var rowCount);
        Assert.Equal(5, rowCount);
        var active = Assert.Single(companies);
        Assert.Equal("1", active.ExternalId);
        Assert.Equal("Active Co", active.Name);
        Assert.False(active.IsInactive);
        Assert.DoesNotContain(companies, c => c.Name == "Inactive Co");
        Assert.DoesNotContain(companies, c => c.Name == "Archive Co");
        Assert.DoesNotContain(companies, c => c.Name == "Archived Co");
        Assert.DoesNotContain(companies, c => c.Name == "Zero Status");
    }

    [Fact]
    public void MapEnvironments_SkipsMissingIdOrName_DoesNotMapIgnoredFields()
    {
        const string json = """
            {"Success":true,"Data":[{"ID":"","Name":"No Id","Status":1},{"ID":99,"Name":"","Status":1},{"Name":"Missing Id","Status":1},{"ID":8888,"Name":"Contoso Nation","Status":1,"Description":"Example Description","Visible":false,"Tier":{"Type":"Essentials"}}],"Pagination":{"HasMoreRows":false,"CurrentPage":1,"TotalPages":1,"PageSize":4}}
            """;

        var companies = LiongardEnvironmentMapper.MapEnvironments(json, out _, out _, out _, out var rowCount);

        Assert.Equal(4, rowCount);
        var contoso = Assert.Single(companies);
        Assert.Equal("8888", contoso.ExternalId);
        Assert.Equal("Contoso Nation", contoso.Name);
        Assert.False(contoso.IsInactive);
        Assert.Null(contoso.Slug);
        Assert.DoesNotContain(companies, c => c.Name == "No Id");
        Assert.DoesNotContain(companies, c => c.Name == "Missing Id");
        Assert.DoesNotContain(companies, c => c.ExternalId == "99");
    }

    [Fact]
    public void MapEnvironments_JsonRpcContentText_UnwrapsToEnvironmentResponse()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = CatalogListFixture } } },
        });

        var companies = LiongardEnvironmentMapper.MapEnvironments(wrapped);
        var contoso = Assert.Single(companies);
        Assert.Equal("8888", contoso.ExternalId);
        Assert.Equal("Contoso Nation", contoso.Name);
        Assert.Equal("CN", contoso.Slug);
        Assert.Equal("contoso.com", contoso.Website);
        Assert.False(contoso.IsInactive);
        Assert.DoesNotContain(companies, c => c.Name == "Inactive Co");
        Assert.DoesNotContain(companies, c => c.Name == "Archive Co");
    }

    [Fact]
    public void MapEnvironments_BareDataArray_MapsEnvironments()
    {
        const string json = """
            [{"ID":8888,"Name":"Contoso Nation","ShortName":"CN","Status":1,"Website":"contoso.com"}]
            """;

        var company = Assert.Single(LiongardEnvironmentMapper.MapEnvironments(json));
        Assert.Equal("8888", company.ExternalId);
        Assert.Equal("Contoso Nation", company.Name);
        Assert.Equal("CN", company.Slug);
        Assert.Equal("contoso.com", company.Website);
        Assert.False(company.IsInactive);
    }

    [Fact]
    public void BuildArgumentsJson_FirstPage_Page1PageSize25_FlatOnly()
    {
        var args = LiongardEnvironmentMapper.BuildArgumentsJson(page: 1);
        Assert.Contains("\"page\":1", args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":25", args, StringComparison.Ordinal);
        Assert.DoesNotContain("Pagination", args, StringComparison.Ordinal);
        Assert.DoesNotContain("Filters", args, StringComparison.Ordinal);
        Assert.DoesNotContain("columns", args, StringComparison.Ordinal);
        Assert.DoesNotContain("orderBy", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_ClampsPageSizeToMax2000_AndFloorsPageTo1()
    {
        var args = LiongardEnvironmentMapper.BuildArgumentsJson(page: 0, pageSize: 5000);
        Assert.Contains("\"page\":1", args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":2000", args, StringComparison.Ordinal);
        Assert.DoesNotContain("Pagination", args, StringComparison.Ordinal);
        Assert.DoesNotContain("Filters", args, StringComparison.Ordinal);
    }

    [Fact]
    public void MapEnvironments_HasMoreRows_VersusLastPageAndEmptyData()
    {
        LiongardEnvironmentMapper.MapEnvironments(NextPageContinuesFixture, out var hasMore, out var page, out var total, out var rowCount);
        Assert.Equal(2, rowCount);
        Assert.True(hasMore);
        Assert.Equal(1, page);
        Assert.Equal(2, total);
        var next = LiongardEnvironmentMapper.BuildArgumentsJson((page ?? 1) + 1, pageSize: 2);
        Assert.Contains("\"page\":2", next, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":2", next, StringComparison.Ordinal);

        LiongardEnvironmentMapper.MapEnvironments(LastPageFixture, out var lastMore, out _, out var lastTotal, out _);
        Assert.False(lastMore);
        Assert.Equal(1, lastTotal);

        LiongardEnvironmentMapper.MapEnvironments(EmptyDataFixture, out var emptyMore, out var emptyPage, out _, out var emptyCount);
        Assert.False(emptyMore);
        Assert.Equal(2, emptyPage);
        Assert.Equal(0, emptyCount);

        const string missingPagination = """{"Success":true,"Data":[{"ID":8888,"Name":"Contoso Nation","Status":1}]}""";
        LiongardEnvironmentMapper.MapEnvironments(missingPagination, out var missingMore, out var missingPage, out var missingTotal, out _);
        Assert.Null(missingMore);
        Assert.Null(missingPage);
        Assert.Null(missingTotal);
    }

    [Fact]
    public async Task PullAsync_LastPage_StopsPaging_AndDoesNotCallInspectors()
    {
        var mcp = new ScriptedMcp([LastPageFixture]);
        var companies = await LiongardEnvironmentMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 25);

        var contoso = Assert.Single(companies);
        Assert.Equal("Contoso Nation", contoso.Name);
        Assert.Equal("8888", contoso.ExternalId);
        Assert.DoesNotContain(companies, c => c.Name == "Inactive Co");
        Assert.Single(mcp.Calls);
        Assert.Equal(LiongardEnvironmentMapper.ToolName, mcp.Calls[0].Tool);
        Assert.Contains("\"page\":1", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":25", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("Pagination", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain(LiongardEnvironmentMapper.InspectorToolName, mcp.Calls.Select(c => c.Tool));
        Assert.DoesNotContain("liongard_get_environment", mcp.Calls.Select(c => c.Tool));
    }

    [Fact]
    public async Task PullAsync_HasMoreRows_ContinuesOnRawCount_NotMappedCount()
    {
        var mcp = new ScriptedMcp([NextPageContinuesFixture, EmptyDataFixture]);
        var companies = await LiongardEnvironmentMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 2);

        var contoso = Assert.Single(companies);
        Assert.Equal("Contoso Nation", contoso.Name);
        Assert.Equal("8888", contoso.ExternalId);
        Assert.Equal(2, mcp.Calls.Count);
        Assert.All(mcp.Calls, c => Assert.Equal(LiongardEnvironmentMapper.ToolName, c.Tool));
        Assert.Contains("\"page\":1", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":2", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"page\":2", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":2", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.DoesNotContain(LiongardEnvironmentMapper.InspectorToolName, mcp.Calls.Select(c => c.Tool));
    }

    [Fact]
    public async Task PullAsync_EmptyData_StopsEvenWhenAnotherPageIsQueued()
    {
        var mcp = new ScriptedMcp([EmptyDataFixture, CatalogListFixture]);
        var companies = await LiongardEnvironmentMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 25);

        Assert.Empty(companies);
        Assert.Single(mcp.Calls);
        Assert.Equal(LiongardEnvironmentMapper.ToolName, mcp.Calls[0].Tool);
    }

    [Fact]
    public void MapEnvironments_SuccessFalse_Throws()
    {
        const string json = """{"Success":false,"Data":[],"Pagination":{"HasMoreRows":false,"CurrentPage":1,"TotalPages":1,"PageSize":25}}""";
        var ex = Assert.Throws<InvalidOperationException>(() => LiongardEnvironmentMapper.MapEnvironments(json));
        Assert.Contains("Liongard MCP tool error", ex.Message, StringComparison.Ordinal);
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
