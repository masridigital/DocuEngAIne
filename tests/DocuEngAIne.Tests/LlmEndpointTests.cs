using System.Net;
using System.Net.Http.Json;
using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;

namespace DocuEngAIne.Tests;

[CollectionDefinition("LlmEndpoints", DisableParallelization = true)]
public class LlmEndpointsCollection;

[Collection("LlmEndpoints")]
public class LlmEndpointTests : IClassFixture<TestHost>
{
    private readonly TestHost _host;

    public LlmEndpointTests(TestHost host) => _host = host;

    [Fact]
    public async Task Chat_Without_Auth_Returns_401()
    {
        using var client = _host.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            "/api/llm/chat",
            new LlmChatRequest([new LlmChatMessageRequest("user", "hello")]));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Config_Without_Auth_Returns_401()
    {
        using var client = _host.CreateAnonymousClient();

        var response = await client.GetAsync("/api/llm/config");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Config_Returns_Provider_And_Model_Without_Secrets()
    {
        using var client = _host.CreateOwnerClient();

        var response = await client.GetAsync("/api/llm/config");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        var body = System.Text.Json.JsonSerializer.Deserialize<LlmConfigResponse>(
            json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(body);
        Assert.Equal("Ollama", body.Provider);
        Assert.Equal("llama3.1", body.Model);
        Assert.DoesNotContain("TogetherApiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AnthropicApiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Chat_Is_Tenant_Scoped_Audit_And_Does_Not_Persist_Prompt()
    {
        var stub = _host.Services.GetRequiredService<StubLlmClient>();
        stub.Reset();
        stub.Result = new("ok", "llama3.1", DocuEngAIne.Core.Enums.LlmProvider.Ollama);

        const string prompt = "unique-tenant-a-prompt-body-not-for-sql";
        using var client = _host.CreateOwnerClient();

        var response = await client.PostAsJsonAsync(
            "/api/llm/chat",
            new LlmChatRequest([new LlmChatMessageRequest("user", prompt)]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LlmChatResponse>();
        Assert.NotNull(body);
        Assert.Equal("ok", body.Content);
        Assert.Equal("llama3.1", body.Model);
        Assert.Equal("Ollama", body.Provider);

        Assert.Equal(1, stub.CallCount);
        Assert.NotNull(stub.LastMessages);
        Assert.Equal(prompt, stub.LastMessages![0].Content);

        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocuEngAIneDbContext>();
        var audits = db.AuditLogs.Where(a => a.Action == "Llm.Chat").ToList();
        Assert.Contains(audits, a => a.TenantId == _host.TenantAId);
        Assert.DoesNotContain(audits, a => a.TenantId == _host.TenantBId && a.Details != null && a.Details.Contains(prompt));
        Assert.All(audits, a => Assert.DoesNotContain(prompt, a.Details ?? string.Empty));
        Assert.DoesNotContain(db.Documents.AsEnumerable(), d => (d.Content ?? string.Empty).Contains(prompt));
        Assert.DoesNotContain(db.Companies.AsEnumerable(), c => (c.Notes ?? string.Empty).Contains(prompt));
    }

    [Fact]
    public async Task Chat_From_Other_Tenant_Does_Not_See_Tenant_A_Audit_Details()
    {
        using var tenantB = _host.CreateAuthenticatedClient(Guid.NewGuid().ToString(), _host.TenantBId, "Owner");

        var response = await tenantB.PostAsJsonAsync(
            "/api/llm/chat",
            new LlmChatRequest([new LlmChatMessageRequest("user", "tenant-b-only")]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocuEngAIneDbContext>();
        var tenantBAudits = db.AuditLogs
            .Where(a => a.Action == "Llm.Chat" && a.TenantId == _host.TenantBId)
            .ToList();
        Assert.NotEmpty(tenantBAudits);
        Assert.All(tenantBAudits, a => Assert.DoesNotContain("unique-tenant-a-prompt-body-not-for-sql", a.Details ?? string.Empty));
    }

    [Fact]
    public async Task Chat_Empty_Messages_Returns_400()
    {
        using var client = _host.CreateOwnerClient();

        var response = await client.PostAsJsonAsync(
            "/api/llm/chat",
            new LlmChatRequest([]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
