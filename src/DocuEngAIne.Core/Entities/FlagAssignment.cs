using DocuEngAIne.Core.Common;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Core.Entities;

public class FlagAssignment : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid FlagDefinitionId { get; set; }
    public FlagDefinition FlagDefinition { get; set; } = null!;

    public required string EntityType { get; set; }
    public Guid EntityId { get; set; }
}
