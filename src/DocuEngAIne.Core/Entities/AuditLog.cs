using DocuEngAIne.Core.Common;

namespace DocuEngAIne.Core.Entities;

public class AuditLog : EntityBase
{
    public Guid? TenantId { get; set; }
    public Guid? UserId { get; set; }

    /// <summary>
    /// Who acted, in the actor's own terms: an Entra object id for browser callers,
    /// <c>apitoken:{id}</c> for the outbound MCP token surface, <c>system:sync-scheduler</c> for
    /// background runs. <see cref="UserId"/> resolves only for Entra users, so without this column
    /// every token- and scheduler-written row was anonymous.
    /// </summary>
    public string? ActorObjectId { get; set; }

    public required string Action { get; set; }
    public required string EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }

    /// <summary>Coarse grouping for filtering: resource | access | archive | export | system | security.</summary>
    public string? Category { get; set; }

    /// <summary>Human name of the target at the time of the event (survives later rename/delete).</summary>
    public string? TargetLabel { get; set; }

    /// <summary>Structured before/after diff: <c>{"field":{"from":...,"to":...}}</c>. Never secrets.</summary>
    public string? ChangesJson { get; set; }

    /// <summary>Actor display name / email at the time of the event, denormalized so the trail survives user removal.</summary>
    public string? ActorName { get; set; }

    public string? RequestMethod { get; set; }
    public string? RequestPath { get; set; }
    public string? UserAgent { get; set; }
}
