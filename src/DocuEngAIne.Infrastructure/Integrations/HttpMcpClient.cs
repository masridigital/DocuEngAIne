using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace DocuEngAIne.Infrastructure.Integrations;

public class HttpMcpClient : IMcpClient
{
    private readonly DocuEngAIneDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IAuditService _audit;

    public HttpMcpClient(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IAuditService audit)
    {
        _db = db;
        _user = user;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _audit = audit;
    }

    public async Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
    {
        return await CallJsonRpcAsync(mcpServerId, "tools/list", new { }, cancellationToken);
    }

    public async Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
    {
        object? args = null;
        if (!string.IsNullOrWhiteSpace(argumentsJson))
            args = JsonSerializer.Deserialize<JsonElement>(argumentsJson);

        var result = await CallJsonRpcAsync(
            mcpServerId,
            "tools/call",
            new { name = toolName, arguments = args },
            cancellationToken);

        await _audit.LogAsync("Mcp.CallTool", nameof(Core.Entities.McpServer), mcpServerId, $"tool={toolName}", cancellationToken);
        return result;
    }

    private async Task<string> CallJsonRpcAsync(Guid mcpServerId, string method, object paramsObj, CancellationToken cancellationToken)
    {
        var server = await _db.McpServers.ForTenant(_user).FirstOrDefaultAsync(s => s.Id == mcpServerId, cancellationToken)
            ?? throw new InvalidOperationException("MCP server not found.");

        if (!server.Enabled)
            throw new InvalidOperationException("MCP server is disabled.");

        if (server.Transport is not (McpTransport.Http or McpTransport.Sse))
            throw new InvalidOperationException("Only HTTP/SSE MCP transports are supported in v1.");

        if (string.IsNullOrWhiteSpace(server.EndpointUrl))
            throw new InvalidOperationException("MCP server EndpointUrl is required.");

        var client = _httpClientFactory.CreateClient(nameof(HttpMcpClient));
        using var request = new HttpRequestMessage(HttpMethod.Post, server.EndpointUrl);

        if (!string.IsNullOrWhiteSpace(server.AuthSecretName))
        {
            var token = _configuration[server.AuthSecretName]
                ?? _configuration[$"KeyVault:{server.AuthSecretName}"]
                ?? Environment.GetEnvironmentVariable(server.AuthSecretName.Replace('-', '_').ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var payload = new
        {
            jsonrpc = "2.0",
            id = Guid.NewGuid().ToString("N"),
            method,
            @params = paramsObj,
        };

        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"MCP call failed ({(int)response.StatusCode}): {body}");
        return body;
    }
}
