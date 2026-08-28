using DocuEngAIne.Core.Common;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Core.Entities;

public class RunbookRun : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid RunbookId { get; set; }
    public Runbook Runbook { get; set; } = null!;

    public Guid? CompanyId { get; set; }
    public Company? Company { get; set; }

    public RunbookRunStatus Status { get; set; } = RunbookRunStatus.Running;
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public string? StartedByObjectId { get; set; }
}
