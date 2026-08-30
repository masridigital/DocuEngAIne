using System.Text.Json;
using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Core.Mcp;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Integrations;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

/// <summary>
/// StackJack Compact is a built-in connector: creating a Halo/NinjaOne/CIPP/Meraki/UniFi/Action1/Autotask/Blackpoint/DefensX/Pax8/Slide integration
/// asks for a provider and a Key Vault secret name, and the Compact MCP server behind it is resolved
/// -- or registered once -- on the admin's behalf. Also covers the plan detection that Test performs
/// and the cadence the detected allowance feeds.
/// </summary>
public class BuiltInCompactTests
{
    /// <summary>Shape of a stackjack_session_info response, trimmed to the connectors these tests use.</summary>
    private const string SessionInfoJson = """
        {"tenantId":"01TESTTENANT","connectors":[
          {"connector":"Halo","plan":"Business","monthlyCallLimit":50000,"hasCredentials":true},
          {"connector":"NinjaRMM","plan":"Free","monthlyCallLimit":100,"hasCredentials":false}],
          "toolSummary":{"total":11179,"accessible":5498}}
        """;

    private sealed class NoopAudit : IAuditService
    {
        public Task LogAsync(string action, string entityType, Guid? entityId = null, string? details = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>Answers tools/list, and returns <see cref="Body"/> as the text content of any tool call.</summary>
    private sealed class RecordingMcp : IMcpClient
    {
        public List<string> Calls { get; } = [];
        public string Body { get; init; } = SessionInfoJson;
        public bool FailToolCalls { get; init; }

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
        {
            Calls.Add("tools/list");
            return Task.FromResult("""{"result":{"tools":[]}}""");
        }

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add(toolName);
            if (FailToolCalls)
                throw new InvalidOperationException("Compact returned 503.");

            var body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "1",
                result = new { content = new[] { new { type = "text", text = Body } } },
            });
            return Task.FromResult(body);
        }
    }

    private static (DocuEngAIneDbContext Db, FakeCurrentUser User) Create()
        => Open(Guid.NewGuid().ToString(), Guid.NewGuid());

    private static (DocuEngAIneDbContext Db, FakeCurrentUser User) Open(string dbName, Guid tenantId)
    {
        var user = new FakeCurrentUser { TenantId = tenantId, ObjectId = Guid.NewGuid().ToString(), Role = UserRole.Owner };
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return (new DocuEngAIneDbContext(options, user), user);
    }

    private static async Task<McpServer> SeedCompactAsync(
        DocuEngAIneDbContext db, FakeCurrentUser user, string? authSecretName = "kv-stackjack-compact")
    {
        var server = new McpServer
        {
            TenantId = user.TenantId!.Value,
            Name = McpServerDefaults.StackJackCompactName,
            Kind = McpServerKind.StackJackCompact,
            Transport = McpTransport.Http,
            EndpointUrl = McpServerDefaults.StackJackCompactEndpoint,
            AuthSecretName = authSecretName,
        };
        db.McpServers.Add(server);
        await db.SaveChangesAsync();
        return server;
    }

    private static int StatusOf(IResult result)
        => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 0;

    private static JsonElement BodyOf(IResult result, out JsonDocument document)
    {
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result).Value;
        document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement;
    }

    [Fact]
    public async Task Compact_Provider_Registers_The_Built_In_Server_From_Nothing_But_A_Secret_Name()
    {
        var (db, user) = Create();
        await using (db)
        {
            var result = await IntegrationEndpoints.CreateIntegrationAsync(
                new CreateIntegrationRequest(IntegrationProvider.Halo, "Halo", AuthSecretName: "  kv-stackjack-compact  "),
                db, user);

            Assert.Equal(StatusCodes.Status201Created, StatusOf(result));

            var server = Assert.Single(await db.McpServers.ForTenant(user).ToListAsync());
            Assert.Equal(McpServerKind.StackJackCompact, server.Kind);
            Assert.Equal(McpServerDefaults.StackJackCompactEndpoint, server.EndpointUrl);
            Assert.Equal(McpServerDefaults.StackJackCompactName, server.Name);
            Assert.Equal("kv-stackjack-compact", server.AuthSecretName);
            Assert.True(server.Enabled);

            var connection = Assert.Single(await db.IntegrationConnections.ForTenant(user).ToListAsync());
            Assert.Equal(server.Id, connection.McpServerId);
            Assert.Equal("kv-stackjack-compact", connection.AuthSecretName);
        }
    }

    [Theory]
    [InlineData(IntegrationProvider.Halo)]
    [InlineData(IntegrationProvider.NinjaOne)]
    [InlineData(IntegrationProvider.Cipp)]
    [InlineData(IntegrationProvider.Meraki)]
    [InlineData(IntegrationProvider.UniFi)]
    [InlineData(IntegrationProvider.Action1)]
    [InlineData(IntegrationProvider.Autotask)]
    [InlineData(IntegrationProvider.Blackpoint)]
    [InlineData(IntegrationProvider.DefensX)]
    [InlineData(IntegrationProvider.Pax8)]
    [InlineData(IntegrationProvider.Slide)]
    public async Task Every_Compact_Backed_Provider_Adopts_The_Tenants_One_Compact_Server(IntegrationProvider provider)
    {
        var (db, user) = Create();
        await using (db)
        {
            var server = await SeedCompactAsync(db, user);

            var result = await IntegrationEndpoints.CreateIntegrationAsync(
                new CreateIntegrationRequest(provider, provider.ToString()), db, user);

            Assert.Equal(StatusCodes.Status201Created, StatusOf(result));
            // Reused, not duplicated: one Compact registration per tenant is the whole point.
            Assert.Single(await db.McpServers.ForTenant(user).ToListAsync());
            var connection = Assert.Single(await db.IntegrationConnections.ForTenant(user).ToListAsync());
            Assert.Equal(server.Id, connection.McpServerId);
        }
    }

    [Fact]
    public async Task A_Different_Secret_Name_Is_Rejected_Rather_Than_Reused_Or_Overwritten()
    {
        var (db, user) = Create();
        await using (db)
        {
            var server = await SeedCompactAsync(db, user, "kv-stackjack-live");

            var result = await IntegrationEndpoints.CreateIntegrationAsync(
                new CreateIntegrationRequest(IntegrationProvider.Meraki, "Meraki", AuthSecretName: "kv-stackjack-other"),
                db, user);

            Assert.Equal(StatusCodes.Status409Conflict, StatusOf(result));
            // The working credential reference is untouched, and no half-made connection is left behind.
            Assert.Equal("kv-stackjack-live", (await db.McpServers.ForTenant(user).FirstAsync(s => s.Id == server.Id)).AuthSecretName);
            Assert.Empty(await db.IntegrationConnections.ForTenant(user).ToListAsync());
        }
    }

    [Fact]
    public async Task The_Same_Secret_Name_Under_Different_Casing_Is_Not_A_Conflict()
    {
        var (db, user) = Create();
        await using (db)
        {
            var server = await SeedCompactAsync(db, user, "kv-stackjack-compact");

            var result = await IntegrationEndpoints.CreateIntegrationAsync(
                new CreateIntegrationRequest(IntegrationProvider.Cipp, "CIPP", AuthSecretName: "KV-StackJack-Compact"),
                db, user);

            Assert.Equal(StatusCodes.Status201Created, StatusOf(result));
            Assert.Equal("kv-stackjack-compact", (await db.McpServers.ForTenant(user).FirstAsync(s => s.Id == server.Id)).AuthSecretName);
        }
    }

    [Fact]
    public async Task An_Existing_Server_With_No_Secret_Name_Adopts_The_Supplied_One()
    {
        var (db, user) = Create();
        await using (db)
        {
            var server = await SeedCompactAsync(db, user, authSecretName: null);

            var result = await IntegrationEndpoints.CreateIntegrationAsync(
                new CreateIntegrationRequest(IntegrationProvider.Halo, "Halo", AuthSecretName: "kv-stackjack-compact"),
                db, user);

            Assert.Equal(StatusCodes.Status201Created, StatusOf(result));
            // Filling a blank is not overwriting a working credential reference.
            Assert.Equal("kv-stackjack-compact", (await db.McpServers.ForTenant(user).FirstAsync(s => s.Id == server.Id)).AuthSecretName);
        }
    }

    [Fact]
    public async Task First_Compact_Integration_Without_A_Secret_Name_Is_Rejected_And_Registers_Nothing()
    {
        var (db, user) = Create();
        await using (db)
        {
            var result = await IntegrationEndpoints.CreateIntegrationAsync(
                new CreateIntegrationRequest(IntegrationProvider.Halo, "Halo"), db, user);

            Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
            Assert.Empty(await db.McpServers.ForTenant(user).ToListAsync());
            Assert.Empty(await db.IntegrationConnections.ForTenant(user).ToListAsync());
        }
    }

    [Fact]
    public async Task Non_Compact_Providers_Still_Get_No_Server_Invented_For_Them()
    {
        var (db, user) = Create();
        await using (db)
        {
            var result = await IntegrationEndpoints.CreateIntegrationAsync(
                new CreateIntegrationRequest(IntegrationProvider.Composio, "Composio", AuthSecretName: "kv-composio"),
                db, user);

            Assert.Equal(StatusCodes.Status201Created, StatusOf(result));
            Assert.Empty(await db.McpServers.ForTenant(user).ToListAsync());
            Assert.Null(Assert.Single(await db.IntegrationConnections.ForTenant(user).ToListAsync()).McpServerId);
        }
    }

    [Fact]
    public async Task An_McpServerId_From_Another_Tenant_Is_Rejected_At_Create()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        Guid serverBId;
        var (dbB, userB) = Open(dbName, tenantB);
        await using (dbB)
        {
            serverBId = (await SeedCompactAsync(dbB, userB)).Id;
        }

        var (dbA, userA) = Open(dbName, tenantA);
        await using (dbA)
        {
            var result = await IntegrationEndpoints.CreateIntegrationAsync(
                new CreateIntegrationRequest(IntegrationProvider.Halo, "Halo", AuthSecretName: "kv-a", McpServerId: serverBId),
                dbA, userA);

            Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(result));
            Assert.Empty(await dbA.IntegrationConnections.ForTenant(userA).ToListAsync());
            Assert.Empty(await dbA.McpServers.ForTenant(userA).ToListAsync());
        }
    }

    [Fact]
    public async Task Test_Connection_Detects_The_Plan_From_The_Free_Session_Info_Tool()
    {
        var (db, user) = Create();
        await using (db)
        {
            var server = await SeedCompactAsync(db, user);
            var connection = new IntegrationConnection
            {
                TenantId = user.TenantId!.Value,
                Provider = IntegrationProvider.Halo,
                DisplayName = "Halo",
                McpServerId = server.Id,
            };
            db.IntegrationConnections.Add(connection);
            await db.SaveChangesAsync();

            var mcp = new RecordingMcp();
            var sync = new IntegrationSyncService(db, user, mcp, new NoopAudit());

            var (ok, message) = await sync.TestConnectionAsync(connection.Id);

            Assert.True(ok);
            Assert.Contains("Business", message, StringComparison.Ordinal);
            // Exactly one tool call, and it is the free platform tool: detection must not spend the
            // connector allowance it is reporting on.
            Assert.Collection(
                mcp.Calls,
                call => Assert.Equal("tools/list", call),
                call => Assert.Equal(StackJackPlanDetector.ToolName, call));

            var stored = await db.IntegrationConnections.ForTenant(user).FirstAsync(c => c.Id == connection.Id);
            Assert.Equal(IntegrationStatus.Connected, stored.Status);
            Assert.Equal(StackJackPlan.Business, stored.StackJackPlan);
            Assert.Equal(50_000, stored.MonthlyCallLimit);
            Assert.NotNull(stored.PlanDetectedAt);
        }
    }

    [Fact]
    public async Task Failed_Detection_Leaves_The_Connection_Connected_And_The_Plan_Unknown()
    {
        var (db, user) = Create();
        await using (db)
        {
            var server = await SeedCompactAsync(db, user);
            var connection = new IntegrationConnection
            {
                TenantId = user.TenantId!.Value,
                Provider = IntegrationProvider.NinjaOne,
                DisplayName = "NinjaOne",
                McpServerId = server.Id,
            };
            db.IntegrationConnections.Add(connection);
            await db.SaveChangesAsync();

            var mcp = new RecordingMcp { FailToolCalls = true };
            var sync = new IntegrationSyncService(db, user, mcp, new NoopAudit());

            var (ok, message) = await sync.TestConnectionAsync(connection.Id);

            // tools/list answered, so the connection works. Only the plan is unknown, and the message
            // says so rather than reporting the whole test as a failure.
            Assert.True(ok);
            Assert.Contains("Plan not detected", message, StringComparison.Ordinal);

            var stored = await db.IntegrationConnections.ForTenant(user).FirstAsync(c => c.Id == connection.Id);
            Assert.Equal(IntegrationStatus.Connected, stored.Status);
            Assert.Null(stored.LastError);
            Assert.Equal(StackJackPlan.Unknown, stored.StackJackPlan);
            Assert.Null(stored.MonthlyCallLimit);
            Assert.Null(stored.PlanDetectedAt);
        }
    }

    [Fact]
    public async Task A_Connection_Predating_The_Built_In_Default_Adopts_The_Compact_Server_On_Sync()
    {
        var (db, user) = Create();
        await using (db)
        {
            var server = await SeedCompactAsync(db, user);
            var connection = new IntegrationConnection
            {
                TenantId = user.TenantId!.Value,
                Provider = IntegrationProvider.Halo,
                DisplayName = "Halo",
                AuthSecretName = "kv-stackjack-compact",
            };
            db.IntegrationConnections.Add(connection);
            await db.SaveChangesAsync();

            var mcp = new RecordingMcp { Body = """{"clients":[]}""" };
            var sync = new IntegrationSyncService(db, user, mcp, new NoopAudit());

            var run = await sync.SyncAsync(connection.Id);

            Assert.Equal(SyncRunStatus.Succeeded, run.Status);
            Assert.Equal(HaloClientMapper.ToolName, Assert.Single(mcp.Calls));
            Assert.Equal(server.Id, (await db.IntegrationConnections.ForTenant(user).FirstAsync(c => c.Id == connection.Id)).McpServerId);
        }
    }

    [Fact]
    public async Task Sync_Without_Any_Compact_Server_Still_Fails_Before_Reaching_Mcp()
    {
        var (db, user) = Create();
        await using (db)
        {
            var connection = new IntegrationConnection
            {
                TenantId = user.TenantId!.Value,
                Provider = IntegrationProvider.Halo,
                DisplayName = "Halo",
                AuthSecretName = "kv-name-only",
            };
            db.IntegrationConnections.Add(connection);
            await db.SaveChangesAsync();

            var mcp = new RecordingMcp();
            var sync = new IntegrationSyncService(db, user, mcp, new NoopAudit());

            var run = await sync.SyncAsync(connection.Id);

            Assert.Equal(SyncRunStatus.Failed, run.Status);
            Assert.Contains("McpServerId", run.ErrorSummary, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(mcp.Calls);
        }
    }

    [Fact]
    public async Task Detail_Response_Carries_The_Plan_And_The_Cadence_It_Derives()
    {
        var (db, user) = Create();
        await using (db)
        {
            var connection = new IntegrationConnection
            {
                TenantId = user.TenantId!.Value,
                Provider = IntegrationProvider.Halo,
                DisplayName = "Halo",
                StackJackPlan = StackJackPlan.Business,
                MonthlyCallLimit = 50_000,
                PlanDetectedAt = DateTimeOffset.UtcNow,
            };
            db.IntegrationConnections.Add(connection);
            await db.SaveChangesAsync();

            var body = BodyOf(await IntegrationEndpoints.GetIntegrationAsync(connection.Id, db, user), out var document);
            using (document)
            {
                Assert.Equal("Business", body.GetProperty("StackJackPlan").GetString());
                Assert.Equal(50_000, body.GetProperty("MonthlyCallLimit").GetInt32());
                Assert.Equal(JsonValueKind.Null, body.GetProperty("SyncIntervalMinutesOverride").ValueKind);
                // 20% of 50,000 calls a cycle, at ~10 calls a run, is a check every 44 minutes.
                Assert.Equal(44, body.GetProperty("SyncIntervalMinutes").GetInt32());
                Assert.Equal(
                    SyncCadencePolicy.DerivedIntervalMinutes(StackJackPlan.Business, 50_000),
                    body.GetProperty("SyncIntervalMinutes").GetInt32());
                Assert.NotEqual(JsonValueKind.Null, body.GetProperty("NextSyncDueAt").ValueKind);
            }
        }
    }

    [Fact]
    public async Task Update_Accepts_A_Cadence_Override_And_Zero_Clears_It()
    {
        var (db, user) = Create();
        await using (db)
        {
            var connection = new IntegrationConnection
            {
                TenantId = user.TenantId!.Value,
                Provider = IntegrationProvider.Halo,
                DisplayName = "Halo",
                StackJackPlan = StackJackPlan.Business,
                MonthlyCallLimit = 50_000,
            };
            db.IntegrationConnections.Add(connection);
            await db.SaveChangesAsync();

            Assert.Equal(
                StatusCodes.Status204NoContent,
                StatusOf(await IntegrationEndpoints.UpdateIntegrationAsync(
                    connection.Id, new UpdateIntegrationRequest(SyncIntervalMinutesOverride: 1440), db, user)));

            var slowed = BodyOf(await IntegrationEndpoints.GetIntegrationAsync(connection.Id, db, user), out var slowedDocument);
            using (slowedDocument)
            {
                Assert.Equal(1440, slowed.GetProperty("SyncIntervalMinutesOverride").GetInt32());
                // Slower than the plan allows is fine; the policy only refuses to out-run it.
                Assert.Equal(1440, slowed.GetProperty("SyncIntervalMinutes").GetInt32());
            }

            Assert.Equal(
                StatusCodes.Status204NoContent,
                StatusOf(await IntegrationEndpoints.UpdateIntegrationAsync(
                    connection.Id, new UpdateIntegrationRequest(SyncIntervalMinutesOverride: 0), db, user)));

            var cleared = BodyOf(await IntegrationEndpoints.GetIntegrationAsync(connection.Id, db, user), out var clearedDocument);
            using (clearedDocument)
            {
                Assert.Equal(JsonValueKind.Null, cleared.GetProperty("SyncIntervalMinutesOverride").ValueKind);
                Assert.Equal(44, cleared.GetProperty("SyncIntervalMinutes").GetInt32());
            }
        }
    }
}
