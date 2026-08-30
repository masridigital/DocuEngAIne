using DocuEngAIne.Core.Entities;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Picks which connections a scheduler tick should hand to <see cref="Core.Interfaces.IIntegrationSyncService.SyncAsync"/>.
/// Cadence is <see cref="SyncCadencePolicy"/>: <c>SyncIntervalMinutesOverride</c> when set, otherwise
/// the interval derived from the detected plan (unknown plan stays manual).
/// </summary>
public static class IntegrationSyncWork
{
    /// <summary>
    /// Connections that are enabled, have a Compact <c>McpServerId</c>, are due now, and do not
    /// already have a live <c>SyncRun</c>. Order is stable for tests.
    /// </summary>
    public static IReadOnlyList<IntegrationConnection> DueConnections(
        IEnumerable<IntegrationConnection> connections,
        IReadOnlySet<Guid> runningConnectionIds,
        DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(connections);
        ArgumentNullException.ThrowIfNull(runningConnectionIds);

        return connections
            .Where(c => IsDue(c, runningConnectionIds, utcNow))
            .OrderBy(c => c.TenantId)
            .ThenBy(c => c.Id)
            .ToList();
    }

    public static bool IsDue(
        IntegrationConnection connection,
        IReadOnlySet<Guid> runningConnectionIds,
        DateTimeOffset utcNow)
    {
        if (!connection.IsEnabled)
            return false;

        // Scheduler does not adopt a tenant Compact server the way create/sync HTTP paths do.
        // A missing id means the connection is not ready for unattended pulls.
        if (connection.McpServerId is null)
            return false;

        if (runningConnectionIds.Contains(connection.Id))
            return false;

        return SyncCadencePolicy.NextDueAt(connection, utcNow) is DateTimeOffset due && due <= utcNow;
    }
}
