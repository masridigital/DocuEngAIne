using System.Text;
using System.Text.Json;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

/// <summary>
/// User-access certification (SOC 2 / cyber-insurance "who has access, and was it reviewed").
/// Draft → start (snapshots every active user's role and grants) → decide each item → complete,
/// which is refused while anything is pending. Decisions act immediately, through the same guards
/// as the Users page: a revoke deactivates the user, a role change changes the role.
/// </summary>
public static class AccessReviewEndpoints
{
    public const string NameRequiredMessage = "Name is required.";
    public const string ReviewerNotFoundMessage = "Reviewer must be an active user in this tenant.";
    public const string NotDraftMessage = "Only a draft review can be started.";
    public const string NotInProgressMessage = "The review is not in progress.";
    public const string NoSubjectsMessage = "There are no active users to review.";
    public const string AlreadyDecidedMessage = "This item has already been decided.";
    public const string PendingItemsMessage = "Every item must be decided before the review can be completed.";
    public const string SelfDecisionMessage = "You cannot revoke or change your own access. Another administrator must decide this item.";
    public const string RequestedRoleMessage = "Change role requires a defined role different from the current one.";
    public const string SubjectGoneMessage = "The subject's user record no longer exists.";
    public const string CannotCancelMessage = "Only a draft or in-progress review can be cancelled.";

    public static IEndpointRouteBuilder MapAccessReviewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/access-reviews")
            .RequireAuthorization(AuthExtensions.AdminPolicy)
            .RequireTenantFeature(TenantFeatures.AccessReviews);

        group.MapGet("", ListAsync);
        group.MapPost("", CreateAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPost("/{id:guid}/start", StartAsync);
        group.MapPatch("/{id:guid}/items/{itemId:guid}", DecideAsync);
        group.MapPost("/{id:guid}/complete", CompleteAsync);
        group.MapPost("/{id:guid}/cancel", CancelAsync);
        group.MapGet("/{id:guid}/export", ExportAsync);

        return app;
    }

    public sealed record CreateAccessReviewRequest(string? Name, Guid? ReviewerUserId = null, DateTimeOffset? DueAt = null, string? Notes = null);

    public sealed record DecideItemRequest(AccessReviewDecision? Decision, UserRole? RequestedRole = null, string? Notes = null);

    public sealed record AccessReviewSummary(
        Guid Id,
        string Name,
        AccessReviewStatus Status,
        Guid? ReviewerUserId,
        DateTimeOffset? DueAt,
        DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt,
        DateTimeOffset? CancelledAt,
        int ItemCount,
        int PendingCount,
        DateTimeOffset CreatedAt);

    public sealed record AccessReviewItemView(
        Guid Id,
        Guid SubjectUserId,
        string SubjectEmail,
        string? SubjectName,
        UserRole RoleAtSnapshot,
        int GrantCount,
        AccessReviewDecision Decision,
        UserRole? RequestedRole,
        string? DecidedByName,
        DateTimeOffset? DecidedAt,
        string? DecisionNotes);

    public sealed record AccessReviewDetail(AccessReviewSummary Review, string? Notes, IReadOnlyList<AccessReviewItemView> Items);

    public static async Task<IResult> ListAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        var reviews = await db.AccessReviews.ForTenant(user).AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new
            {
                Review = r,
                ItemCount = r.Items.Count,
                PendingCount = r.Items.Count(i => i.Decision == AccessReviewDecision.Pending),
            })
            .ToListAsync(cancellationToken);

        return Results.Ok(reviews.Select(x => Summarize(x.Review, x.ItemCount, x.PendingCount)).ToList());
    }

    public static async Task<IResult> CreateAsync(
        [FromBody] CreateAccessReviewRequest? request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        var name = request?.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(NameRequiredMessage);

        if (request!.ReviewerUserId is Guid reviewerId
            && !await db.Users.ForTenant(user).AnyAsync(u => u.Id == reviewerId && u.IsActive, cancellationToken))
        {
            return Results.BadRequest(ReviewerNotFoundMessage);
        }

        var review = new AccessReview
        {
            TenantId = user.TenantId.Value,
            Name = Truncate(name, 150),
            ReviewerUserId = request.ReviewerUserId,
            CreatedByObjectId = user.ObjectId,
            DueAt = request.DueAt,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : Truncate(request.Notes.Trim(), 2000),
        };
        db.AccessReviews.Add(review);
        await db.SaveChangesAsync(cancellationToken);

        await LogAsync(audit, "AccessReview.Create", review, "Draft created", cancellationToken);
        return Results.Created($"/api/access-reviews/{review.Id}", Summarize(review, 0, 0));
    }

    public static async Task<IResult> GetAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        var review = await db.AccessReviews.ForTenant(user).AsNoTracking()
            .Include(r => r.Items)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (review is null)
            return Results.NotFound();

        var items = review.Items
            .OrderBy(i => i.SubjectEmail, StringComparer.OrdinalIgnoreCase)
            .Select(MapItem)
            .ToList();
        var pending = review.Items.Count(i => i.Decision == AccessReviewDecision.Pending);
        return Results.Ok(new AccessReviewDetail(Summarize(review, review.Items.Count, pending), review.Notes, items));
    }

    /// <summary>
    /// Freezes the population: every active user in the tenant with their role and grants at this
    /// moment. Users provisioned after the start are not part of this review — that is the point.
    /// </summary>
    public static async Task<IResult> StartAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        var review = await db.AccessReviews.ForTenant(user).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (review is null)
            return Results.NotFound();
        if (review.Status != AccessReviewStatus.Draft)
            return Results.Conflict(new { error = NotDraftMessage });

        var subjects = await db.Users.ForTenant(user).AsNoTracking()
            .Where(u => u.IsActive)
            .OrderBy(u => u.Email)
            .ToListAsync(cancellationToken);
        if (subjects.Count == 0)
            return Results.BadRequest(NoSubjectsMessage);

        var subjectIds = subjects.Select(u => u.Id).ToList();
        var grants = await db.ResourceRoleAssignments.ForTenant(user).AsNoTracking()
            .Where(g => subjectIds.Contains(g.UserId))
            .ToListAsync(cancellationToken);
        var grantsByUser = grants.ToLookup(g => g.UserId);

        foreach (var subject in subjects)
        {
            var userGrants = grantsByUser[subject.Id]
                .OrderBy(g => g.ResourceType)
                .ThenBy(g => g.ResourceId)
                .Select(g => new { g.ResourceType, g.ResourceId, Role = g.Role.ToString() })
                .ToList();

            db.AccessReviewItems.Add(new AccessReviewItem
            {
                TenantId = review.TenantId,
                AccessReviewId = review.Id,
                SubjectUserId = subject.Id,
                SubjectEmail = subject.Email,
                SubjectName = subject.DisplayName,
                RoleAtSnapshot = subject.Role,
                GrantCount = userGrants.Count,
                AccessSnapshotJson = JsonSerializer.Serialize(new { role = subject.Role.ToString(), grants = userGrants }),
            });
        }

        review.Status = AccessReviewStatus.InProgress;
        review.StartedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        await LogAsync(audit, "AccessReview.Start", review, $"Snapshotted {subjects.Count} active user(s)", cancellationToken);
        return Results.Ok(Summarize(review, subjects.Count, subjects.Count));
    }

    /// <summary>
    /// Records one decision and applies it in the same save. Revoke and change-role run through
    /// the Users page's guards, so a review cannot do what the Users page would refuse: lock out the
    /// last Owner, let an Admin act on an Owner, or let a reviewer strip or raise their own access.
    /// </summary>
    public static async Task<IResult> DecideAsync(
        Guid id,
        Guid itemId,
        [FromBody] DecideItemRequest? request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        if (request?.Decision is not AccessReviewDecision decision
            || decision == AccessReviewDecision.Pending
            || !Enum.IsDefined(decision))
        {
            return Results.BadRequest("Decision must be Retain, Revoke, or ChangeRole.");
        }

        var review = await db.AccessReviews.ForTenant(user).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (review is null)
            return Results.NotFound();
        if (review.Status != AccessReviewStatus.InProgress)
            return Results.Conflict(new { error = NotInProgressMessage });

        var item = await db.AccessReviewItems.ForTenant(user)
            .FirstOrDefaultAsync(i => i.Id == itemId && i.AccessReviewId == review.Id, cancellationToken);
        if (item is null)
            return Results.NotFound();
        if (item.Decision != AccessReviewDecision.Pending)
            return Results.Conflict(new { error = AlreadyDecidedMessage });

        var subject = await db.Users.ForTenant(user).FirstOrDefaultAsync(u => u.Id == item.SubjectUserId, cancellationToken);
        if (subject is null)
            return Results.Conflict(new { error = SubjectGoneMessage });

        var isSelf = !string.IsNullOrEmpty(user.ObjectId)
            && string.Equals(subject.EntraObjectId, user.ObjectId, StringComparison.OrdinalIgnoreCase);
        if (isSelf && decision != AccessReviewDecision.Retain)
            return Results.BadRequest(SelfDecisionMessage);

        AuditEntry? userChange = null;
        switch (decision)
        {
            case AccessReviewDecision.Revoke when subject.IsActive:
                if (await UserEndpoints.GuardDeactivateAsync(db, user, subject, cancellationToken) is { } refusedRevoke)
                    return refusedRevoke;
                subject.IsActive = false;
                userChange = new AuditEntry(
                    "User.Deactivate",
                    nameof(User),
                    subject.Id,
                    $"Access revoked in review '{review.Name}'",
                    Category: AuditCategories.Security,
                    TargetLabel: subject.DisplayName ?? subject.Email,
                    ChangesJson: JsonSerializer.Serialize(new { isActive = new { from = true, to = false } }));
                break;

            case AccessReviewDecision.ChangeRole:
                if (request.RequestedRole is not UserRole requested
                    || !Enum.IsDefined(requested)
                    || requested == subject.Role)
                {
                    return Results.BadRequest(RequestedRoleMessage);
                }

                if (await UserEndpoints.GuardRoleChangeAsync(db, user, subject, requested, cancellationToken) is { } refusedChange)
                    return refusedChange;
                var previous = subject.Role;
                subject.Role = requested;
                userChange = new AuditEntry(
                    "User.ChangeRole",
                    nameof(User),
                    subject.Id,
                    $"Role changed from {previous} to {requested} in review '{review.Name}'",
                    Category: AuditCategories.Security,
                    TargetLabel: subject.DisplayName ?? subject.Email,
                    ChangesJson: JsonSerializer.Serialize(new { role = new { from = previous.ToString(), to = requested.ToString() } }));
                break;
        }

        item.Decision = decision;
        item.RequestedRole = decision == AccessReviewDecision.ChangeRole ? request.RequestedRole : null;
        item.DecidedByObjectId = user.ObjectId;
        item.DecidedByName = user.DisplayName ?? user.Email;
        item.DecidedAt = DateTimeOffset.UtcNow;
        item.DecisionNotes = string.IsNullOrWhiteSpace(request.Notes) ? null : Truncate(request.Notes.Trim(), 2000);
        await db.SaveChangesAsync(cancellationToken);

        if (audit is not null)
        {
            await audit.LogAsync(
                new AuditEntry(
                    "AccessReview.Decide",
                    nameof(AccessReview),
                    review.Id,
                    $"{decision} for {subject.Email}",
                    Category: AuditCategories.Access,
                    TargetLabel: review.Name),
                cancellationToken);
            if (userChange is not null)
                await audit.LogAsync(userChange, cancellationToken);
        }

        return Results.Ok(MapItem(item));
    }

    public static async Task<IResult> CompleteAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        var review = await db.AccessReviews.ForTenant(user).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (review is null)
            return Results.NotFound();
        if (review.Status != AccessReviewStatus.InProgress)
            return Results.Conflict(new { error = NotInProgressMessage });

        var reviewId = review.Id;
        var pending = await db.AccessReviewItems.ForTenant(user)
            .CountAsync(i => i.AccessReviewId == reviewId && i.Decision == AccessReviewDecision.Pending, cancellationToken);
        if (pending > 0)
            return Results.Conflict(new { error = PendingItemsMessage, pending });

        review.Status = AccessReviewStatus.Completed;
        review.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        await LogAsync(audit, "AccessReview.Complete", review, "All items decided", cancellationToken);
        return Results.NoContent();
    }

    public static async Task<IResult> CancelAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        var review = await db.AccessReviews.ForTenant(user).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (review is null)
            return Results.NotFound();
        if (review.Status is not (AccessReviewStatus.Draft or AccessReviewStatus.InProgress))
            return Results.Conflict(new { error = CannotCancelMessage });

        review.Status = AccessReviewStatus.Cancelled;
        review.CancelledAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        await LogAsync(audit, "AccessReview.Cancel", review, "Cancelled; decisions already applied stay applied", cancellationToken);
        return Results.NoContent();
    }

    /// <summary>The evidence an auditor asks for: who had what at start, who decided, what, and when.</summary>
    public static async Task<IResult> ExportAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        var review = await db.AccessReviews.ForTenant(user).AsNoTracking()
            .Include(r => r.Items)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (review is null)
            return Results.NotFound();

        var sb = new StringBuilder();
        sb.AppendLine("review,status,started_at,completed_at,subject_email,subject_name,role_at_start,grant_count,decision,requested_role,decided_by,decided_at,notes");
        foreach (var item in review.Items.OrderBy(i => i.SubjectEmail, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendJoin(',',
                AuditEndpoints.CsvField(review.Name),
                AuditEndpoints.CsvField(review.Status.ToString()),
                AuditEndpoints.CsvField(review.StartedAt?.ToString("O")),
                AuditEndpoints.CsvField(review.CompletedAt?.ToString("O")),
                AuditEndpoints.CsvField(item.SubjectEmail),
                AuditEndpoints.CsvField(item.SubjectName),
                AuditEndpoints.CsvField(item.RoleAtSnapshot.ToString()),
                AuditEndpoints.CsvField(item.GrantCount.ToString()),
                AuditEndpoints.CsvField(item.Decision.ToString()),
                AuditEndpoints.CsvField(item.RequestedRole?.ToString()),
                AuditEndpoints.CsvField(item.DecidedByName ?? item.DecidedByObjectId),
                AuditEndpoints.CsvField(item.DecidedAt?.ToString("O")),
                AuditEndpoints.CsvField(item.DecisionNotes));
            sb.AppendLine();
        }

        await LogAsync(audit, "AccessReview.Export", review, $"rows={review.Items.Count}", cancellationToken);
        return Results.File(
            Encoding.UTF8.GetBytes(sb.ToString()),
            "text/csv",
            $"access-review-{review.CreatedAt:yyyy-MM-dd}.csv");
    }

    private static Task LogAsync(IAuditService? audit, string action, AccessReview review, string details, CancellationToken cancellationToken)
        => audit is null
            ? Task.CompletedTask
            : audit.LogAsync(
                new AuditEntry(action, nameof(AccessReview), review.Id, details, Category: AuditCategories.Access, TargetLabel: review.Name),
                cancellationToken);

    private static AccessReviewSummary Summarize(AccessReview r, int itemCount, int pendingCount) => new(
        r.Id, r.Name, r.Status, r.ReviewerUserId, r.DueAt, r.StartedAt, r.CompletedAt, r.CancelledAt,
        itemCount, pendingCount, r.CreatedAt);

    private static AccessReviewItemView MapItem(AccessReviewItem i) => new(
        i.Id, i.SubjectUserId, i.SubjectEmail, i.SubjectName, i.RoleAtSnapshot, i.GrantCount,
        i.Decision, i.RequestedRole, i.DecidedByName, i.DecidedAt, i.DecisionNotes);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
