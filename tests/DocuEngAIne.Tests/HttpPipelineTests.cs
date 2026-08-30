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
}
