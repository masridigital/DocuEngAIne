using DocuEngAIne.Core.Common;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Core.Entities;

public class KeeperLink : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public required string Name { get; set; }
    public string? UsernameHint { get; set; }
    public string? KeeperRecordUrl { get; set; }
    public string? KeeperRecordUid { get; set; }
    public string? Notes { get; set; }
    public string? AssociatedResourceType { get; set; }
    public Guid? AssociatedResourceId { get; set; }
}
