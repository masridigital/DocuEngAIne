using System.Collections.Concurrent;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>Outcome of one scheduler pass, used by tests to see what was queued versus skipped.</summary>
public sealed record IntegrationSyncTickResult(
    IReadOnlyList<Guid> QueuedConnectionIds,
    IReadOnlyList<Guid> SkippedOverlapConnectionIds);

/// <summary>
/// Walks due integration connections and calls <see cref="IIntegrationSyncService.SyncAsync"/>.
/// Each tenant is a fresh DI scope with <see cref="BackgroundCurrentUser.ForTenant"/> so
/// <c>ForTenant</c> never sees another tenant's rows in the same run.
/// </summary>
public sealed class IntegrationSyncRunner
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<IntegrationSyncRunner> _logger;
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

    public IntegrationSyncRunner(IServiceScopeFactory scopes, ILogger<IntegrationSyncRunner> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public async Task<IntegrationSyncTickResult> RunDueAsync(CancellationToken cancellationToken = default)
    {
        var queued = new List<Guid>();
        var skippedOverlap = new List<Guid>();

        var tenantIds = await ListCandidateTenantIdsAsync(cancellationToken);

        foreach (var tenantId in tenantIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await RunTenantAsync(tenantId, queued, skippedOverlap, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled sync failed for tenant {TenantId}.", tenantId);
            }
        }

        return new IntegrationSyncTickResult(queued, skippedOverlap);
    }

    /// <summary>
    /// Tenant ids only — no connection rows, secret names, or payloads. The processing scope
    /// loads that tenant's connections through <c>ForTenant</c>.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> ListCandidateTenantIdsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocuEngAIneDbContext>();
        return await db.IntegrationConnections.AsNoTracking()
            .Where(c => c.IsEnabled && c.McpServerId != null)
            .Select(c => c.TenantId)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync(cancellationToken);
    }

    private async Task RunTenantAsync(
        Guid tenantId,
        List<Guid> queued,
        List<Guid> skippedOverlap,
        CancellationToken cancellationToken)
    {
        using var scope = OpenTenantScope(tenantId);
        var user = scope.ServiceProvider.GetRequiredService<ICurrentUser>();
        if (user.TenantId != tenantId)
        {
            throw new InvalidOperationException(
                $"Background ICurrentUser resolved to {user.TenantId} instead of tenant {tenantId}.");
        }

        var db = scope.ServiceProvider.GetRequiredService<DocuEngAIneDbContext>();
        var sync = scope.ServiceProvider.GetRequiredService<IIntegrationSyncService>();

        var connections = await db.IntegrationConnections.ForTenant(user)
            .AsNoTracking()
            .Where(c => c.IsEnabled && c.McpServerId != null)
            .ToListAsync(cancellationToken);

        var running = (await db.SyncRuns.ForTenant(user)
            .AsNoTracking()
            .Where(r => r.Status == SyncRunStatus.Running)
            .Select(r => r.IntegrationConnectionId)
            .ToListAsync(cancellationToken)).ToHashSet();

        var now = DateTimeOffset.UtcNow;
        foreach (var connection in connections)
        {
            if (connection.TenantId != tenantId)
            {
                _logger.LogError(
                    "Refusing to sync connection {ConnectionId}: tenant {Actual} is not the bound tenant {Expected}.",
                    connection.Id, connection.TenantId, tenantId);
                continue;
            }

            if (!IntegrationSyncWork.IsDue(connection, running, now))
            {
                if (running.Contains(connection.Id) && connection.McpServerId is not null && connection.IsEnabled)
                    skippedOverlap.Add(connection.Id);
                continue;
            }

            if (!_inFlight.TryAdd(connection.Id, 0))
            {
                skippedOverlap.Add(connection.Id);
                continue;
            }

            queued.Add(connection.Id);
            try
            {
                await sync.SyncAsync(connection.Id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled sync failed for connection {ConnectionId}.", connection.Id);
            }
            finally
            {
                _inFlight.TryRemove(connection.Id, out _);
            }
        }
    }

    private IServiceScope OpenTenantScope(Guid tenantId)
    {
        var scope = _scopes.CreateScope();
        try
        {
            scope.ServiceProvider.GetRequiredService<IBackgroundTenantContext>().Bind(tenantId);
            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }
}
