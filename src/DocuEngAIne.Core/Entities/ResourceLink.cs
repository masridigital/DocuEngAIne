using DocuEngAIne.Core.Common;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Core.Entities;

/// <summary>
/// Directed related-item link between two tenant-scoped records.
/// Not a graph visualization — Hudu Related Items job, Masri data model.
/// </summary>
public class ResourceLink : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public required string FromType { get; set; }
    public Guid FromId { get; set; }

    public required string ToType { get; set; }
    public Guid ToId { get; set; }

    public string? Label { get; set; }
}
