using DocuEngAIne.Core.Common;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Core.Entities;

/// <summary>
/// One allowed source network for a tenant. Only consulted while
/// <see cref="Tenant.IpAllowlistEnabled"/> is on; <see cref="Cidr"/> is always stored normalized
/// (network address + prefix, e.g. <c>203.0.113.0/24</c>, <c>198.51.100.7/32</c>).
/// </summary>
public class IpAllowlistEntry : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public string? Label { get; set; }
    public required string Cidr { get; set; }
    public bool IsActive { get; set; } = true;
    public string? CreatedByObjectId { get; set; }
}
