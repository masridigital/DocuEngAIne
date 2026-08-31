using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class HuntressOrganizationMapperTests
{
    // Sanitized Compact huntress_list_organizations envelope. Field names match the Huntress
    // list object (id / name / key + ignored metadata) and pagination.next_page_token.
    // Connector is unsubscribed — no live Compact; no PII.
    public const string ExampleNextPageToken = "example-next-page-token";

    public const string SanitizedListFixture = """
        {"organizations":[{"id":1,"name":"Acme Inc.","key":"acme-inc","account_id":5,"agents_count":42,"incident_reports_count":3,"logs_sources_count":2,"identity_provider_tenant_id":"00000000-0000-0000-0000-000000000001","billable_identity_count":10,"report_recipients":["notify@example.com"],"sat_learner_count":0,"created_at":"2022-03-01T18:54:02Z","updated_at":"2022-03-01T18:54:02Z"},{"id":2,"name":"Contoso Security","key":"contoso-sec","account_id":5,"agents_count":7,"incident_reports_count":0,"created_at":"2023-01-15T12:00:00Z","updated_at":"2023-01-15T12:00:00Z"}],"pagination":{}}
        """;

    // Two raw rows (one empty name), next_page_token present. Mapped count is 1; raw count is 2.
    public const string NextPageContinuesFixture = """
        {"organizations":[{"id":1,"name":"Acme Inc.","key":"acme-inc"},{"id":99,"name":"","key":"skip-empty"}],"pagination":{"next_page_token":"example-next-page-token"}}
        """;

    public const string EmptyOrganizationsFixture = """
        {"organizations":[],"pagination":{}}
        """;

    [Fact]
    public void MapOrganizations_SanitizedEnvelope_MapsIdNameKeySlug_IgnoresMetadata_DoesNotInventInactive()
    {
        var companies = HuntressOrganizationMapper.MapOrganizations(SanitizedListFixture, out var nextPageToken, out var rowCount);

        Assert.Equal(2, rowCount);
        Assert.Null(nextPageToken);
        Assert.Equal(2, companies.Count);

        var acme = companies[0];
        Assert.Equal("1", acme.ExternalId);
        Assert.Equal("Acme Inc.", acme.Name);
        Assert.Equal("acme-inc", acme.Slug);
        Assert.Null(acme.IsInactive);
        Assert.Null(acme.PrimaryDomain);
        Assert.Null(acme.Website);
        Assert.Null(acme.City);
        Assert.Null(acme.State);
        Assert.Null(acme.Address);
        Assert.DoesNotContain("notify@example.com", companies.Select(c => c.Name));
        Assert.DoesNotContain("00000000-0000-0000-0000-000000000001", companies.Select(c => c.ExternalId));

        Assert.Equal("2", companies[1].ExternalId);
        Assert.Equal("Contoso Security", companies[1].Name);
        Assert.Equal("contoso-sec", companies[1].Slug);
        Assert.Null(companies[1].IsInactive);
    }

    [Fact]
    public void MapOrganizations_SkipsMissingIdOrName_CountsRawRows()
    {
        const string json = """
            {"organizations":[{"id":"","name":"No Id","key":"no-id"},{"id":99,"name":"","key":"skip-empty"},{"name":"Missing Id","key":"missing-id"},{"id":3,"key":"no-name-key"},{"id":1,"name":"Acme Inc.","key":"acme-inc"}],"pagination":{}}
            """;

        var companies = HuntressOrganizationMapper.MapOrganizations(json, out _, out var rowCount);

        Assert.Equal(5, rowCount);
        var acme = Assert.Single(companies);
        Assert.Equal("1", acme.ExternalId);
        Assert.Equal("Acme Inc.", acme.Name);
        Assert.Equal("acme-inc", acme.Slug);
        Assert.DoesNotContain(companies, c => c.Name == "No Id");
        Assert.DoesNotContain(companies, c => c.Name == "Missing Id");
        Assert.DoesNotContain(companies, c => c.ExternalId == "99");
        Assert.DoesNotContain(companies, c => c.ExternalId == "3");
    }

    [Fact]
    public void MapOrganizations_JsonRpcContentText_UnwrapsToEnvelope()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = SanitizedListFixture } } },
        });

        var companies = HuntressOrganizationMapper.MapOrganizations(wrapped);
        Assert.Equal(2, companies.Count);
        Assert.Equal("1", companies[0].ExternalId);
        Assert.Equal("Acme Inc.", companies[0].Name);
        Assert.Equal("acme-inc", companies[0].Slug);
        Assert.Null(companies[0].IsInactive);
    }

    [Fact]
    public void BuildArgumentsJson_FirstPage_Limit50_OmitsPageTokenAndFilters()
    {
        var args = HuntressOrganizationMapper.BuildArgumentsJson(pageToken: null);
        Assert.Contains("\"limit\":50", args, StringComparison.Ordinal);
        Assert.DoesNotContain("pageToken", args, StringComparison.Ordinal);
        Assert.DoesNotContain("\"name\"", args, StringComparison.Ordinal);
        Assert.DoesNotContain("\"key\"", args, StringComparison.Ordinal);
        Assert.DoesNotContain("sort", args, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildArgumentsJson_ClampsLimitToApiMax500()
    {
        var args = HuntressOrganizationMapper.BuildArgumentsJson(pageToken: null, pageSize: 20000);
        Assert.Contains("\"limit\":500", args, StringComparison.Ordinal);
        Assert.DoesNotContain("pageToken", args, StringComparison.Ordinal);
    }

    [Fact]
    public void MapOrganizations_NextPageToken_VersusAbsentAndEmpty()
    {
        HuntressOrganizationMapper.MapOrganizations(NextPageContinuesFixture, out var next, out var rowCount);
        Assert.Equal(2, rowCount);
        Assert.Equal(ExampleNextPageToken, next);
        var pageArgs = HuntressOrganizationMapper.BuildArgumentsJson(next);
        Assert.Contains($"\"pageToken\":\"{ExampleNextPageToken}\"", pageArgs, StringComparison.Ordinal);
        Assert.Contains("\"limit\":50", pageArgs, StringComparison.Ordinal);

        HuntressOrganizationMapper.MapOrganizations(SanitizedListFixture, out var absent, out _);
        Assert.Null(absent);

        HuntressOrganizationMapper.MapOrganizations(EmptyOrganizationsFixture, out var emptyNext, out var emptyCount);
        Assert.Null(emptyNext);
        Assert.Equal(0, emptyCount);

        const string missingPagination = """{"organizations":[{"id":1,"name":"Acme Inc.","key":"acme-inc"}]}""";
        HuntressOrganizationMapper.MapOrganizations(missingPagination, out var missing, out _);
        Assert.Null(missing);

        const string nullNext = """{"organizations":[],"pagination":{"next_page_token":null}}""";
        HuntressOrganizationMapper.MapOrganizations(nullNext, out var nulled, out _);
        Assert.Null(nulled);

        const string emptyStringNext = """{"organizations":[{"id":1,"name":"Acme Inc.","key":"acme-inc"}],"pagination":{"next_page_token":""}}""";
        HuntressOrganizationMapper.MapOrganizations(emptyStringNext, out var emptyString, out _);
        Assert.Null(emptyString);

        const string urlOnly = """{"organizations":[{"id":1,"name":"Acme Inc.","key":"acme-inc"}],"pagination":{"next_page_url":"https://api.example/v1/organizations?page_token=x"}}""";
        HuntressOrganizationMapper.MapOrganizations(urlOnly, out var fromUrl, out _);
        Assert.Null(fromUrl);
    }

    [Fact]
    public async Task PullAsync_AbsentNextPageToken_StopsPaging()
    {
        var mcp = new ScriptedMcp([SanitizedListFixture]);
        var companies = await HuntressOrganizationMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 5);

        Assert.Equal(2, companies.Count);
        Assert.Equal("Acme Inc.", companies[0].Name);
        Assert.Equal("1", companies[0].ExternalId);
        Assert.Equal("acme-inc", companies[0].Slug);
        Assert.Equal("Contoso Security", companies[1].Name);
        Assert.Single(mcp.Calls);
        Assert.Equal(HuntressOrganizationMapper.ToolName, mcp.Calls[0].Tool);
        Assert.Contains("\"limit\":5", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("pageToken", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("huntress_get_organization", mcp.Calls.Select(c => c.Tool));
        Assert.DoesNotContain("huntress_get_account", mcp.Calls.Select(c => c.Tool));
        Assert.DoesNotContain("huntress_list_managed_accounts", mcp.Calls.Select(c => c.Tool));
    }

    [Fact]
    public async Task PullAsync_NextPageToken_ContinuesOnRawCount_NotMappedCount()
    {
        var mcp = new ScriptedMcp([NextPageContinuesFixture, EmptyOrganizationsFixture]);
        var companies = await HuntressOrganizationMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 2);

        var acme = Assert.Single(companies);
        Assert.Equal("Acme Inc.", acme.Name);
        Assert.Equal("1", acme.ExternalId);
        Assert.Equal(2, mcp.Calls.Count);
        Assert.All(mcp.Calls, c => Assert.Equal(HuntressOrganizationMapper.ToolName, c.Tool));
        Assert.DoesNotContain("pageToken", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"limit\":2", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains($"\"pageToken\":\"{ExampleNextPageToken}\"", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.Contains("\"limit\":2", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("\"name\"", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("\"key\"", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("huntress_get_organization", mcp.Calls.Select(c => c.Tool));
        Assert.DoesNotContain("huntress_list_managed_accounts", mcp.Calls.Select(c => c.Tool));
    }

    [Fact]
    public async Task PullAsync_EmptyOrganizations_StopsEvenWhenNextPageWouldContinue()
    {
        var mcp = new ScriptedMcp([EmptyOrganizationsFixture, SanitizedListFixture]);
        var companies = await HuntressOrganizationMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 5);

        Assert.Empty(companies);
        Assert.Single(mcp.Calls);
        Assert.Equal(HuntressOrganizationMapper.ToolName, mcp.Calls[0].Tool);
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
            var inner = _pages.Count > 0 ? _pages.Dequeue() : EmptyOrganizationsFixture;
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
