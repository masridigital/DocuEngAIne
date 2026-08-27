using DocuEngAIne.Core.Common;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Core.Entities;

public class User : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public required string EntraObjectId { get; set; }
    public required string Email { get; set; }
    public string? DisplayName { get; set; }
    public UserRole Role { get; set; } = UserRole.Reader;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastSeenAt { get; set; }
}
