using DocuEngAIne.Core.Common;

namespace DocuEngAIne.Core.Entities;

public class AuditLog : EntityBase
{
    public Guid? TenantId { get; set; }
    public Guid? UserId { get; set; }
    public required string Action { get; set; }
    public required string EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
}
