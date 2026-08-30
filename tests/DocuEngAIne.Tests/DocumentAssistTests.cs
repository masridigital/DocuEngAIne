using System.Net;
using System.Net.Http.Json;
using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DocuEngAIne.Tests;

/// <summary>
/// HTTP coverage for <c>POST /api/documents/{id}/assist</c>. Uses <see cref="StubLlmClient"/>
/// so no test opens a socket to Ollama, Together, or Anthropic.
/// </summary>
[Collection("LlmEndpoints")]
public class DocumentAssistTests : IClassFixture<TestHost>
{
    private readonly TestHost _host;

    public DocumentAssistTests(TestHost host) => _host = host;

    [Fact]
    public async Task Assist_Without_Auth_Returns_401()
    {
        var stub = _host.Services.GetRequiredService<StubLlmClient>();
        stub.Reset();
        using var client = _host.CreateAnonymousClient();

        var response = await client.PostAsJsonAsync(
            $"/api/documents/{Guid.NewGuid()}/assist",
            new { action = "summarize" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, stub.CallCount);
    }

    [Fact]
    public async Task Assist_Other_Tenant_Document_Returns_404()
    {
        var foreignId = await SeedDocumentAsync(_host.TenantBId, "Foreign", "other-tenant-body");
        var stub = _host.Services.GetRequiredService<StubLlmClient>();
        stub.Reset();

        using var client = _host.CreateOwnerClient();
        var response = await client.PostAsJsonAsync(
            $"/api/documents/{foreignId}/assist",
            new { action = "summarize" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, stub.CallCount);
    }

    [Fact]
    public async Task Summarize_Returns_Stub_Content()
    {
        const string body = "vpn-cutover-runbook-body";
        var id = await SeedDocumentAsync(_host.TenantAId, "VPN cutover", body);
        var stub = _host.Services.GetRequiredService<StubLlmClient>();
        stub.Reset();
        stub.Result = new("stub-summary", "llama3.1", DocuEngAIne.Core.Enums.LlmProvider.Ollama);

        using var client = _host.CreateOwnerClient();
        var response = await client.PostAsJsonAsync(
            $"/api/documents/{id}/assist",
            new { action = "summarize" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<DocumentAssistResponse>();
        Assert.NotNull(payload);
        Assert.Equal("stub-summary", payload.Content);
        Assert.Equal("llama3.1", payload.Model);
        Assert.Equal("Ollama", payload.Provider);

        Assert.Equal(1, stub.CallCount);
        Assert.NotNull(stub.LastMessages);
        Assert.Contains(stub.LastMessages, m => m.Role == "system" && m.Content.Contains("MSP documentation"));
        Assert.Contains(stub.LastMessages, m => m.Role == "system" && m.Content.Contains("secrets"));
        Assert.Contains(stub.LastMessages, m => m.Role == "system" && m.Content.Contains("credentials"));
        Assert.Contains(stub.LastMessages, m => m.Role == "user" && m.Content == body);
    }

    [Fact]
    public async Task Rewrite_Passes_Instruction()
    {
        const string body = "original-procedure";
        const string instruction = "shorten-for-on-call";
        var id = await SeedDocumentAsync(_host.TenantAId, "Procedure", body);
        var stub = _host.Services.GetRequiredService<StubLlmClient>();
        stub.Reset();
        stub.Result = new("rewritten-procedure", "llama3.1", DocuEngAIne.Core.Enums.LlmProvider.Ollama);

        using var client = _host.CreateOwnerClient();
        var response = await client.PostAsJsonAsync(
            $"/api/documents/{id}/assist",
            new { action = "rewrite", instruction });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<DocumentAssistResponse>();
        Assert.NotNull(payload);
        Assert.Equal("rewritten-procedure", payload.Content);

        Assert.Equal(1, stub.CallCount);
        Assert.NotNull(stub.LastMessages);
        Assert.Contains(stub.LastMessages, m => m.Role == "user" && m.Content == body);
        Assert.Contains(stub.LastMessages, m => m.Role == "user" && m.Content == instruction);
    }

    [Fact]
    public async Task Apply_False_Does_Not_Create_Version()
    {
        const string body = "do-not-version-this";
        var id = await SeedDocumentAsync(_host.TenantAId, "Preview only", body);
        var stub = _host.Services.GetRequiredService<StubLlmClient>();
        stub.Reset();
        stub.Result = new("preview-only-text", "llama3.1", DocuEngAIne.Core.Enums.LlmProvider.Ollama);

        using var client = _host.CreateOwnerClient();
        var response = await client.PostAsJsonAsync(
            $"/api/documents/{id}/assist",
            new { action = "rewrite", apply = false });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocuEngAIneDbContext>();
        Assert.Empty(db.DocumentVersions.Where(v => v.DocumentId == id));
        var doc = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == id);
        Assert.Equal(body, doc.Content);
    }

    [Fact]
    public async Task Assist_Reader_Returns_403()
    {
        var id = await SeedDocumentAsync(_host.TenantAId, "Reader blocked", "body");
        var stub = _host.Services.GetRequiredService<StubLlmClient>();
        stub.Reset();

        using var client = _host.CreateReaderClient();
        var response = await client.PostAsJsonAsync(
            $"/api/documents/{id}/assist",
            new { action = "summarize" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, stub.CallCount);
    }

    private async Task<Guid> SeedDocumentAsync(Guid tenantId, string title, string content)
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocuEngAIneDbContext>();
        var doc = new Document
        {
            TenantId = tenantId,
            Title = title,
            Slug = $"{title.ToLowerInvariant().Replace(' ', '-')}-{Guid.NewGuid():N}",
            Content = content,
            IsPublished = true,
        };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return doc.Id;
    }
}
