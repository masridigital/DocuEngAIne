using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace DocuEngAIne.Tests;

public class HttpMcpClientTests
{
    private const string Endpoint = "https://mcp.example.test/mcp";
    private const string ToolResultJson = """{"jsonrpc":"2.0","id":"1","result":{"content":[{"type":"text","text":"[]"}]}}""";

    private sealed class NoopAudit : IAuditService
    {
        public Task LogAsync(string action, string entityType, Guid? entityId = null, string? details = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed record RecordedRequest(HttpRequestHeaders Headers, string Body);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public List<RecordedRequest> Requests { get; } = [];

        public StubHandler Enqueue(HttpResponseMessage response)
        {
            _responses.Enqueue(response);
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Read the body now: the client disposes the request (and its content) as soon as this returns.
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Headers, body));

            if (_responses.Count == 0)
                throw new InvalidOperationException("StubHandler received more requests than were queued.");

            return _responses.Dequeue();
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static HttpResponseMessage Json(string body, string? sessionId = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (sessionId is not null)
            response.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        return response;
    }

    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
    };

    private static HttpResponseMessage Accepted() => new(HttpStatusCode.Accepted)
    {
        Content = new StringContent(string.Empty),
    };

    private static HttpResponseMessage InitializeOk(string? sessionId = null)
        => Json("""{"jsonrpc":"2.0","id":"0","result":{"protocolVersion":"2025-06-18","capabilities":{},"serverInfo":{"name":"stub","version":"1.0"}}}""", sessionId);

    private static IConfiguration BuildConfiguration(params (string Key, string Value)[] values)
    {
        var settings = new Dictionary<string, string?>();
        foreach (var (key, value) in values)
            settings[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static (DocuEngAIneDbContext Db, FakeCurrentUser User) CreateDb()
    {
        var user = new FakeCurrentUser { TenantId = Guid.NewGuid(), ObjectId = Guid.NewGuid().ToString(), Role = UserRole.Owner };
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return (new DocuEngAIneDbContext(options, user), user);
    }

    private static async Task<McpServer> SeedServerAsync(DocuEngAIneDbContext db, FakeCurrentUser user, string? authSecretName = null)
    {
        var server = new McpServer
        {
            TenantId = user.TenantId!.Value,
            Name = "StackJack Compact",
            Kind = McpServerKind.StackJackCompact,
            Transport = McpTransport.Http,
            EndpointUrl = Endpoint,
            AuthSecretName = authSecretName,
        };
        db.McpServers.Add(server);
        await db.SaveChangesAsync();
        return server;
    }

    private static HttpMcpClient CreateClient(
        DocuEngAIneDbContext db,
        FakeCurrentUser user,
        StubHandler handler,
        IConfiguration? configuration = null)
        => new(db, user, new StubHttpClientFactory(handler), configuration ?? BuildConfiguration(), new NoopAudit());

    private static string? Header(HttpRequestHeaders headers, string name)
        => headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    private static void AssertStreamableHttpAccept(HttpRequestHeaders headers)
    {
        var accept = headers.Accept.Select(a => a.MediaType ?? string.Empty).ToArray();
        Assert.Contains("application/json", accept);
        Assert.Contains("text/event-stream", accept);
    }

    [Fact]
    public async Task Every_Request_Sends_Streamable_Http_Accept_Header()
    {
        var (db, user) = CreateDb();
        var server = await SeedServerAsync(db, user);
        var handler = new StubHandler()
            .Enqueue(InitializeOk())
            .Enqueue(Accepted())
            .Enqueue(Json(ToolResultJson));
        var client = CreateClient(db, user, handler);

        await client.CallToolAsync(server.Id, "halo_list_clients", null);

        Assert.Equal(3, handler.Requests.Count);
        foreach (var request in handler.Requests)
            AssertStreamableHttpAccept(request.Headers);
    }

    [Fact]
    public async Task Initialize_And_Initialized_Notification_Precede_First_Tool_Call()
    {
        var (db, user) = CreateDb();
        var server = await SeedServerAsync(db, user);
        var handler = new StubHandler()
            .Enqueue(InitializeOk())
            .Enqueue(Accepted())
            .Enqueue(Json(ToolResultJson));
        var client = CreateClient(db, user, handler);

        await client.CallToolAsync(server.Id, "halo_list_clients", null);

        Assert.Equal(3, handler.Requests.Count);

        Assert.Contains("\"method\":\"initialize\"", handler.Requests[0].Body);
        Assert.Contains("\"protocolVersion\":\"2025-06-18\"", handler.Requests[0].Body);
        Assert.Contains("\"name\":\"DocuEngAIne\"", handler.Requests[0].Body);

        Assert.Contains("\"method\":\"notifications/initialized\"", handler.Requests[1].Body);
        Assert.DoesNotContain("\"id\"", handler.Requests[1].Body);

        Assert.Contains("\"method\":\"tools/call\"", handler.Requests[2].Body);
        Assert.Contains("\"name\":\"halo_list_clients\"", handler.Requests[2].Body);
    }

    [Fact]
    public async Task Protocol_Version_Header_Is_Sent_On_Requests_After_Initialize()
    {
        var (db, user) = CreateDb();
        var server = await SeedServerAsync(db, user);
        var handler = new StubHandler()
            .Enqueue(InitializeOk())
            .Enqueue(Accepted())
            .Enqueue(Json(ToolResultJson));
        var client = CreateClient(db, user, handler);

        await client.CallToolAsync(server.Id, "halo_list_clients", null);

        Assert.Null(Header(handler.Requests[0].Headers, "MCP-Protocol-Version"));
        Assert.Equal("2025-06-18", Header(handler.Requests[1].Headers, "MCP-Protocol-Version"));
        Assert.Equal("2025-06-18", Header(handler.Requests[2].Headers, "MCP-Protocol-Version"));
    }

    [Fact]
    public async Task Session_Id_From_Initialize_Is_Echoed_On_Later_Requests()
    {
        var (db, user) = CreateDb();
        var server = await SeedServerAsync(db, user);
        var handler = new StubHandler()
            .Enqueue(InitializeOk("session-abc"))
            .Enqueue(Accepted())
            .Enqueue(Json(ToolResultJson));
        var client = CreateClient(db, user, handler);

        await client.CallToolAsync(server.Id, "halo_list_clients", null);

        Assert.Null(Header(handler.Requests[0].Headers, "Mcp-Session-Id"));
        Assert.Equal("session-abc", Header(handler.Requests[1].Headers, "Mcp-Session-Id"));
        Assert.Equal("session-abc", Header(handler.Requests[2].Headers, "Mcp-Session-Id"));
    }

    [Fact]
    public async Task Sse_Response_Is_Unwrapped_To_The_Last_Message_Payload()
    {
        var (db, user) = CreateDb();
        var server = await SeedServerAsync(db, user);
        var sse = ": keep-alive\n"
            + "event: ping\ndata: {\"ignored\":true}\n\n"
            + "event: message\ndata: " + ToolResultJson + "\n\n";
        var handler = new StubHandler()
            .Enqueue(InitializeOk())
            .Enqueue(Accepted())
            .Enqueue(Sse(sse));
        var client = CreateClient(db, user, handler);

        var result = await client.CallToolAsync(server.Id, "halo_list_clients", null);

        Assert.Equal(ToolResultJson, result);
    }

    [Fact]
    public async Task Sse_Multiline_Data_Fields_Are_Joined_With_Newlines()
    {
        var (db, user) = CreateDb();
        var server = await SeedServerAsync(db, user);
        var sse = "data: {\"jsonrpc\":\"2.0\",\r\ndata: \"id\":\"1\",\r\ndata: \"result\":{}}\r\n\r\n";
        var handler = new StubHandler()
            .Enqueue(InitializeOk())
            .Enqueue(Accepted())
            .Enqueue(Sse(sse));
        var client = CreateClient(db, user, handler);

        var result = await client.CallToolAsync(server.Id, "halo_list_clients", null);

        Assert.Equal("{\"jsonrpc\":\"2.0\",\n\"id\":\"1\",\n\"result\":{}}", result);
    }

    [Fact]
    public async Task Json_Response_Is_Returned_Unchanged()
    {
        var (db, user) = CreateDb();
        var server = await SeedServerAsync(db, user);
        var handler = new StubHandler()
            .Enqueue(InitializeOk())
            .Enqueue(Accepted())
            .Enqueue(Json(ToolResultJson));
        var client = CreateClient(db, user, handler);

        var result = await client.CallToolAsync(server.Id, "halo_list_clients", null);

        Assert.Equal(ToolResultJson, result);
    }

    [Fact]
    public async Task Resolved_Auth_Secret_Is_Sent_As_Bearer_Token()
    {
        var (db, user) = CreateDb();
        var server = await SeedServerAsync(db, user, "kv-stackjack-compact");
        var handler = new StubHandler()
            .Enqueue(InitializeOk())
            .Enqueue(Accepted())
            .Enqueue(Json(ToolResultJson));
        var client = CreateClient(db, user, handler, BuildConfiguration(("kv-stackjack-compact", "s3cret")));

        await client.CallToolAsync(server.Id, "halo_list_clients", null);

        Assert.Equal(3, handler.Requests.Count);
        foreach (var request in handler.Requests)
        {
            Assert.NotNull(request.Headers.Authorization);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("s3cret", request.Headers.Authorization.Parameter);
        }
    }

    [Fact]
    public async Task Unresolved_Auth_Secret_Throws_Before_Any_Request_Is_Sent()
    {
        var (db, user) = CreateDb();
        // Unique name so no ambient environment variable can satisfy the lookup.
        var secretName = "kv-missing-" + Guid.NewGuid().ToString("N");
        var server = await SeedServerAsync(db, user, secretName);
        var handler = new StubHandler();
        var client = CreateClient(db, user, handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await client.CallToolAsync(server.Id, "halo_list_clients", null));

        Assert.Contains(secretName, ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Handshake_Runs_Once_Per_Server_Across_Multiple_Calls()
    {
        var (db, user) = CreateDb();
        var server = await SeedServerAsync(db, user);
        var handler = new StubHandler()
            .Enqueue(InitializeOk("session-abc"))
            .Enqueue(Accepted())
            .Enqueue(Json(ToolResultJson))
            .Enqueue(Json(ToolResultJson));
        var client = CreateClient(db, user, handler);

        await client.CallToolAsync(server.Id, "halo_list_clients", null);
        await client.ListToolsAsync(server.Id);

        Assert.Equal(4, handler.Requests.Count);
        Assert.Single(handler.Requests, r => r.Body.Contains("\"method\":\"initialize\""));
        Assert.Single(handler.Requests, r => r.Body.Contains("\"method\":\"notifications/initialized\""));
        Assert.Contains("\"method\":\"tools/list\"", handler.Requests[3].Body);
        Assert.Equal("session-abc", Header(handler.Requests[3].Headers, "Mcp-Session-Id"));
    }

    [Fact]
    public async Task Initialize_Json_Rpc_Error_Throws_Without_Sending_Later_Requests()
    {
        var (db, user) = CreateDb();
        var server = await SeedServerAsync(db, user);
        var handler = new StubHandler()
            .Enqueue(Json("""{"jsonrpc":"2.0","id":"0","error":{"code":-32602,"message":"Unsupported protocol version"}}"""));
        var client = CreateClient(db, user, handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await client.CallToolAsync(server.Id, "halo_list_clients", null));

        Assert.Contains("Unsupported protocol version", ex.Message);
        Assert.Single(handler.Requests);
        Assert.Contains("\"method\":\"initialize\"", handler.Requests[0].Body);
    }

    [Fact]
    public async Task Tool_Call_Json_Rpc_Error_Surfaces_As_InvalidOperationException()
    {
        var (db, user) = CreateDb();
        var server = await SeedServerAsync(db, user);
        var handler = new StubHandler()
            .Enqueue(InitializeOk())
            .Enqueue(Accepted())
            .Enqueue(Json("""{"jsonrpc":"2.0","id":"1","error":{"code":-32603,"message":"halo_list_clients is not available"}}"""));
        var client = CreateClient(db, user, handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await client.CallToolAsync(server.Id, "halo_list_clients", null));

        Assert.Contains("halo_list_clients is not available", ex.Message);
    }

    [Fact]
    public async Task Tool_Call_IsError_Surfaces_As_InvalidOperationException()
    {
        var (db, user) = CreateDb();
        var server = await SeedServerAsync(db, user);
        var handler = new StubHandler()
            .Enqueue(InitializeOk())
            .Enqueue(Accepted())
            .Enqueue(Json("""{"jsonrpc":"2.0","id":"1","result":{"isError":true,"content":[{"type":"text","text":"connector not subscribed"}]}}"""));
        var client = CreateClient(db, user, handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await client.CallToolAsync(server.Id, "halo_list_clients", null));

        Assert.Contains("connector not subscribed", ex.Message);
    }

    [Fact]
    public async Task Sse_Wrapped_Json_Rpc_Error_Surfaces_As_InvalidOperationException()
    {
        var (db, user) = CreateDb();
        var server = await SeedServerAsync(db, user);
        var sse = "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":\"1\",\"error\":{\"code\":-32000,\"message\":\"sse boom\"}}\n\n";
        var handler = new StubHandler()
            .Enqueue(InitializeOk())
            .Enqueue(Accepted())
            .Enqueue(Sse(sse));
        var client = CreateClient(db, user, handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await client.CallToolAsync(server.Id, "halo_list_clients", null));

        Assert.Contains("sse boom", ex.Message);
    }

    [Fact]
    public async Task Sse_Unwrapped_Body_Is_Json_Mappers_Can_Read()
    {
        var (db, user) = CreateDb();
        var server = await SeedServerAsync(db, user);
        var inner = """{"clients":[{"id":12,"name":"Masri","inactive":false}]}""";
        var rpc = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new
            {
                content = new[] { new { type = "text", text = inner } },
            },
        });
        var sse = ": keep-alive\nevent: message\ndata: " + rpc + "\n\n";
        var handler = new StubHandler()
            .Enqueue(InitializeOk())
            .Enqueue(Accepted())
            .Enqueue(Sse(sse));
        var client = CreateClient(db, user, handler);

        var result = await client.CallToolAsync(server.Id, "halo_list_clients", null);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        var company = Assert.Single(HaloClientMapper.MapClients(result));
        Assert.Equal("12", company.ExternalId);
        Assert.Equal("Masri", company.Name);
    }
}
