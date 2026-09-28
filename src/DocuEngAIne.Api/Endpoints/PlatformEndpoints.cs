using System.Text.Json;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using DocuEngAIne.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

/// <summary>
/// The platform console: every tenant on this deployment and its lifecycle. Only the operators
/// configured on the host can reach it (<see cref="AuthExtensions.PlatformOperatorPolicy"/>). A
/// suspended or archived tenant is refused on every route and by its API tokens, and its sync
/// stops; its data is kept, and reactivating restores everything. Each change is written to the
/// operator's own audit log and to the affected tenant's, so its administrators can see who closed
/// it and why. An operator cannot close the tenant they are signed in to.
/// </summary>
public static class PlatformEndpoints
{
    public const string ReasonRequiredMessage = "Say why the tenant is being suspended: its users see the reason.";
    public const string OwnTenantMessage = "You cannot suspend or archive the tenant you are signed in to.";
    public const int ReasonMaxLength = 500;

    public static IEndpointRouteBuilder MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/platform").RequireAuthorization(AuthExtensions.PlatformOperatorPolicy);
        group.MapGet("/tenants", ListTenantsAsync);
        group.MapPost("/tenants/{id:guid}/suspend", SuspendAsync);
        group.MapPost("/tenants/{id:guid}/archive", ArchiveAsync);
        group.MapPost("/tenants/{id:guid}/reactivate", ReactivateAsync);
        return app;
    }

    public sealed record TenantSummary(
        Guid Id,
        string Name,
        string Slug,
        string? PrimaryDomain,
        TenantStatus Status,
        string? StatusReason,
        DateTimeOffset? StatusChangedAt,
        DateTimeOffset CreatedAt,
        int ActiveUsers,
        int Companies);

    public sealed record StatusChangeRequest(string? Reason = null);

    public static async Task<IResult> ListTenantsAsync(DocuEngAIneDbContext db, CancellationToken cancellationToken = default)
    {
        // Deliberately across tenants, and past the operator's own company scope. That reads past
        // the Museum too, so archived companies are left out of the count by hand.
        var tenants = await db.Tenants.IgnoreQueryFilters().AsNoTracking()
            .OrderBy(t => t.Name)
            .Select(t => new TenantSummary(
                t.Id,
                t.Name,
                t.Slug,
                t.PrimaryDomain,
                t.Status,
                t.StatusReason,
                t.StatusChangedAt,
                t.CreatedAt,
                t.Users.Count(u => u.IsActive),
                t.Companies.Count(c => c.DeletedAt == null)))
            .ToListAsync(cancellationToken);
        return Results.Ok(tenants);
    }

    public static Task<IResult> SuspendAsync(
        Guid id,
        [FromBody] StatusChangeRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        TenantStatusService statuses,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
        => ChangeAsync(id, TenantStatus.Suspended, request.Reason, db, user, statuses, audit, cancellationToken);

    public static Task<IResult> ArchiveAsync(
        Guid id,
        [FromBody] StatusChangeRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        TenantStatusService statuses,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
        => ChangeAsync(id, TenantStatus.Archived, request.Reason, db, user, statuses, audit, cancellationToken);

    public static Task<IResult> ReactivateAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        TenantStatusService statuses,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
        => ChangeAsync(id, TenantStatus.Active, null, db, user, statuses, audit, cancellationToken);

    private static async Task<IResult> ChangeAsync(
        Guid id,
        TenantStatus target,
        string? requestedReason,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        TenantStatusService statuses,
        IAuditService? audit,
        CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (tenant is null)
            return Results.NotFound();
        if (target != TenantStatus.Active && tenant.Id == user.TenantId)
            return Results.Conflict(new { error = OwnTenantMessage });
        if (target == TenantStatus.Suspended && tenant.Status == TenantStatus.Archived)
            return Results.Conflict(new { error = "An archived tenant cannot be suspended. Reactivate it first." });

        var reason = Clean(requestedReason);
        if (target == TenantStatus.Suspended && reason is null)
            return Results.BadRequest(ReasonRequiredMessage);
        if (target == TenantStatus.Active)
            reason = null;

        if (tenant.Status == target && tenant.StatusReason == reason)
            return Results.Ok(await SummaryAsync(db, tenant.Id, cancellationToken));

        var previousStatus = tenant.Status;
        var previousReason = tenant.StatusReason;
        tenant.Status = target;
        tenant.StatusReason = reason;
        tenant.StatusChangedAt = DateTimeOffset.UtcNow;
        tenant.StatusChangedByObjectId = user.ObjectId;

        var changes = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["status"] = new { from = previousStatus.ToString(), to = target.ToString() },
            ["statusReason"] = new { from = previousReason, to = reason },
        });
        var details = target switch
        {
            TenantStatus.Suspended => $"Tenant suspended: {reason}",
            TenantStatus.Archived => reason is null ? "Tenant archived" : $"Tenant archived: {reason}",
            _ => "Tenant reactivated",
        };

        // The tenant's own trail says who closed it and why. Written straight to its log: the
        // audit service would file it under the operator's tenant.
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenant.Id,
            ActorObjectId = user.ObjectId,
            ActorName = $"Platform operator ({user.Email ?? user.ObjectId ?? "unknown"})",
            Action = $"Tenant.{target}",
            EntityType = nameof(Tenant),
            EntityId = tenant.Id,
            Details = details,
            Category = AuditCategories.Security,
            TargetLabel = tenant.Name,
            ChangesJson = changes,
        });
        await db.SaveChangesAsync(cancellationToken);
        statuses.Invalidate(tenant.Id);

        if (audit is not null)
        {
            await audit.LogAsync(
                new AuditEntry($"Platform.Tenant{target}", nameof(Tenant), tenant.Id, details, AuditCategories.Security, tenant.Name, changes),
                cancellationToken);
        }

        return Results.Ok(await SummaryAsync(db, tenant.Id, cancellationToken));
    }

    private static async Task<TenantSummary> SummaryAsync(DocuEngAIneDbContext db, Guid id, CancellationToken cancellationToken)
        => await db.Tenants.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new TenantSummary(
                t.Id,
                t.Name,
                t.Slug,
                t.PrimaryDomain,
                t.Status,
                t.StatusReason,
                t.StatusChangedAt,
                t.CreatedAt,
                t.Users.Count(u => u.IsActive),
                t.Companies.Count(c => c.DeletedAt == null)))
            .FirstAsync(cancellationToken);

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length <= ReasonMaxLength ? trimmed : trimmed[..ReasonMaxLength];
    }
}
