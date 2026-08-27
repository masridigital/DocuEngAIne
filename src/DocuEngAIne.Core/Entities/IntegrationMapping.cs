using DocuEngAIne.Core.Common;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Core.Entities;

public class IntegrationMapping : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid IntegrationConnectionId { get; set; }
    public IntegrationConnection IntegrationConnection { get; set; } = null!;

    public required string ExternalId { get; set; }
    public required string ExternalType { get; set; }
    public required string LocalEntityType { get; set; }
    public Guid LocalEntityId { get; set; }
    public string? MetadataJson { get; set; }
}
