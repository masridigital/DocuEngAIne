using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DocuEngAIne.Infrastructure.Security;

/// <summary>
/// Writes a tenant's security events. The same kind of event from the same address and caller within
/// <see cref="MergeWindow"/> of the last one adds to that row's count instead of a new row. Recording
/// never fails the request it is about: a refusal is still a refusal if the log cannot be written.
/// </summary>
public sealed class SecurityEventRecorder
{
    public static readonly TimeSpan MergeWindow = TimeSpan.FromMinutes(10);

    private readonly DocuEngAIneDbContext _db;
    private readonly ILogger<SecurityEventRecorder>? _logger;

    public SecurityEventRecorder(DocuEngAIneDbContext db, ILogger<SecurityEventRecorder>? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    public async Task RecordAsync(
        Guid tenantId,
        string eventType,
        string description,
        string? ipAddress = null,
        string? actorObjectId = null,
        string? actorName = null,
        string? path = null,
        DateTimeOffset? utcNow = null,
        CancellationToken cancellationToken = default)
    {
        var definition = SecurityEventTypes.Find(eventType)
            ?? throw new ArgumentException($"'{eventType}' is not a registered security event.", nameof(eventType));

        try
        {
            var now = utcNow ?? DateTimeOffset.UtcNow;
            var since = now - MergeWindow;
            var ip = Clip(ipAddress, 45);
            var actor = Clip(actorObjectId, 128);

            var open = await _db.SecurityEvents
                .Where(e => e.TenantId == tenantId
                    && e.EventType == definition.Type
                    && e.IpAddress == ip
                    && e.ActorObjectId == actor
                    && e.LastSeenAt >= since)
                .OrderByDescending(e => e.LastSeenAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (open is not null)
            {
                open.Count++;
                open.LastSeenAt = now;
                open.Path = Clip(path, 256);
            }
            else
            {
                _db.SecurityEvents.Add(new SecurityEvent
                {
                    TenantId = tenantId,
                    EventType = definition.Type,
                    Severity = definition.Severity,
                    Description = Clip(description, 500) ?? definition.Name,
                    IpAddress = ip,
                    ActorObjectId = actor,
                    ActorName = Clip(actorName, 200),
                    Path = Clip(path, 256),
                    FirstSeenAt = now,
                    LastSeenAt = now,
                });
            }

            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Leave nothing half-written in the request's context for a later save to trip over.
            foreach (var entry in _db.ChangeTracker.Entries<SecurityEvent>().ToList())
                entry.State = EntityState.Detached;

            _logger?.LogWarning(ex, "Could not record security event {EventType} for tenant {TenantId}.", eventType, tenantId);
        }
    }

    private static string? Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
