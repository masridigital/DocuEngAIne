using DocuEngAIne.Core.Common;

namespace DocuEngAIne.Core.Entities;

public class CustomFieldValue : EntityBase
{
    public Guid AssetId { get; set; }
    public Asset Asset { get; set; } = null!;

    public Guid FieldDefinitionId { get; set; }
    public FieldDefinition FieldDefinition { get; set; } = null!;

    public string? Value { get; set; }
}
