using DocuEngAIne.Core.Common;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Core.Entities;

public class Asset : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public required string Name { get; set; }
    public string? Location { get; set; }
    public string? Notes { get; set; }
    public string? Status { get; set; } = "Active";

    public Guid? CompanyId { get; set; }
    public Company? Company { get; set; }

    public Guid AssetTypeId { get; set; }
    public AssetType AssetType { get; set; } = null!;

    /// <summary>Optional first-class expiration shortcut, rolled up with date custom fields.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Optional Halo PSA asset deep link. URL only — no secrets.</summary>
    public string? HaloAssetUrl { get; set; }
    /// <summary>Optional NinjaOne device deep link. URL only — no secrets.</summary>
    public string? NinjaDeviceUrl { get; set; }
    /// <summary>Provider external ids, same shape as <see cref="Company.ExternalIdsJson"/>.</summary>
    public string? ExternalIdsJson { get; set; }

    public ICollection<CustomFieldValue> CustomFieldValues { get; set; } = [];
    public ICollection<AssetDocumentLink> Documents { get; set; } = [];
}
