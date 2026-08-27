using DocuEngAIne.Core.Common;

namespace DocuEngAIne.Core.Entities;

public class AssetDocumentLink : EntityBase
{
    public Guid AssetId { get; set; }
    public Asset Asset { get; set; } = null!;

    public Guid DocumentId { get; set; }
    public Document Document { get; set; } = null!;
}
