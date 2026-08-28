using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

/// <summary>
/// Administers <see cref="ResourceRoleAssignment"/> — the per-resource grants that
/// <c>ResourceWriteGuard</c> enforces on asset, document, runbook and Keeper writes.
///
/// Without these routes the enforcement is unusable: grants would exist only as rows someone
/// inserted by hand in SQL, so a tenant where everyone is provisioned <see cref="UserRole.Reader"/>
/// would have exactly one person able to write anything, with no way to widen that from the product.
/// </summary>
public static class ResourceAccessEndpoints
{
    /// <summary>Resource types a grant may name. Must match <see cref="ResourceType"/> exactly — the
    /// guard compares these strings, so an unrecognised value would store a grant that never matches.</summary>
    private static readonly string[] KnownResourceTypes =
        [ResourceType.Asset, ResourceType.Document, ResourceType.Runbook, ResourceType.KeeperLink];

    public static IEndpointRouteBuilder MapResourceAccessEndpoints(this IEndpointRouteBuilder app)
    {
        // Admin-only, like /api/users: deciding who may write which record is administration, and the
        // listing alone reveals which records are considered sensitive enough to grant individually.
        var group = app.MapGroup("/api/resource-access").RequireAuthorization(AuthExtensions.AdminPolicy);

        group.MapGet("", async (
            DocuEngAIneDbContext db,
            ICurrentUser user,
            [FromQuery] string? resourceType,
            [FromQuery] Guid? resourceId,
            [FromQuery] Guid? userId,
            CancellationToken cancellationToken) =>
                await ListAsync(db, user, resourceType, resourceId, userId, cancellationToken));

        group.MapPost("", async (
            [FromBody] GrantResourceAccessRequest? request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            IAuditService audit,
            CancellationToken cancellationToken) =>
                await GrantAsync(request, db, user, audit, cancellationToken));

        group.MapDelete("/{id:guid}", async (
            Guid id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            IAuditService audit,
            CancellationToken cancellationToken) =>
                await RevokeAsync(id, db, user, audit, cancellationToken));

        return app;
    }

    /// <summary>Grants in this tenant, optionally narrowed to one resource or one user.</summary>
    public static async Task<IResult> ListAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        string? resourceType,
        Guid? resourceId,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        var query = db.ResourceRoleAssignments.ForTenant(user).AsNoTracking();

        if (!string.IsNullOrWhiteSpace(resourceType))
        {
            var normalized = NormalizeResourceType(resourceType);
            if (normalized is null)
                return Results.BadRequest(UnknownResourceTypeMessage(resourceType));
            query = query.Where(a => a.ResourceType == normalized);
        }

        if (resourceId is Guid rid)
            query = query.Where(a => a.ResourceId == rid);
        if (userId is Guid uid)
            query = query.Where(a => a.UserId == uid);

        // Joined to Users through the tenant-scoped set so a grant can never surface another
        // tenant's user, even if a row somehow named one.
        var users = await db.Users.ForTenant(user).AsNoTracking()
            .Select(u => new { u.Id, u.Email, u.DisplayName })
            .ToListAsync(cancellationToken);
        var byId = users.ToDictionary(u => u.Id);

        var grants = await query
            .OrderBy(a => a.ResourceType).ThenBy(a => a.ResourceId)
            .ToListAsync(cancellationToken);

        return Results.Ok(grants.Select(a => new ResourceAccessItem(
            a.Id,
            a.UserId,
            byId.TryGetValue(a.UserId, out var u) ? u.Email : null,
            byId.TryGetValue(a.UserId, out var d) ? d.DisplayName : null,
            a.ResourceType,
            a.ResourceId,
            a.Role,
            a.CreatedAt)));
    }

    /// <summary>
    /// Creates or updates one grant. Re-granting the same (user, type, resource) updates the role
    /// rather than stacking a second row, because the guard reads a single assignment and a duplicate
    /// pair would make the effective role depend on row order.
    /// </summary>
    public static async Task<IResult> GrantAsync(
        GrantResourceAccessRequest? request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IAuditService audit,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        // A missing body would otherwise bind every field to its default: Guid.Empty for the user and
        // resource, and UserRole.None for the role — a grant naming nobody, silently stored.
        if (request is null || request.Role is null)
            return Results.BadRequest("userId, resourceType, resourceId and role are all required.");

        var normalized = NormalizeResourceType(request.ResourceType);
        if (normalized is null)
            return Results.BadRequest(UnknownResourceTypeMessage(request.ResourceType));

        if (!Enum.IsDefined(request.Role.Value))
            return Results.BadRequest($"'{(int)request.Role.Value}' is not a valid role.");

        if (request.UserId == Guid.Empty || request.ResourceId == Guid.Empty)
            return Results.BadRequest("userId and resourceId must both be supplied.");

        // ForTenant, so granting to another tenant's user is a 404 rather than a cross-tenant grant.
        var target = await db.Users.ForTenant(user)
            .FirstOrDefaultAsync(u => u.Id == request.UserId, cancellationToken);
        if (target is null)
            return Results.NotFound();

        var existing = await db.ResourceRoleAssignments.ForTenant(user)
            .FirstOrDefaultAsync(a =>
                a.UserId == request.UserId
                && a.ResourceType == normalized
                && a.ResourceId == request.ResourceId, cancellationToken);

        var previous = existing?.Role;
        if (existing is null)
        {
            existing = new ResourceRoleAssignment
            {
                TenantId = user.TenantId.Value,
                UserId = request.UserId,
                ResourceType = normalized,
                ResourceId = request.ResourceId,
                Role = request.Role.Value,
            };
            db.ResourceRoleAssignments.Add(existing);
        }
        else if (existing.Role == request.Role.Value)
        {
            return Results.Ok(ToItem(existing, target));
        }
        else
        {
            existing.Role = request.Role.Value;
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync("ResourceAccess.Grant", nameof(ResourceRoleAssignment), existing.Id,
            $"user={request.UserId} {normalized}={request.ResourceId} role={request.Role.Value}"
            + (previous is null ? " (new)" : $" (was {previous})"),
            cancellationToken);

        return previous is null
            ? Results.Created($"/api/resource-access/{existing.Id}", ToItem(existing, target))
            : Results.Ok(ToItem(existing, target));
    }

    /// <summary>Removes one grant. The caller's own tenant-wide role is unaffected.</summary>
    public static async Task<IResult> RevokeAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IAuditService audit,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        var grant = await db.ResourceRoleAssignments.ForTenant(user)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (grant is null)
            return Results.NotFound();

        db.ResourceRoleAssignments.Remove(grant);
        await db.SaveChangesAsync(cancellationToken);
        await audit.LogAsync("ResourceAccess.Revoke", nameof(ResourceRoleAssignment), id,
            $"user={grant.UserId} {grant.ResourceType}={grant.ResourceId} role={grant.Role}",
            cancellationToken);

        return Results.NoContent();
    }

    /// <summary>Case-insensitive match onto the exact <see cref="ResourceType"/> constant, or null.</summary>
    private static string? NormalizeResourceType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return KnownResourceTypes.FirstOrDefault(t =>
            string.Equals(t, value.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static string UnknownResourceTypeMessage(string? value)
        => $"'{value}' is not a grantable resource type. Expected one of: {string.Join(", ", KnownResourceTypes)}.";

    private static ResourceAccessItem ToItem(ResourceRoleAssignment a, User target)
        => new(a.Id, a.UserId, target.Email, target.DisplayName, a.ResourceType, a.ResourceId, a.Role, a.CreatedAt);
}

public record GrantResourceAccessRequest(
    Guid UserId,
    string? ResourceType,
    Guid ResourceId,
    UserRole? Role = null);

public record ResourceAccessItem(
    Guid Id,
    Guid UserId,
    string? Email,
    string? DisplayName,
    string ResourceType,
    Guid ResourceId,
    UserRole Role,
    DateTimeOffset CreatedAt);
