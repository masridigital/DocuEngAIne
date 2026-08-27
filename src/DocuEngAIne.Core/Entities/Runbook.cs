using DocuEngAIne.Core.Common;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Core.Entities;

public class Runbook : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid? CompanyId { get; set; }
    public Company? Company { get; set; }

    public required string Title { get; set; }
    public string? Slug { get; set; }
    public string? Description { get; set; }
    public string? Tags { get; set; }
    public bool IsPublished { get; set; } = true;

    public ICollection<RunbookStep> Steps { get; set; } = [];
}
