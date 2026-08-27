using DocuEngAIne.Core.Common;

namespace DocuEngAIne.Core.Entities;

public class DocumentVersion : EntityBase
{
    public Guid DocumentId { get; set; }
    public Document Document { get; set; } = null!;

    public int VersionNumber { get; set; }
    public required string Title { get; set; }
    public string? Slug { get; set; }
    public string? Summary { get; set; }
    public string? Content { get; set; }
    public string? Tags { get; set; }
    public string? ChangeNote { get; set; }
    public Guid? CreatedByUserId { get; set; }
}
