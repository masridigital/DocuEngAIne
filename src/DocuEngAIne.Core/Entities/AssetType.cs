using DocuEngAIne.Core.Common;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Core.Entities;

public class AssetType : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? Icon { get; set; }

    public ICollection<FieldDefinition> Fields { get; set; } = [];
    public ICollection<Asset> Assets { get; set; } = [];
}

public class FieldDefinition : EntityBase
{
    public Guid AssetTypeId { get; set; }
    public AssetType AssetType { get; set; } = null!;

    public required string Name { get; set; }
    public required string FieldType { get; set; } // Text, Number, Date, DateTime, Url, Markdown
    public bool IsRequired { get; set; }
    public bool IsExpiration { get; set; }
    public int SortOrder { get; set; }
}
