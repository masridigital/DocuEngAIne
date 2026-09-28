using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using DocuEngAIne.Infrastructure.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocuEngAIne.Tests;

public class IntegrationSyncSchedulerTests
{
    private sealed class NoopAudit : IAuditService
    {
        public Task LogAsync(string action, string entityType, Guid? entityId = null, string? details = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NoopMcp : IMcpClient
    {
        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"content":[{"type":"text","text":"{\"clients\":[]}"}]}}""");
    }

    private sealed class ThrowingMcp : IMcpClient
    {
        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Compact unreachable.");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Compact unreachable.");
    }

    [Fact]
    public void BackgroundCurrentUser_ForTenant_Is_Pinned_To_That_Tenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var userA = BackgroundCurrentUser.ForTenant(tenantA);
        var userB = BackgroundCurrentUser.ForTenant(tenantB);

        Assert.Equal(tenantA, userA.TenantId);
        Assert.Equal(tenantB, userB.TenantId);
        Assert.NotEqual(userA.TenantId, userB.TenantId);
        Assert.True(userA.IsAuthenticated);
        Assert.True(userA.HasRole(UserRole.Owner));
        Assert.Throws<ArgumentException>(() => BackgroundCurrentUser.ForTenant(Guid.Empty));
    }

    [Fact]
    public void BackgroundTenantContext_Cannot_Switch_Tenants()
    {
        var context = new BackgroundTenantContext();
        var tenantA = Guid.NewGuid();
        context.Bind(tenantA);
        context.Bind(tenantA);
        Assert.Equal(tenantA, context.TenantId);
        Assert.Throws<InvalidOperationException>(() => context.Bind(Guid.NewGuid()));
    }

    [Fact]
    public void Due_Selection_Honours_Override_Plan_McpServer_And_Overlap()
    {
        var now = DateTimeOffset.UtcNow;
        var due = NewConnection(StackJackPlan.Enterprise, int.MaxValue);
        due.McpServerId = Guid.NewGuid();
        due.LastSyncAt = null;

        var notDue = NewConnection(StackJackPlan.Enterprise, int.MaxValue);
        notDue.McpServerId = Guid.NewGuid();
        notDue.LastSyncAt = now;

        var noServer = NewConnection(StackJackPlan.Enterprise, int.MaxValue);
        noServer.McpServerId = null;

        var unknownPlan = NewConnection(StackJackPlan.Unknown, null);
        unknownPlan.McpServerId = Guid.NewGuid();

        var overrideDue = NewConnection(StackJackPlan.Unknown, null);
        overrideDue.McpServerId = Guid.NewGuid();
        overrideDue.SyncIntervalMinutesOverride = 60;
        overrideDue.LastSyncAt = now.AddHours(-2);

        var running = NewConnection(StackJackPlan.Enterprise, int.MaxValue);
        running.McpServerId = Guid.NewGuid();

        var selected = IntegrationSyncWork.DueConnections(
            [due, notDue, noServer, unknownPlan, overrideDue, running],
            new HashSet<Guid> { running.Id },
            now);

        Assert.Equal(2, selected.Count);
        Assert.Contains(due.Id, selected.Select(c => c.Id));
        Assert.Contains(overrideDue.Id, selected.Select(c => c.Id));
    }

    [Fact]
    public async Task Due_Connection_Is_Queued()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var sp = BuildProvider(dbName);
        var tenantId = Guid.NewGuid();

        Guid dueId, skippedNoServerId, skippedNotDueId;
        await using (var seed = OpenBound(sp, tenantId))
        {
            var server = await AddCompactServerAsync(seed.Db, tenantId);

            var due = DueHalo(tenantId, server.Id);
            seed.Db.IntegrationConnections.Add(due);

            var noServer = DueHalo(tenantId, mcpServerId: null);
            noServer.DisplayName = "No Compact";
            seed.Db.IntegrationConnections.Add(noServer);

            var notDue = DueHalo(tenantId, server.Id);
            notDue.DisplayName = "Just synced";
            notDue.LastSyncAt = DateTimeOffset.UtcNow;
            seed.Db.IntegrationConnections.Add(notDue);

            await seed.Db.SaveChangesAsync();
            dueId = due.Id;
            skippedNoServerId = noServer.Id;
            skippedNotDueId = notDue.Id;
        }

        var result = await CreateRunner(sp).RunDueAsync();

        Assert.Contains(dueId, result.QueuedConnectionIds);
        Assert.DoesNotContain(skippedNoServerId, result.QueuedConnectionIds);
        Assert.DoesNotContain(skippedNotDueId, result.QueuedConnectionIds);

        await using var check = OpenBound(sp, tenantId);
        var runs = await check.Db.SyncRuns.ForTenant(check.User).ToListAsync();
        Assert.Single(runs);
        Assert.Equal(dueId, runs[0].IntegrationConnectionId);
        Assert.True(runs[0].StartedAt.Date == DateTimeOffset.UtcNow.Date);
    }

    [Fact]
    public async Task Other_Tenant_Data_Is_Not_Visible()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var sp = BuildProvider(dbName);
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        Guid connectionA, connectionB;
        await using (var seedA = OpenBound(sp, tenantA))
        {
            var server = await AddCompactServerAsync(seedA.Db, tenantA);
            var connection = DueHalo(tenantA, server.Id);
            seedA.Db.IntegrationConnections.Add(connection);
            await seedA.Db.SaveChangesAsync();
            connectionA = connection.Id;
        }

        await using (var seedB = OpenBound(sp, tenantB))
        {
            var server = await AddCompactServerAsync(seedB.Db, tenantB);
            var connection = DueHalo(tenantB, server.Id);
            connection.DisplayName = "Halo B";
            seedB.Db.IntegrationConnections.Add(connection);
            await seedB.Db.SaveChangesAsync();
            connectionB = connection.Id;
        }

        var result = await CreateRunner(sp).RunDueAsync();
        Assert.Contains(connectionA, result.QueuedConnectionIds);
        Assert.Contains(connectionB, result.QueuedConnectionIds);

        var userA = BackgroundCurrentUser.ForTenant(tenantA);
        var userB = BackgroundCurrentUser.ForTenant(tenantB);

        await using (var dbA = OpenBound(sp, tenantA))
        {
            var connections = await dbA.Db.IntegrationConnections.ForTenant(userA).ToListAsync();
            var runs = await dbA.Db.SyncRuns.ForTenant(userA).ToListAsync();
            Assert.Single(connections);
            Assert.Equal(connectionA, connections[0].Id);
            Assert.DoesNotContain(connections, c => c.Id == connectionB || c.TenantId == tenantB);
            Assert.Single(runs);
            Assert.All(runs, r => Assert.Equal(tenantA, r.TenantId));
            Assert.Empty(await dbA.Db.IntegrationConnections.ForTenant(userA)
                .Where(c => c.Id == connectionB).ToListAsync());
        }

        await using (var dbB = OpenBound(sp, tenantB))
        {
            var hidden = await dbB.Db.IntegrationConnections.ForTenant(userB)
                .Where(c => c.Id == connectionA).ToListAsync();
            Assert.Empty(hidden);
            Assert.Empty(await dbB.Db.SyncRuns.ForTenant(userB)
                .Where(r => r.IntegrationConnectionId == connectionA).ToListAsync());
        }
    }

    [Fact]
    public async Task Overlap_Is_Skipped()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var sp = BuildProvider(dbName);
        var tenantId = Guid.NewGuid();

        Guid connectionId, runningRunId;
        await using (var seed = OpenBound(sp, tenantId))
        {
            var server = await AddCompactServerAsync(seed.Db, tenantId);
            var connection = DueHalo(tenantId, server.Id);
            seed.Db.IntegrationConnections.Add(connection);
            await seed.Db.SaveChangesAsync();

            var running = new SyncRun
            {
                TenantId = tenantId,
                IntegrationConnectionId = connection.Id,
                StartedAt = DateTimeOffset.UtcNow,
                Status = SyncRunStatus.Running,
            };
            seed.Db.SyncRuns.Add(running);
            await seed.Db.SaveChangesAsync();
            connectionId = connection.Id;
            runningRunId = running.Id;
        }

        var result = await CreateRunner(sp).RunDueAsync();

        Assert.DoesNotContain(connectionId, result.QueuedConnectionIds);
        Assert.Contains(connectionId, result.SkippedOverlapConnectionIds);

        await using var check = OpenBound(sp, tenantId);
        var runs = await check.Db.SyncRuns.ForTenant(check.User).ToListAsync();
        Assert.Single(runs);
        Assert.Equal(runningRunId, runs[0].Id);
        Assert.Equal(SyncRunStatus.Running, runs[0].Status);
    }

    [Fact]
    public async Task Stale_Running_Run_Is_Reaped_And_The_Connection_Syncs_Again()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var sp = BuildProvider(dbName);
        var tenantId = Guid.NewGuid();

        Guid connectionId, staleRunId;
        await using (var seed = OpenBound(sp, tenantId))
        {
            var server = await AddCompactServerAsync(seed.Db, tenantId);
            var connection = DueHalo(tenantId, server.Id);
            seed.Db.IntegrationConnections.Add(connection);
            await seed.Db.SaveChangesAsync();

            var stuck = new SyncRun
            {
                TenantId = tenantId,
                IntegrationConnectionId = connection.Id,
                StartedAt = DateTimeOffset.UtcNow - IntegrationSyncWork.StaleRunningThreshold - TimeSpan.FromMinutes(10),
                Status = SyncRunStatus.Running,
            };
            seed.Db.SyncRuns.Add(stuck);
            await seed.Db.SaveChangesAsync();
            connectionId = connection.Id;
            staleRunId = stuck.Id;
        }

        var result = await CreateRunner(sp).RunDueAsync();

        // Without the reap, the crash leftover kept the connection in the running set forever.
        Assert.Contains(connectionId, result.QueuedConnectionIds);
        Assert.DoesNotContain(connectionId, result.SkippedOverlapConnectionIds);

        await using var check = OpenBound(sp, tenantId);
        var reaped = await check.Db.SyncRuns.ForTenant(check.User).SingleAsync(r => r.Id == staleRunId);
        Assert.Equal(SyncRunStatus.Failed, reaped.Status);
        Assert.NotNull(reaped.FinishedAt);
        Assert.Contains("scheduler", reaped.ErrorSummary, StringComparison.OrdinalIgnoreCase);
        Assert.True(await check.Db.SyncRuns.ForTenant(check.User).AnyAsync(r => r.Id != staleRunId),
            "the reaped connection should have been synced again this tick");
    }

    [Fact]
    public async Task Failed_Run_Backs_Off_Instead_Of_Retrying_Next_Tick()
    {
        var dbName = Guid.NewGuid().ToString();
        await using var sp = BuildProvider(dbName, new ThrowingMcp());
        var tenantId = Guid.NewGuid();

        Guid connectionId;
        await using (var seed = OpenBound(sp, tenantId))
        {
            var server = await AddCompactServerAsync(seed.Db, tenantId);
            var connection = DueHalo(tenantId, server.Id);
            seed.Db.IntegrationConnections.Add(connection);
            await seed.Db.SaveChangesAsync();
            connectionId = connection.Id;
        }

        var first = await CreateRunner(sp).RunDueAsync();
        Assert.Contains(connectionId, first.QueuedConnectionIds);

        await using (var check = OpenBound(sp, tenantId))
        {
            var run = await check.Db.SyncRuns.ForTenant(check.User).SingleAsync();
            Assert.Equal(SyncRunStatus.Failed, run.Status);
            var connection = await check.Db.IntegrationConnections.ForTenant(check.User).SingleAsync();
            Assert.NotNull(connection.LastAttemptAt);
            Assert.Null(connection.LastSyncAt);
        }

        // The next tick must NOT pick the connection up again — before LastAttemptAt existed, a
        // failing connection retried every minute and burned the plan allowance.
        var second = await CreateRunner(sp).RunDueAsync();
        Assert.DoesNotContain(connectionId, second.QueuedConnectionIds);

        await using (var recheck = OpenBound(sp, tenantId))
        {
            Assert.Equal(1, await recheck.Db.SyncRuns.ForTenant(recheck.User).CountAsync());
        }
    }

    private static IntegrationSyncRunner CreateRunner(ServiceProvider sp)
        => new(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<IntegrationSyncRunner>.Instance);

    private static ServiceProvider BuildProvider(string dbName, IMcpClient? mcp = null)
    {
        var services = new ServiceCollection();
        services.AddHttpContextAccessor();
        services.AddScoped<IBackgroundTenantContext, BackgroundTenantContext>();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddDbContext<DocuEngAIneDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<IMcpClient>(_ => mcp ?? new NoopMcp());
        services.AddScoped<IAuditService, NoopAudit>();
        services.AddScoped<IIntegrationSyncService, IntegrationSyncService>();
        return services.BuildServiceProvider();
    }

    private static BoundScope OpenBound(ServiceProvider sp, Guid tenantId)
    {
        var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<IBackgroundTenantContext>().Bind(tenantId);
        return new BoundScope(scope);
    }

    private sealed class BoundScope : IAsyncDisposable
    {
        private readonly IServiceScope _scope;
        public BoundScope(IServiceScope scope) => _scope = scope;
        public ICurrentUser User => _scope.ServiceProvider.GetRequiredService<ICurrentUser>();
        public DocuEngAIneDbContext Db => _scope.ServiceProvider.GetRequiredService<DocuEngAIneDbContext>();
        public ValueTask DisposeAsync()
        {
            _scope.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<McpServer> AddCompactServerAsync(DocuEngAIneDbContext db, Guid tenantId)
    {
        var server = new McpServer
        {
            TenantId = tenantId,
            Name = "StackJack Compact",
            Kind = McpServerKind.StackJackCompact,
            Transport = McpTransport.Http,
            EndpointUrl = "https://compact.example.test",
            AuthSecretName = "kv-stackjack-compact",
        };
        db.McpServers.Add(server);
        await db.SaveChangesAsync();
        return server;
    }

    private static IntegrationConnection DueHalo(Guid tenantId, Guid? mcpServerId)
        => new()
        {
            TenantId = tenantId,
            Provider = IntegrationProvider.Halo,
            DisplayName = "Halo",
            McpServerId = mcpServerId,
            IsEnabled = true,
            StackJackPlan = StackJackPlan.Enterprise,
            MonthlyCallLimit = int.MaxValue,
            LastSyncAt = null,
        };

    private static IntegrationConnection NewConnection(StackJackPlan plan, int? limit) => new()
    {
        TenantId = Guid.NewGuid(),
        Provider = IntegrationProvider.Halo,
        DisplayName = "Halo",
        StackJackPlan = plan,
        MonthlyCallLimit = limit,
        IsEnabled = true,
    };
}
