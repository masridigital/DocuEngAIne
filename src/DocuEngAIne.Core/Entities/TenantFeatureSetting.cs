using DocuEngAIne.Core.Common;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Core.Entities;

/// <summary>
/// A tenant's choice for one <see cref="Enums.TenantFeatures"/> key. No row means the feature's
/// default applies.
/// </summary>
public class TenantFeatureSetting : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public required string Key { get; set; }
    public bool IsEnabled { get; set; }
    public string? UpdatedByObjectId { get; set; }
}
