using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class ThreatLockerOrganizationMapperTests
{
    // Catalog-schema fixture only. ThreatLocker is not subscribed — do not call live Compact.
    // There is no tl_list_organizations. Child-org search returns GUIDs used as managedOrganizationId.
    // Fields: organizationId, displayName, name, domains. Envelope: items + totalItems (pageNumber paging).
    public const string CatalogListFixture = """
        {"totalItems":2,"items":[{"organizationId":"3f2a1c80-9b14-4e6d-a7c1-0d8e5b2a4f11","displayName":"Adroc Capital","name":"Adroc Capital","domains":["adroccap.com"]},{"organizationId":"7c9e4d12-2a88-4b01-9f3e-6a1c0b8d5e20","displayName":"Masri Digital (Customer)","name":"Masri Digital (Customer)","domains":[]}]}
        """;

    // Two raw rows (one empty name), full pageSize, more remain. Mapped count is 1; raw count is 2.
    public const string NextPageContinuesFixture = """
        {"totalItems":3,"items":[{"organizationId":"3f2a1c80-9b14-4e6d-a7c1-0d8e5b2a4f11","displayName":"Adroc Capital","name":"Adroc Capital","domains":["adroccap.com"]},{"organizationId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","displayName":"","name":"","domains":["skip.example"]}]}
        """;

    public const string LastPageFixture = """
        {"totalItems":3,"items":[{"organizationId":"7c9e4d12-2a88-4b01-9f3e-6a1c0b8d5e20","displayName":"Masri Digital (Customer)","name":"Masri Digital (Customer)","domains":[]}]}
        """;

    public const string EmptyItemsFixture = """
        {"totalItems":3,"items":[]}
        """;

    [Fact]
    public void MapOrganizations_CatalogList_MapsOrganizationIdDisplayNameDomains_DoesNotInventInactive()
    {
        var companies = ThreatLockerOrganizationMapper.MapOrganizations(CatalogListFixture, out var totalItems, out var rowCount);

        Assert.Equal(2, rowCount);
        Assert.Equal(2, totalItems);
        Assert.Equal(2, companies.Count);

        var adroc = companies[0];
        Assert.Equal("3f2a1c80-9b14-4e6d-a7c1-0d8e5b2a4f11", adroc.ExternalId);
        Assert.Equal("Adroc Capital", adroc.Name);
        Assert.Equal("adroccap.com", adroc.PrimaryDomain);
        Assert.Null(adroc.IsInactive);
        Assert.Null(adroc.Slug);
        Assert.Null(adroc.Website);
        Assert.Null(adroc.City);
        Assert.Null(adroc.State);
        Assert.Null(adroc.Address);

        var masri = companies[1];
        Assert.Equal("7c9e4d12-2a88-4b01-9f3e-6a1c0b8d5e20", masri.ExternalId);
        Assert.Equal("Masri Digital (Customer)", masri.Name);
        Assert.Null(masri.PrimaryDomain);
        Assert.Null(masri.IsInactive);
    }

    [Fact]
    public void MapOrganizations_RawArray_MapsWithoutEnvelope()
    {
        const string json = """
            [{"organizationId":"3f2a1c80-9b14-4e6d-a7c1-0d8e5b2a4f11","displayName":"Adroc Capital","name":"Adroc Capital","domains":["adroccap.com"]}]
            """;

        var companies = ThreatLockerOrganizationMapper.MapOrganizations(json, out var totalItems, out var rowCount);
        Assert.Null(totalItems);
        Assert.Equal(1, rowCount);
        var adroc = Assert.Single(companies);
        Assert.Equal("3f2a1c80-9b14-4e6d-a7c1-0d8e5b2a4f11", adroc.ExternalId);
        Assert.Equal("Adroc Capital", adroc.Name);
        Assert.Equal("adroccap.com", adroc.PrimaryDomain);
        Assert.Null(adroc.IsInactive);
    }

    [Fact]
    public void MapOrganizations_Skips_Missing_Id_Or_Name_But_Counts_Raw_Rows()
    {
        const string json = """
            {"totalItems":3,"items":[{"organizationId":"","displayName":"No Id","name":"No Id","domains":["x.example"]},{"organizationId":"bbbbbbbb-bbbb-cccc-dddd-eeeeeeeeeeee","displayName":"","name":"","domains":["y.example"]},{"organizationId":"3f2a1c80-9b14-4e6d-a7c1-0d8e5b2a4f11","displayName":"Adroc Capital","name":"Adroc Capital","domains":["adroccap.com"]}]}
            """;

        var companies = ThreatLockerOrganizationMapper.MapOrganizations(json, out _, out var rowCount);
        Assert.Equal(3, rowCount);
        var adroc = Assert.Single(companies);
        Assert.Equal("Adroc Capital", adroc.Name);
        Assert.Equal("3f2a1c80-9b14-4e6d-a7c1-0d8e5b2a4f11", adroc.ExternalId);
        Assert.DoesNotContain(companies, c => c.Name == "No Id");
    }

    [Fact]
    public void MapOrganizations_DisplayNameWinsOverName()
    {
        const string json = """
            {"items":[{"organizationId":"3f2a1c80-9b14-4e6d-a7c1-0d8e5b2a4f11","displayName":"Adroc Capital","name":"internal-adroc","domains":["adroccap.com"]}]}
            """;

        var company = Assert.Single(ThreatLockerOrganizationMapper.MapOrganizations(json));
        Assert.Equal("Adroc Capital", company.Name);
        Assert.Equal("adroccap.com", company.PrimaryDomain);
        Assert.Null(company.Slug);
    }

    [Fact]
    public void MapOrganizations_JsonRpcContentText_UnwrapsToItems()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = CatalogListFixture } } },
        });

        var companies = ThreatLockerOrganizationMapper.MapOrganizations(wrapped);
        Assert.Equal(2, companies.Count);
        Assert.Equal("3f2a1c80-9b14-4e6d-a7c1-0d8e5b2a4f11", companies[0].ExternalId);
        Assert.Equal("Adroc Capital", companies[0].Name);
        Assert.Equal("adroccap.com", companies[0].PrimaryDomain);
        Assert.Null(companies[0].IsInactive);
    }

    [Fact]
    public void BuildArgumentsJson_FirstPage_RequiresOrderBy_PageNumber1_OmitsManagedOrganizationId()
    {
        var args = ThreatLockerOrganizationMapper.BuildArgumentsJson(pageNumber: 1);
        Assert.Contains("\"orderBy\":\"name\"", args, StringComparison.Ordinal);
        Assert.Contains("\"pageNumber\":1", args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":50", args, StringComparison.Ordinal);
        Assert.Contains("\"includeAllChildren\":true", args, StringComparison.Ordinal);
        Assert.Contains("\"isAscending\":true", args, StringComparison.Ordinal);
        Assert.DoesNotContain("managedOrganizationId", args, StringComparison.Ordinal);
        Assert.DoesNotContain("searchText", args, StringComparison.Ordinal);
        Assert.DoesNotContain("cursor", args, StringComparison.Ordinal);
        Assert.DoesNotContain("startingAfter", args, StringComparison.Ordinal);
        Assert.DoesNotContain("tl_list_organizations", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_PassesManagedOrganizationId_AndClampsPageSizeTo500()
    {
        var args = ThreatLockerOrganizationMapper.BuildArgumentsJson(
            pageNumber: 0,
            pageSize: 20000,
            managedOrganizationId: "3f2a1c80-9b14-4e6d-a7c1-0d8e5b2a4f11");
        Assert.Contains("\"orderBy\":\"name\"", args, StringComparison.Ordinal);
        Assert.Contains("\"pageNumber\":1", args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":500", args, StringComparison.Ordinal);
        Assert.Contains("\"managedOrganizationId\":\"3f2a1c80-9b14-4e6d-a7c1-0d8e5b2a4f11\"", args, StringComparison.Ordinal);
    }

    [Fact]
    public void MapOrganizations_TotalItems_VersusShortAndEmptyPage()
    {
        ThreatLockerOrganizationMapper.MapOrganizations(CatalogListFixture, out var liveTotal, out var liveCount);
        Assert.Equal(2, liveTotal);
        Assert.Equal(2, liveCount);

        ThreatLockerOrganizationMapper.MapOrganizations(NextPageContinuesFixture, out var nextTotal, out var nextCount);
        Assert.Equal(3, nextTotal);
        Assert.Equal(2, nextCount);

        ThreatLockerOrganizationMapper.MapOrganizations(LastPageFixture, out var lastTotal, out var lastCount);
        Assert.Equal(3, lastTotal);
        Assert.Equal(1, lastCount);

        ThreatLockerOrganizationMapper.MapOrganizations(EmptyItemsFixture, out var emptyTotal, out var emptyCount);
        Assert.Equal(3, emptyTotal);
        Assert.Equal(0, emptyCount);
    }

    [Fact]
    public async Task PullAsync_CatalogPage_CallsSearchChildOrganizations_StopsOnTotalItems()
    {
        var mcp = new ScriptedMcp([CatalogListFixture]);
        var companies = await ThreatLockerOrganizationMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 50);

        Assert.Equal(2, companies.Count);
        Assert.Equal("Adroc Capital", companies[0].Name);
        Assert.Equal("3f2a1c80-9b14-4e6d-a7c1-0d8e5b2a4f11", companies[0].ExternalId);
        Assert.Equal("adroccap.com", companies[0].PrimaryDomain);
        var call = Assert.Single(mcp.Calls);
        Assert.Equal(ThreatLockerOrganizationMapper.ToolName, call.Tool);
        Assert.Equal("tl_search_child_organizations", call.Tool);
        Assert.Contains("\"orderBy\":\"name\"", call.Args, StringComparison.Ordinal);
        Assert.Contains("\"pageNumber\":1", call.Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":50", call.Args, StringComparison.Ordinal);
        Assert.DoesNotContain("managedOrganizationId", call.Args, StringComparison.Ordinal);
        Assert.DoesNotContain("cursor", call.Args, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_ContinuesOnRawCount_NotMappedCount_IncrementsPageNumber()
    {
        var mcp = new ScriptedMcp([NextPageContinuesFixture, LastPageFixture]);
        var companies = await ThreatLockerOrganizationMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 2);

        Assert.Equal(2, companies.Count);
        Assert.Equal("Adroc Capital", companies[0].Name);
        Assert.Equal("Masri Digital (Customer)", companies[1].Name);
        Assert.Equal(2, mcp.Calls.Count);
        Assert.All(mcp.Calls, c => Assert.Equal("tl_search_child_organizations", c.Tool));
        Assert.Contains("\"pageNumber\":1", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":2", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"orderBy\":\"name\"", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageNumber\":2", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":2", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.DoesNotContain(companies, c => c.ExternalId == "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    }

    [Fact]
    public async Task PullAsync_PassesManagedOrganizationId_AndStopsOnEmptyItems()
    {
        var mcp = new ScriptedMcp([EmptyItemsFixture]);
        var companies = await ThreatLockerOrganizationMapper.PullAsync(
            mcp,
            Guid.NewGuid(),
            pageSize: 5,
            managedOrganizationId: "3f2a1c80-9b14-4e6d-a7c1-0d8e5b2a4f11");

        Assert.Empty(companies);
        var call = Assert.Single(mcp.Calls);
        Assert.Equal("tl_search_child_organizations", call.Tool);
        Assert.Contains("\"managedOrganizationId\":\"3f2a1c80-9b14-4e6d-a7c1-0d8e5b2a4f11\"", call.Args, StringComparison.Ordinal);
        Assert.Contains("\"orderBy\":\"name\"", call.Args, StringComparison.Ordinal);
        Assert.Contains("\"pageNumber\":1", call.Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":5", call.Args, StringComparison.Ordinal);
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
            var inner = _pages.Count > 0 ? _pages.Dequeue() : EmptyItemsFixture;
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
