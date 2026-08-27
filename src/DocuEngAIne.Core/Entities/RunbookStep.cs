using DocuEngAIne.Core.Common;

namespace DocuEngAIne.Core.Entities;

public class RunbookStep : EntityBase
{
    public Guid RunbookId { get; set; }
    public Runbook Runbook { get; set; } = null!;

    public int Order { get; set; }
    public required string Title { get; set; }
    public string? Details { get; set; }
    public string? StepType { get; set; } = "Manual";
    public bool IsRequired { get; set; } = true;
    public string? ExpectedOutput { get; set; }
}
