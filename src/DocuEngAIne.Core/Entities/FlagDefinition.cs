using DocuEngAIne.Core.Common;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Core.Entities;

public class FlagDefinition : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public required string Name { get; set; }
    /// <summary>Hex color, e.g. #DC2626.</summary>
    public required string Color { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<FlagAssignment> Assignments { get; set; } = [];
}
