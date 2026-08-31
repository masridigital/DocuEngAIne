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
}
