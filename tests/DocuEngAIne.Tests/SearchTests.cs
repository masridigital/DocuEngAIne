using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Configuration;
using DocuEngAIne.Infrastructure.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocuEngAIne.Tests;

/// <summary>
/// Azure AI Search scaffolding: in-memory <see cref="ISearchService"/> only. No Azure SDK
/// client, no endpoint, no key — CI must never reach a live search service.
/// </summary>
public class SearchTests : IClassFixture<TestHost>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly TestHost _host;

    public SearchTests(TestHost host) => _host = host;

    [Fact]
    public async Task Indexes_Title_Body_Company_And_Tenant_And_Finds_By_Each_Text_Field()
    {
        var search = new InMemorySearchService();
        var tenantId = Guid.NewGuid();
        var companyId = Guid.NewGuid();
        var document = new SearchDocument(
            Guid.NewGuid(),
            "VPN runbook",
            "Split-tunnel checklist for ExampleCo",
            companyId,
            tenantId);

        await search.IndexDocumentAsync(document);

        var byTitle = await search.SearchAsync("vpn", tenantId);
        var byBody = await search.SearchAsync("split-tunnel", tenantId);

        var hit = Assert.Single(byTitle);
        Assert.Equal(document.Id, hit.Id);
        Assert.Equal(document.Title, hit.Title);
        Assert.Equal(document.Body, hit.Body);
        Assert.Equal(companyId, hit.CompanyId);
        Assert.Equal(tenantId, hit.TenantId);
        Assert.Equal(hit.Id, Assert.Single(byBody).Id);
    }

    [Fact]
    public async Task Search_Does_Not_Leak_Other_Tenant_Documents()
    {
        var search = new InMemorySearchService();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        await search.IndexDocumentAsync(new SearchDocument(
            Guid.NewGuid(), "Shared phrase", "body A", Guid.NewGuid(), tenantA));
        await search.IndexDocumentAsync(new SearchDocument(
            Guid.NewGuid(), "Shared phrase", "body B", Guid.NewGuid(), tenantB));

        var forA = await search.SearchAsync("Shared phrase", tenantA);
        var forB = await search.SearchAsync("Shared phrase", tenantB);

        Assert.Single(forA);
        Assert.Equal(tenantA, forA[0].TenantId);
        Assert.Equal("body A", forA[0].Body);
        Assert.Single(forB);
        Assert.Equal(tenantB, forB[0].TenantId);
        Assert.Equal("body B", forB[0].Body);
    }

    [Fact]
    public async Task Empty_Query_Returns_Empty_And_Remove_Drops_The_Hit()
    {
        var search = new InMemorySearchService();
        var tenantId = Guid.NewGuid();
        var id = Guid.NewGuid();
        await search.IndexDocumentAsync(new SearchDocument(id, "Keep", "indexed body", null, tenantId));

        Assert.Empty(await search.SearchAsync("  ", tenantId));
        Assert.Empty(await search.SearchAsync("", tenantId));

        await search.RemoveDocumentAsync(id, tenantId);
        Assert.Empty(await search.SearchAsync("Keep", tenantId));
    }

    [Fact]
    public async Task Reindex_Replaces_Title_Body_And_Company()
    {
        var search = new InMemorySearchService();
        var tenantId = Guid.NewGuid();
        var id = Guid.NewGuid();
        await search.IndexDocumentAsync(new SearchDocument(id, "Old title", "old body", Guid.NewGuid(), tenantId));

        var companyId = Guid.NewGuid();
        await search.IndexDocumentAsync(new SearchDocument(id, "New title", "new body", companyId, tenantId));

        Assert.Empty(await search.SearchAsync("Old", tenantId));
        var hit = Assert.Single(await search.SearchAsync("new body", tenantId));
        Assert.Equal("New title", hit.Title);
        Assert.Equal(companyId, hit.CompanyId);
    }

    [Fact]
    public void Config_Placeholders_Are_IndexName_Endpoint_And_Key_Vault_Secret_Name()
    {
        var configuration = _host.Services.GetRequiredService<IConfiguration>();
        var options = _host.Services.GetRequiredService<IOptions<AzureSearchOptions>>().Value;

        Assert.Equal("Azure:Search", AzureSearchOptions.SectionName);
        Assert.Equal(string.Empty, configuration["Azure:Search:IndexName"]);
        Assert.Equal(string.Empty, configuration["Azure:Search:Endpoint"]);
        Assert.Equal(string.Empty, configuration["Azure:Search:ApiKeySecretName"]);
        Assert.Equal(string.Empty, options.IndexName);
        Assert.Equal(string.Empty, options.Endpoint);
        Assert.Equal(string.Empty, options.ApiKeySecretName);
        Assert.IsType<InMemorySearchService>(_host.Services.GetRequiredService<ISearchService>());
    }

    [Fact]
    public async Task Unauthenticated_Search_Returns_401()
    {
        using var client = _host.CreateAnonymousClient();

        var response = await client.GetAsync("/api/search?q=vpn");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_Search_Uses_ISearchService_And_Scopes_To_Caller_Tenant()
    {
        var marker = $"vpn-sop-{Guid.NewGuid():N}";
        Guid documentId;
        Guid companyId;

        using (var owner = _host.CreateOwnerClient())
        {
            var company = await owner.PostAsJsonAsync(
                "/api/companies",
                new CreateCompanyRequest("ExampleCo", $"exampleco-{Guid.NewGuid():N}"));
            Assert.Equal(HttpStatusCode.Created, company.StatusCode);
            var companyBody = await company.Content.ReadFromJsonAsync<CreatedCompanyResponse>(JsonOptions);
            Assert.NotNull(companyBody);
            companyId = companyBody.Id;

            var created = await owner.PostAsJsonAsync(
                "/api/documents",
                new CreateDocumentRequest(
                    marker,
                    marker,
                    null,
                    "Split-tunnel body for search",
                    null,
                    true,
                    companyId));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var createdBody = await created.Content.ReadFromJsonAsync<CreatedDocumentResponse>(JsonOptions);
            Assert.NotNull(createdBody);
            documentId = createdBody.Id;

            var found = await owner.GetFromJsonAsync<List<SearchHit>>($"/api/search?q={marker}", JsonOptions);
            var hit = Assert.Single(found!);
            Assert.Equal(documentId, hit.Id);
            Assert.Equal(marker, hit.Title);
            Assert.Equal("Split-tunnel body for search", hit.Body);
            Assert.Equal(companyId, hit.CompanyId);
            Assert.Equal(_host.TenantAId, hit.TenantId);

            Assert.Empty(await owner.GetFromJsonAsync<List<SearchHit>>("/api/search?q=", JsonOptions) ?? []);
        }

        using var otherTenant = _host.CreateAuthenticatedClient(Guid.NewGuid().ToString(), _host.TenantBId);
        var leaked = await otherTenant.GetFromJsonAsync<List<SearchHit>>($"/api/search?q={marker}", JsonOptions);
        Assert.NotNull(leaked);
        Assert.Empty(leaked);
    }

    private sealed record CreatedCompanyResponse(Guid Id);

    private sealed record CreatedDocumentResponse(Guid Id);
}
