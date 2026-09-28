using DocuEngAIne.Core.Common;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Core.Entities;

/// <summary>
/// An asset layout: the named field set assets of one kind carry. Layouts built in the app start as
/// drafts and are offered for new assets once published; layouts created by sync and import are
/// published from the start.
/// </summary>
public class AssetType : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? Icon { get; set; }

    /// <summary>Only published layouts can be used for new assets. Existing assets are unaffected either way.</summary>
    public bool IsPublished { get; set; } = true;
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>
    /// When false, only companies with an <see cref="AssetTypeCompanyActivation"/> can have new
    /// assets of this layout (and it cannot be used for tenant-wide assets).
    /// </summary>
    public bool AvailableToAllCompanies { get; set; } = true;

    /// <summary>Number of the latest <see cref="AssetTypeVersion"/>; 0 before the first is recorded.</summary>
    public int CurrentVersion { get; set; }

    public ICollection<FieldDefinition> Fields { get; set; } = [];
    public ICollection<Asset> Assets { get; set; } = [];
    public ICollection<AssetTypeCompanyActivation> CompanyActivations { get; set; } = [];
    public ICollection<AssetTypeVersion> Versions { get; set; } = [];
}

public class FieldDefinition : EntityBase
{
    public Guid AssetTypeId { get; set; }
    public AssetType AssetType { get; set; } = null!;

    public required string Name { get; set; }

    /// <summary>One of <see cref="Enums.AssetFieldType"/>.</summary>
    public required string FieldType { get; set; }
    public bool IsRequired { get; set; }
    public bool IsExpiration { get; set; }
    public int SortOrder { get; set; }

    /// <summary>Heading the field is grouped under on the asset form; fields without one come first.</summary>
    public string? Section { get; set; }
    public string? HelpText { get; set; }

    /// <summary>The choices for a Select or MultiSelect field.</summary>
    public Guid? OptionListId { get; set; }
    public OptionList? OptionList { get; set; }
}

/// <summary>Allows a layout that is not available to every company to be used for one company.</summary>
public class AssetTypeCompanyActivation : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid AssetTypeId { get; set; }
    public AssetType AssetType { get; set; } = null!;

    public Guid CompanyId { get; set; }
    public Company Company { get; set; } = null!;

    public string? ActivatedByObjectId { get; set; }
}

/// <summary>
/// Immutable snapshot of a published layout's schema, written when it is published and again after
/// every schema change while it is published, so the history of what the layout asked for survives.
/// </summary>
public class AssetTypeVersion : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid AssetTypeId { get; set; }
    public AssetType AssetType { get; set; } = null!;

    public int VersionNumber { get; set; }
    public required string SchemaJson { get; set; }
    public string? Summary { get; set; }
    public string? CreatedByObjectId { get; set; }
    public string? CreatedByName { get; set; }
}
