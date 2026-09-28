using System.Text.Json;
using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

/// <summary>
/// GET /api/integrations/{id}/runs is the history the Integrations SPA reads. Coverage here is the
/// tenant fence and the fields the table shows — status, provider, started/finished, error.
/// Pull logic stays in <see cref="IntegrationSyncTests"/>.
/// </summary>
public class SyncRunHistoryTests
{
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

    private static async Task<IntegrationConnection> SeedConnectionAsync(
        DocuEngAIneDbContext db, FakeCurrentUser user, IntegrationProvider provider = IntegrationProvider.Halo)
    {
        var connection = new IntegrationConnection
        {
            TenantId = user.TenantId!.Value,
            Provider = provider,
            DisplayName = provider.ToString(),
        };
        db.IntegrationConnections.Add(connection);
        await db.SaveChangesAsync();
        return connection;
    }

    private static async Task<SyncRun> SeedRunAsync(
        DocuEngAIneDbContext db,
        FakeCurrentUser user,
        Guid connectionId,
        DateTimeOffset startedAt,
        SyncRunStatus status = SyncRunStatus.Succeeded,
        string? error = null,
        DateTimeOffset? finishedAt = null)
    {
        var run = new SyncRun
        {
            TenantId = user.TenantId!.Value,
            IntegrationConnectionId = connectionId,
            StartedAt = startedAt,
            FinishedAt = finishedAt ?? startedAt.AddMinutes(1),
            Status = status,
            ErrorSummary = error,
            ItemsCreated = status == SyncRunStatus.Succeeded ? 2 : 0,
            ItemsUpdated = 1,
            ItemsSkipped = 0,
        };
        db.SyncRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
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
    public async Task ListRuns_Returns_Newest_First_With_Status_Provider_Times_And_Error()
    {
        var (db, user) = Create();
        await using (db)
        {
            var connection = await SeedConnectionAsync(db, user, IntegrationProvider.NinjaOne);
            var older = await SeedRunAsync(
                db, user, connection.Id, DateTimeOffset.Parse("2026-08-01T10:00:00Z"), SyncRunStatus.Failed, "compact 503");
            var newer = await SeedRunAsync(
                db, user, connection.Id, DateTimeOffset.Parse("2026-08-01T12:00:00Z"), SyncRunStatus.Succeeded);

            var body = BodyOf(await IntegrationEndpoints.ListRunsAsync(connection.Id, db, user), out var document);
            using (document)
            {
                Assert.Equal(JsonValueKind.Array, body.ValueKind);
                Assert.Equal(2, body.GetArrayLength());

                var first = body[0];
                Assert.Equal(newer.Id, first.GetProperty("Id").GetGuid());
                Assert.Equal("Succeeded", first.GetProperty("Status").GetString());
                Assert.Equal("NinjaOne", first.GetProperty("Provider").GetString());
                Assert.Equal(newer.StartedAt, first.GetProperty("StartedAt").GetDateTimeOffset());
                Assert.Equal(newer.FinishedAt, first.GetProperty("FinishedAt").GetDateTimeOffset());
                Assert.Equal(JsonValueKind.Null, first.GetProperty("ErrorSummary").ValueKind);

                var second = body[1];
                Assert.Equal(older.Id, second.GetProperty("Id").GetGuid());
                Assert.Equal("Failed", second.GetProperty("Status").GetString());
                Assert.Equal("NinjaOne", second.GetProperty("Provider").GetString());
                Assert.Equal("compact 503", second.GetProperty("ErrorSummary").GetString());
            }
        }
    }

    [Fact]
    public async Task ListRuns_Is_Empty_When_The_Integration_Has_Never_Synced()
    {
        var (db, user) = Create();
        await using (db)
        {
            var connection = await SeedConnectionAsync(db, user);

            var body = BodyOf(await IntegrationEndpoints.ListRunsAsync(connection.Id, db, user), out var document);
            using (document)
            {
                Assert.Equal(JsonValueKind.Array, body.ValueKind);
                Assert.Equal(0, body.GetArrayLength());
            }
        }
    }

    [Fact]
    public async Task ListRuns_Does_Not_Include_A_Sibling_Integration()
    {
        var (db, user) = Create();
        await using (db)
        {
            var halo = await SeedConnectionAsync(db, user, IntegrationProvider.Halo);
            var ninja = await SeedConnectionAsync(db, user, IntegrationProvider.NinjaOne);
            await SeedRunAsync(db, user, halo.Id, DateTimeOffset.UtcNow, SyncRunStatus.Succeeded);
            await SeedRunAsync(db, user, ninja.Id, DateTimeOffset.UtcNow, SyncRunStatus.Failed, "ninja timeout");

            var body = BodyOf(await IntegrationEndpoints.ListRunsAsync(halo.Id, db, user), out var document);
            using (document)
            {
                Assert.Equal(1, body.GetArrayLength());
                Assert.Equal("Halo", body[0].GetProperty("Provider").GetString());
                Assert.Equal("Succeeded", body[0].GetProperty("Status").GetString());
            }
        }
    }

    [Fact]
    public async Task ListRuns_Caps_At_Fifty()
    {
        var (db, user) = Create();
        await using (db)
        {
            var connection = await SeedConnectionAsync(db, user);
            var start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            for (var i = 0; i < 51; i++)
                await SeedRunAsync(db, user, connection.Id, start.AddMinutes(i));

            var body = BodyOf(await IntegrationEndpoints.ListRunsAsync(connection.Id, db, user), out var document);
            using (document)
            {
                Assert.Equal(50, body.GetArrayLength());
                Assert.Equal(start.AddMinutes(50), body[0].GetProperty("StartedAt").GetDateTimeOffset());
                Assert.Equal(start.AddMinutes(1), body[49].GetProperty("StartedAt").GetDateTimeOffset());
            }
        }
    }

    [Fact]
    public async Task ListRuns_Returns_404_For_Another_Tenant_Integration()
    {
        var dbName = Guid.NewGuid().ToString();
        var (dbA, userA) = Open(dbName, Guid.NewGuid());
        var (dbB, userB) = Open(dbName, Guid.NewGuid());
        await using (dbA)
        await using (dbB)
        {
            var foreign = await SeedConnectionAsync(dbB, userB);
            await SeedRunAsync(dbB, userB, foreign.Id, DateTimeOffset.UtcNow, SyncRunStatus.Failed, "secret");

            Assert.Equal(
                StatusCodes.Status404NotFound,
                StatusOf(await IntegrationEndpoints.ListRunsAsync(foreign.Id, dbA, userA)));
            Assert.Empty(await dbA.SyncRuns.ForTenant(userA).ToListAsync());
        }
    }

    [Fact]
    public async Task ListRuns_Returns_404_For_An_Unknown_Id()
    {
        var (db, user) = Create();
        await using (db)
        {
            Assert.Equal(
                StatusCodes.Status404NotFound,
                StatusOf(await IntegrationEndpoints.ListRunsAsync(Guid.NewGuid(), db, user)));
        }
    }
}
