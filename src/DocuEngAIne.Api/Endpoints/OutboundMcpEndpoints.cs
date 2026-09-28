using System.Text.Json;
using DocuEngAIne.Api.Mcp;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;

namespace DocuEngAIne.Api.Endpoints;

/// <summary>
/// Streamable HTTP MCP endpoint at <c>/mcp</c>. This is the outbound server — other harnesses call
/// us. Auth is a per-tenant API token, not an Entra browser JWT. POST is the protocol; GET documents
/// the surface without requiring a token.
/// </summary>
public static class OutboundMcpEndpoints
{
    public const string ProtocolVersionHeader = "MCP-Protocol-Version";
    public const string SessionIdHeader = "Mcp-Session-Id";
    public const string EventStreamMediaType = "text/event-stream";

    public static IEndpointRouteBuilder MapOutboundMcpEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/mcp").AllowAnonymous();

        group.MapGet("", () => Results.Json(Describe(), DocuEngAIneMcpServer.JsonOptions));

        group.MapPost("", HandlePostAsync);

        return app;
    }

    public static object Describe() => new
    {
        name = DocuEngAIneMcpServer.ServerName,
        version = DocuEngAIneMcpServer.ServerVersion,
        transport = "Streamable HTTP",
        protocolVersion = DocuEngAIneMcpServer.ProtocolVersion,
        endpoint = "/mcp",
        authentication = new
        {
            type = "apiToken",
            header = "Authorization: Bearer <token>",
            alternateHeader = "X-Api-Token",
            note = "Per-tenant API token minted at POST /api/tokens. MCP is not a browser JWT. Hash is stored; plaintext is shown once.",
        },
        methods = new[] { "initialize", "notifications/initialized", "tools/list", "tools/call", "ping" },
        tools = DocuEngAIneMcpServer.Tools.Select(t => t.Name).ToArray(),
        notes = new[]
        {
            "Read-only. Every tool query is ForTenant on the token identity.",
            "list_keeper_links returns titles and ids only. reveal_keeper_link returns one link's Keeper URL and writes a KeeperLink.Reveal audit row.",
            "POST application/json JSON-RPC 2.0. Accept: application/json, text/event-stream.",
            "A text/event-stream-only Accept wraps the JSON-RPC result as a single SSE message event.",
        },
    };

    public static async Task<IResult> HandlePostAsync(
        HttpContext http,
        DocuEngAIneDbContext db,
        IAuditService audit,
        CancellationToken cancellationToken)
    {
        var presented = ApiTokenAuthenticator.ReadPresentedToken(
            http.Request.Headers.Authorization,
            http.Request.Headers["X-Api-Token"]);

        var user = await ApiTokenAuthenticator.AuthenticateAsync(presented, db, cancellationToken);
        if (user is null)
            return Results.Unauthorized();

        using var scope = CurrentUserScope.Use(user);

        JsonElement root;
        try
        {
            root = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return Results.Json(
                DocuEngAIneMcpServer.Error(null, -32700, "Parse error"),
                DocuEngAIneMcpServer.JsonOptions);
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            return Results.Json(
                DocuEngAIneMcpServer.Error(null, -32600, "JSON-RPC batches are not supported."),
                DocuEngAIneMcpServer.JsonOptions,
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return Results.Json(
                DocuEngAIneMcpServer.Error(null, -32600, "Invalid Request"),
                DocuEngAIneMcpServer.JsonOptions);
        }

        var request = DocuEngAIneMcpServer.Parse(root);
        if (DocuEngAIneMcpServer.IsNotification(request.Method, request.HasId))
            return Results.Accepted();

        var response = await DocuEngAIneMcpServer.HandleAsync(request, db, user, audit, cancellationToken);

        http.Response.Headers[ProtocolVersionHeader] = DocuEngAIneMcpServer.ProtocolVersion;
        if (!http.Response.Headers.ContainsKey(SessionIdHeader))
            http.Response.Headers[SessionIdHeader] = http.Request.Headers[SessionIdHeader].FirstOrDefault()
                ?? Guid.NewGuid().ToString("N");

        if (WantsEventStreamOnly(http.Request.Headers.Accept.ToString()))
        {
            var json = JsonSerializer.Serialize(response, DocuEngAIneMcpServer.JsonOptions);
            return Results.Text($"event: message\ndata: {json}\n\n", EventStreamMediaType);
        }

        return Results.Json(response, DocuEngAIneMcpServer.JsonOptions);
    }

    public static bool WantsEventStreamOnly(string? accept)
    {
        if (string.IsNullOrWhiteSpace(accept))
            return false;

        var wantsSse = accept.Contains(EventStreamMediaType, StringComparison.OrdinalIgnoreCase);
        var wantsJson = accept.Contains("application/json", StringComparison.OrdinalIgnoreCase)
            || accept.Contains("*/*", StringComparison.OrdinalIgnoreCase);
        return wantsSse && !wantsJson;
    }
}
