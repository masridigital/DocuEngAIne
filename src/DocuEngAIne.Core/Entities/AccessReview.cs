using DocuEngAIne.Core.Common;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Core.Entities;

/// <summary>
/// A periodic user-access certification: who had what access, and what an administrator decided
/// about each of them. Starting the review snapshots every active user, so the items are evidence
/// of access at that moment — later role changes do not rewrite them.
/// </summary>
public class AccessReview : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public required string Name { get; set; }
    public AccessReviewStatus Status { get; set; } = AccessReviewStatus.Draft;

    /// <summary>Assigned reviewer (informational); any tenant administrator may decide items.</summary>
    public Guid? ReviewerUserId { get; set; }
    public string? CreatedByObjectId { get; set; }

    public DateTimeOffset? DueAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? Notes { get; set; }

    public ICollection<AccessReviewItem> Items { get; set; } = [];
}

/// <summary>
/// One subject in a review, frozen at start. <see cref="SubjectUserId"/> carries no FK so the
/// evidence outlives the user row.
/// </summary>
public class AccessReviewItem : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid AccessReviewId { get; set; }
    public AccessReview AccessReview { get; set; } = null!;

    public Guid SubjectUserId { get; set; }
    public required string SubjectEmail { get; set; }
    public string? SubjectName { get; set; }

    public UserRole RoleAtSnapshot { get; set; }
    public int GrantCount { get; set; }

    /// <summary><c>{"role":..,"grants":[{"resourceType","resourceId","role"}]}</c> at start.</summary>
    public string? AccessSnapshotJson { get; set; }

    public AccessReviewDecision Decision { get; set; } = AccessReviewDecision.Pending;
    public UserRole? RequestedRole { get; set; }
    public string? DecidedByObjectId { get; set; }
    public string? DecidedByName { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionNotes { get; set; }
}
