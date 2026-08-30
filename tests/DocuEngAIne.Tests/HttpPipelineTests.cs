using System.Net;
using System.Net.Http.Json;
using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Infrastructure.Identity;

namespace DocuEngAIne.Tests;

/// <summary>
/// HTTP-level coverage of the behaviors that the service-layer tests cannot see: authentication
/// on the endpoint groups, anonymous health, tenant isolation through <c>ForTenant</c> on the
/// request path, and the <see cref="AuthExtensions.AdminPolicy"/> gate on integrations.
/// </summary>
public class HttpPipelineTests : IClassFixture<TestHost>
{
    private readonly TestHost _host;

    public HttpPipelineTests(TestHost host) => _host = host;

    [Fact]
    public async Task Unauthenticated_Llm_Chat_Returns_401()
    {
        using var client = _host.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            "/api/llm/chat",
            new LlmChatRequest([new LlmChatMessageRequest("user", "hello")]));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_Companies_Returns_401()
    {
        using var client = _host.CreateAnonymousClient();

        var response = await client.GetAsync("/api/companies");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Other_Tenant_Company_Get_Returns_404()
    {
        using var client = _host.CreateOwnerClient();

        var response = await client.GetAsync($"/api/companies/{_host.OtherTenantCompanyId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Other_Tenant_Company_Graph_Returns_404()
    {
        using var client = _host.CreateOwnerClient();

        var response = await client.GetAsync($"/api/companies/{_host.OtherTenantCompanyId}/graph");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Other_Tenant_Folder_Attach_On_Document_Create_Returns_400()
    {
        using var client = _host.CreateContributorClient();

        var response = await client.PostAsJsonAsync(
            "/api/documents",
            new CreateDocumentRequest(
                "Leak",
                "leak",
                null,
                null,
                null,
                true,
                CompanyId: null,
                FolderId: _host.OtherTenantFolderId));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            FolderEndpoints.FolderNotFoundMessage,
            await response.Content.ReadFromJsonAsync<string>());
    }

    [Fact]
    public async Task Health_Live_Is_Anonymous_200()
    {
        using var client = _host.CreateAnonymousClient();

        var response = await client.GetAsync("/api/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Integrations_Returns_403_For_Reader()
    {
        using var client = _host.CreateReaderClient();

        var response = await client.GetAsync("/api/integrations");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Tokens_Returns_403_For_Reader()
    {
        using var client = _host.CreateReaderClient();

        var response = await client.GetAsync("/api/tokens");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Mcp_Get_Is_Anonymous_200()
    {
        using var client = _host.CreateAnonymousClient();

        var response = await client.GetAsync("/mcp");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("list_companies", body);
        Assert.DoesNotContain("\"reveal\"", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mcp_Post_Without_Token_Returns_401()
    {
        using var client = _host.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = "1", method = "initialize" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Mcp_Post_With_Browser_Jwt_And_No_Api_Token_Returns_401()
    {
        using var client = _host.CreateOwnerClient();

        var response = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = "1", method = "initialize" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Mcp_Post_With_Tenant_Token_Lists_Only_That_Tenant()
    {
        string plaintext;
        using (var admin = _host.CreateOwnerClient())
        {
            var created = await admin.PostAsJsonAsync("/api/tokens", new CreateApiTokenRequest("pipeline"));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var body = await created.Content.ReadFromJsonAsync<CreatedApiTokenResponse>();
            Assert.NotNull(body);
            plaintext = body.Token;
        }

        using var mcp = _host.CreateAnonymousClient();
        mcp.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", plaintext);

        var response = await mcp.PostAsJsonAsync(
            "/mcp",
            new { jsonrpc = "2.0", id = "1", method = "tools/call", @params = new { name = "list_companies", arguments = new { } } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("PoisonCo", json);
        Assert.DoesNotContain("reveal", json, StringComparison.OrdinalIgnoreCase);
    }
}
