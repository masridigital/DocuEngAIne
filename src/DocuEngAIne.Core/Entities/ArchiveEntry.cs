using DocuEngAIne.Core.Common;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Core.Entities;

/// <summary>
/// Museum registry row: one per archive of a soft-deletable resource. The archived row itself is
/// hidden by the global query filter, so this entry carries what the Museum needs to list it
/// (<see cref="ResourceLabel"/>, who, when, why). It outlives a permanent delete as a tombstone,
/// so the trail of what existed and who destroyed it survives the row.
/// </summary>
public class ArchiveEntry : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    /// <summary>One of <see cref="Enums.ResourceType"/>.</summary>
    public required string ResourceType { get; set; }
    public Guid ResourceId { get; set; }

    /// <summary>
    /// The resource's company at archive time; null for a tenant-wide resource. Company scoping
    /// filters the Museum on it, because the archived row itself is hidden and cannot be joined.
    /// </summary>
    public Guid? CompanyId { get; set; }

    /// <summary>Name or title at archive time.</summary>
    public required string ResourceLabel { get; set; }
    public string? Reason { get; set; }

    public DateTimeOffset ArchivedAt { get; set; }
    public string? ArchivedByObjectId { get; set; }
    public string? ArchivedByName { get; set; }

    public DateTimeOffset? RestoredAt { get; set; }
    public string? RestoredByObjectId { get; set; }

    public DateTimeOffset? PermanentlyDeletedAt { get; set; }
    public string? PermanentlyDeletedByObjectId { get; set; }
}
