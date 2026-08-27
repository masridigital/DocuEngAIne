using DocuEngAIne.Core.Common;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Core.Entities;

public class ResourceRoleAssignment : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public required string ResourceType { get; set; }
    public Guid ResourceId { get; set; }
    public UserRole Role { get; set; }
}
