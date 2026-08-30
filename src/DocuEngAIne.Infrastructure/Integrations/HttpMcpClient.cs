using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Core.Mcp;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Speaks MCP Streamable HTTP so spec-conformant servers accept our calls: every request advertises
/// <c>Accept: application/json, text/event-stream</c> (servers answer 406 without it), the
/// <c>initialize</c> / <c>notifications/initialized</c> handshake runs before the first tool call, and the
/// <c>Mcp-Session-Id</c> the server hands back is echoed on every later request.
/// A server may answer either <c>application/json</c> or an SSE stream; SSE frames are unwrapped here so
/// callers always receive the same JSON-RPC string the mappers already know how to read.
/// The handshake is cached per server for the lifetime of this instance only — the client is registered
/// scoped, so the cache dies with the request and never leaks a session across tenants.
/// </summary>
public class HttpMcpClient : IMcpClient
{
    /// <summary>MCP protocol revision this client implements.</summary>
    private const string ProtocolVersion = "2025-06-18";
    private const string ProtocolVersionHeader = "MCP-Protocol-Version";
    private const string SessionIdHeader = "Mcp-Session-Id";
    private const string EventStreamMediaType = "text/event-stream";

    private readonly DocuEngAIneDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly IAuditService _audit;

    /// <summary>Handshake state per MCP server, scoped to this instance. Never make this static.</summary>
    private readonly Dictionary<Guid, McpSession> _sessions = new();

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
            cancellationToken,
            toolName);

        await _audit.LogAsync("Mcp.CallTool", nameof(Core.Entities.McpServer), mcpServerId, $"tool={toolName}", cancellationToken);
        return result;
    }

    private async Task<string> CallJsonRpcAsync(
        Guid mcpServerId,
        string method,
        object paramsObj,
        CancellationToken cancellationToken,
        string? toolName = null)
    {
        var server = await _db.McpServers.ForTenant(_user).FirstOrDefaultAsync(s => s.Id == mcpServerId, cancellationToken)
            ?? throw new InvalidOperationException("MCP server not found.");

        if (!server.Enabled)
            throw new InvalidOperationException("MCP server is disabled.");

        if (server.Transport is not (McpTransport.Http or McpTransport.Sse))
            throw new InvalidOperationException("Only HTTP/SSE MCP transports are supported in v1.");

        if (string.IsNullOrWhiteSpace(server.EndpointUrl))
            throw new InvalidOperationException("MCP server EndpointUrl is required.");

        // Composio is the second harness, not a Compact PSA/RMM stand-in. Refuse ads/social and
        // anything outside the allowlist before a token is resolved or a request is sent.
        if (server.Kind == McpServerKind.Composio
            && method == "tools/call"
            && !McpServerDefaults.IsAllowedComposioTool(toolName))
        {
            throw new InvalidOperationException(
                $"Composio tool '{toolName}' is outside the allowed toolkits (github, cloudflare, outlook, notion). Ads and social toolkits are skipped.");
        }

        // Resolve the secret before any network call: a misconfigured secret must not reach the vendor as a 401.
        var token = ResolveAuthToken(server);
        var session = await EnsureSessionAsync(server, server.EndpointUrl, token, cancellationToken);

        var requestId = Guid.NewGuid().ToString("N");
        var payload = new
        {
            jsonrpc = "2.0",
            id = requestId,
            method,
            @params = paramsObj,
        };

        using var response = await PostAsync(server.EndpointUrl, token, session, payload, includeProtocolVersion: true, cancellationToken);
        var body = await ReadBodyAsync(response, cancellationToken);

        if (IsEventStream(response))
        {
            body = ParseSseMessage(body, requestId)
                ?? throw new InvalidOperationException("MCP server returned an SSE response with no message event.");
        }

        // HTTP 200 still carries JSON-RPC errors and MCP tool isError payloads. Surface those here
        // so callers (and the mappers) do not treat a failed tool as an empty success.
        EnsureNoRpcFailure(body, "MCP call failed");

        if (server.Kind == McpServerKind.Composio && method == "tools/list")
            return McpServerDefaults.FilterComposioToolsList(body);

        return body;
    }

    /// <summary>
    /// Runs the MCP handshake once per server: <c>initialize</c>, capture <c>Mcp-Session-Id</c>, then the
    /// <c>notifications/initialized</c> notification (no JSON-RPC id, so the server replies 202 with no body).
    /// </summary>
    private async Task<McpSession> EnsureSessionAsync(McpServer server, string endpointUrl, string? token, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(server.Id, out var cached))
            return cached;

        var session = new McpSession();

        var initialize = new
        {
            jsonrpc = "2.0",
            id = Guid.NewGuid().ToString("N"),
            method = "initialize",
            @params = new
            {
                protocolVersion = ProtocolVersion,
                capabilities = new { },
                clientInfo = new { name = "DocuEngAIne", version = "1.0" },
            },
        };

        using (var response = await PostAsync(endpointUrl, token, session, initialize, includeProtocolVersion: false, cancellationToken))
        {
            var body = await ReadBodyAsync(response, cancellationToken);
            if (IsEventStream(response))
                body = ParseSseMessage(body, null) ?? body;

            // A server may reject initialize with HTTP 200 and a JSON-RPC error. Treating that as a
            // successful handshake would cache a dead session and fail every later call obscurely.
            session.ProtocolVersion = ReadInitializeResult(body);

            if (response.Headers.TryGetValues(SessionIdHeader, out var sessionIds))
            {
                var sessionId = sessionIds.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(sessionId))
                    session.SessionId = sessionId;
            }
        }

        var initialized = new
        {
            jsonrpc = "2.0",
            method = "notifications/initialized",
        };

        // notifications/initialized is fire-and-forget: it carries no JSON-RPC id and expects no result.
        // Servers that answer anything other than 2xx are still usable for tool calls, so a non-success
        // status here must not throw and take every later call down with it.
        using (var response = await PostAsync(endpointUrl, token, session, initialized, includeProtocolVersion: true, cancellationToken))
        {
            await response.Content.ReadAsStringAsync(cancellationToken);
        }

        _sessions[server.Id] = session;
        return session;
    }

    private async Task<HttpResponseMessage> PostAsync<TPayload>(
        string endpointUrl,
        string? token,
        McpSession session,
        TPayload payload,
        bool includeProtocolVersion,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(HttpMcpClient));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpointUrl);

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(EventStreamMediaType));

        if (includeProtocolVersion)
            request.Headers.TryAddWithoutValidation(ProtocolVersionHeader, session.ProtocolVersion ?? ProtocolVersion);

        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (!string.IsNullOrWhiteSpace(session.SessionId))
            request.Headers.TryAddWithoutValidation(SessionIdHeader, session.SessionId);

        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return await client.SendAsync(request, cancellationToken);
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"MCP call failed ({(int)response.StatusCode}): {body}");
        return body;
    }

    private static bool IsEventStream(HttpResponseMessage response)
        => string.Equals(response.Content.Headers.ContentType?.MediaType, EventStreamMediaType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the payload of the <c>message</c> event whose JSON-RPC <c>id</c> matches
    /// <paramref name="requestId"/>, falling back to the last <c>message</c> event when no frame matches
    /// (or when no id was supplied). Correlating by id matters because a server may emit notifications
    /// after the result, and handing one of those to the mappers would look like an empty response.
    /// Multi-line <c>data:</c> continuations are joined with a newline, per the SSE spec. A trailing block that
    /// is not terminated by a blank line is still dispatched — servers that close the stream without one are
    /// common enough that discarding the reply would be worse than accepting it.
    /// </summary>
    private static string? ParseSseMessage(string body, string? requestId)
    {
        string? matched = null;
        string? lastMessage = null;
        var eventName = "message";
        var data = new StringBuilder();
        var hasData = false;

        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (line.Length == 0)
            {
                if (hasData && eventName == "message")
                {
                    lastMessage = data.ToString();
                    if (matched is null && MatchesRequestId(lastMessage, requestId))
                        matched = lastMessage;
                }
                eventName = "message";
                data.Clear();
                hasData = false;
                continue;
            }

            if (line[0] == ':')
                continue;

            var separator = line.IndexOf(':');
            var field = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? string.Empty : line[(separator + 1)..];
            if (value.StartsWith(' '))
                value = value[1..];

            if (field == "event")
            {
                eventName = value;
            }
            else if (field == "data")
            {
                if (hasData)
                    data.Append('\n');
                data.Append(value);
                hasData = true;
            }
        }

        if (hasData && eventName == "message")
        {
            lastMessage = data.ToString();
            if (matched is null && MatchesRequestId(lastMessage, requestId))
                matched = lastMessage;
        }

        return matched ?? lastMessage;
    }

    private static bool MatchesRequestId(string frame, string? requestId)
    {
        if (string.IsNullOrEmpty(requestId))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(frame);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                && string.Equals(id.GetString(), requestId, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the negotiated protocol version out of an <c>initialize</c> result, throwing when the server
    /// answered with a JSON-RPC error instead.
    /// </summary>
    private static string? ReadInitializeResult(string body)
    {
        EnsureNoRpcFailure(body, "MCP initialize failed");

        if (!TryParseObject(body, out var root))
        {
            // A server that answers initialize with something unparsable is still worth trying for
            // tool calls; the tool call itself will surface a clearer failure.
            return null;
        }

        if (root.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("protocolVersion", out var version)
            && version.ValueKind == JsonValueKind.String)
        {
            return version.GetString();
        }

        return null;
    }

    /// <summary>
    /// Throws when the body is a JSON-RPC error object or an MCP tool result with <c>isError: true</c>.
    /// Unparsable bodies are left to the caller — Compact sometimes wraps the real payload as text,
    /// and the mappers already know how to unwrap that.
    /// </summary>
    private static void EnsureNoRpcFailure(string body, string prefix)
    {
        if (!TryParseObject(body, out var root))
            return;

        if (root.TryGetProperty("error", out var error))
        {
            var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m)
                ? m.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"{prefix}: {message}");
        }

        var payload = root;
        if (root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
            payload = result;

        if (payload.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
        {
            var errText = ReadContentText(payload) ?? payload.GetRawText();
            throw new InvalidOperationException($"{prefix}: {errText}");
        }
    }

    private static bool TryParseObject(string body, out JsonElement root)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            root = doc.RootElement.Clone();
            return root.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            root = default;
            return false;
        }
    }

    private static string? ReadContentText(JsonElement payload)
    {
        if (!payload.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString();
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves the bearer token for a server. A configured secret name that resolves to nothing is a
    /// deployment mistake, so it throws here rather than becoming an opaque 401 from the vendor.
    /// </summary>
    private string? ResolveAuthToken(McpServer server)
    {
        if (string.IsNullOrWhiteSpace(server.AuthSecretName))
            return null;

        var token = FirstNonEmpty(
            _configuration[server.AuthSecretName],
            _configuration[$"KeyVault:{server.AuthSecretName}"],
            Environment.GetEnvironmentVariable(server.AuthSecretName.Replace('-', '_').ToUpperInvariant()));

        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException($"MCP server auth secret '{server.AuthSecretName}' did not resolve to a value.");

        return token;
    }

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;
        }

        return null;
    }

    private sealed class McpSession
    {
        public string? SessionId { get; set; }

        /// <summary>Version the server named at initialize, echoed on later requests. Null falls back to ours.</summary>
        public string? ProtocolVersion { get; set; }
    }
}
