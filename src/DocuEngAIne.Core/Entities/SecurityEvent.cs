using DocuEngAIne.Core.Common;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Core.Entities;

/// <summary>
/// One security event (<see cref="Enums.SecurityEventTypes"/>) in a tenant. Repeats of the same kind
/// from the same address and caller within a few minutes are one row with a <see cref="Count"/>, so a
/// script hammering a blocked address cannot flood the log.
/// </summary>
public class SecurityEvent : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public required string EventType { get; set; }
    public required string Severity { get; set; }
    public required string Description { get; set; }

    public string? IpAddress { get; set; }
    /// <summary>An Entra object id, or <c>apitoken:{id}</c> for an API token.</summary>
    public string? ActorObjectId { get; set; }
    public string? ActorName { get; set; }
    /// <summary>The route that was refused, last seen.</summary>
    public string? Path { get; set; }

    public int Count { get; set; } = 1;
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}
