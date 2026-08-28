using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

/// <summary>
/// Tenant user administration: who is provisioned in the tenant, and what tenant-wide
/// <see cref="UserRole"/> each of them holds.
/// </summary>
/// <remarks>
/// This group exists because until now nothing in the system could write <see cref="User.Role"/>
/// after the row was created. <c>POST /api/tenant/onboard</c> grants the onboarding caller
/// <see cref="UserRole.Owner"/> and every later sign-in provisions a <see cref="UserRole.Reader"/>,
/// so a tenant had exactly one administrator forever and no in-app recovery if that person left.
/// The whole point of the endpoint is therefore to make the admin gate *usable* without ever making
/// it *empty*, which is why <see cref="SetRoleAsync"/> reads more like a set of refusals than an
/// update handler.
/// </remarks>
public static class UserEndpoints
{
    public const string RoleRequiredMessage = "Role is required.";
    public const string UnknownRoleMessage = "Unknown role.";
    public const string LastOwnerMessage = "Cannot change the role of the tenant's last Owner. Grant Owner to another active user first.";
    public const string OwnerRoleRequiresOwnerMessage = "Only an Owner can grant or revoke the Owner role.";

    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        // Admin-only as a group, the listing included: the roster is the map of who can administer
        // the tenant, which is exactly the reconnaissance a lower-privileged account would want.
        var group = app.MapGroup("/api/users").RequireAuthorization(AuthExtensions.AdminPolicy);

        group.MapGet("", async (
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            if (user.TenantId is null)
                return Results.Unauthorized();

            return Results.Ok(await ListAsync(db, user, cancellationToken));
        });

        group.MapPut("/{id:guid}/role", async (
            Guid id,
            [FromBody] UpdateUserRoleRequest? request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            IAuditService audit,
            CancellationToken cancellationToken) =>
            await SetRoleAsync(id, request, db, user, audit, cancellationToken));

        return app;
    }

    public static async Task<IReadOnlyList<TenantUserItem>> ListAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var users = await db.Users.ForTenant(user).AsNoTracking()
            .OrderByDescending(u => u.Role)
            .ThenBy(u => u.Email)
            .ToListAsync(cancellationToken);
        return users.Select(Map).ToList();
    }

    /// <summary>
    /// Sets one user's tenant-wide role.
    /// </summary>
    /// <remarks>
    /// Four refusals guard this, in the order they are checked:
    /// <list type="number">
    /// <item>An undefined enum value is rejected rather than persisted, so a typo or a raw numeric
    /// body cannot leave a row holding a role nothing in the system understands.</item>
    /// <item>The target is resolved through <c>ForTenant</c>, so another tenant's user is a 404 and
    /// is never mutated — indistinguishable from a row that does not exist at all.</item>
    /// <item>The tenant's last active Owner cannot be moved off Owner, by anyone, including
    /// themselves. This is the lockout the endpoint exists to prevent: strip that role and the
    /// tenant has no way back to an Owner from inside the application.</item>
    /// <item>Granting or revoking Owner requires the caller to already be an Owner, *unless* the
    /// tenant currently has no active Owner at all.</item>
    /// </list>
    /// On the fourth point: an <see cref="UserRole.Admin"/> is deliberately not allowed to hand out
    /// <see cref="UserRole.Owner"/> — that is a grant of a level above their own, and letting an
    /// Admin mint Owners (or demote existing ones) would make the Admin/Owner distinction decorative.
    /// The exception matters just as much: because "only an Owner may grant Owner" is otherwise a
    /// closed loop, a tenant whose only Owner is deleted out-of-band in Entra could never have one
    /// again. When zero active Owners remain, any admin who already passes
    /// <see cref="AuthExtensions.AdminPolicy"/> may appoint one. That escape hatch cannot be forced
    /// open from inside the app — refusal (3) is what keeps the active-Owner count from reaching
    /// zero in the first place.
    /// <para>
    /// The last-Owner guard is a check-then-act on separate statements, so it is racy in principle:
    /// two concurrent demotions of two different Owners can each observe the other as the surviving
    /// Owner and both succeed, leaving none. Closing that would need a serializable transaction or a
    /// concurrency token on the row, and neither is worth it here — role changes are rare, manual,
    /// and performed by a handful of admins, and the recovery hatch above is precisely what makes an
    /// ownerless tenant repairable rather than terminal.
    /// </para>
    /// </remarks>
    public static async Task<IResult> SetRoleAsync(
        Guid id,
        UpdateUserRoleRequest? request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IAuditService audit,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        // Nullable and explicitly required: an empty body must not bind to default(UserRole), which
        // is UserRole.None and would silently strip the target of all access.
        if (request?.Role is not UserRole newRole)
            return Results.BadRequest(RoleRequiredMessage);

        // JsonStringEnumConverter also accepts raw numbers, so an out-of-range value can reach here.
        // UserRole.None is intentionally allowed through: it is a defined value meaning "no access",
        // and as the lowest role it is a demotion like any other, covered by the last-Owner guard.
        if (!Enum.IsDefined(newRole))
            return Results.BadRequest(UnknownRoleMessage);

        var target = await db.Users.ForTenant(user).FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (target is null)
            return Results.NotFound();

        var previousRole = target.Role;
        if (previousRole == newRole)
            return Results.NoContent();

        // Only *active* Owners count. An inactive user fails the admin policy, so an Owner row that
        // cannot sign in is not a tenant administrator and must not be mistaken for the one holding
        // the tenant's last set of keys.
        var targetId = target.Id;
        var otherActiveOwners = await db.Users.ForTenant(user)
            .CountAsync(u => u.Id != targetId && u.Role == UserRole.Owner && u.IsActive, cancellationToken);

        // Owner is the top of the enum, so any change away from Owner is a demotion.
        if (previousRole == UserRole.Owner && otherActiveOwners == 0)
            return Results.BadRequest(LastOwnerMessage);

        // The target is not an Owner here (the guard above returned), so otherActiveOwners is the
        // tenant's full count of active Owners — zero means the recovery case described above.
        if ((previousRole == UserRole.Owner || newRole == UserRole.Owner)
            && otherActiveOwners > 0
            && await ResolveCallerRoleAsync(db, user, cancellationToken) < UserRole.Owner)
        {
            return Results.Json(OwnerRoleRequiresOwnerMessage, statusCode: StatusCodes.Status403Forbidden);
        }

        target.Role = newRole;
        await db.SaveChangesAsync(cancellationToken);

        // Audited after the save so the trail only ever records changes that actually landed. The
        // AuditService stamps the acting user and IP itself; the old and new role go in the details
        // because a role change is only reviewable if you can see what it moved *from*.
        await audit.LogAsync(
            "User.ChangeRole",
            nameof(User),
            target.Id,
            $"Role changed from {previousRole} to {newRole} by {user.Email ?? user.ObjectId ?? "unknown"}",
            cancellationToken);

        return Results.NoContent();
    }

    /// <summary>
    /// The caller's effective tenant role, taken as the higher of the two signals that
    /// <see cref="AuthExtensions.AdminPolicy"/> itself accepts: an Entra app role, or the stored
    /// <see cref="User.Role"/> row.
    /// </summary>
    /// <remarks>
    /// Both have to be consulted for the same reason the policy consults both — Entra app roles are
    /// an optional setup step, so a tenant that never defined them has only the row; and an Entra
    /// Owner who has not yet hit <c>GET /api/me</c> has only the claim.
    /// </remarks>
    private static async Task<UserRole> ResolveCallerRoleAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var claimRole = user.HasRole(UserRole.Owner)
            ? UserRole.Owner
            : user.HasRole(UserRole.Admin)
                ? UserRole.Admin
                : UserRole.None;

        if (claimRole == UserRole.Owner || string.IsNullOrEmpty(user.ObjectId))
            return claimRole;

        var objectId = user.ObjectId;
        var row = await db.Users.ForTenant(user).AsNoTracking()
            .FirstOrDefaultAsync(u => u.EntraObjectId == objectId, cancellationToken);

        var rowRole = row is { IsActive: true } ? row.Role : UserRole.None;
        return rowRole > claimRole ? rowRole : claimRole;
    }

    private static TenantUserItem Map(User u) =>
        new(u.Id, u.EntraObjectId, u.Email, u.DisplayName, u.Role, u.IsActive, u.LastSeenAt);
}

public sealed record TenantUserItem(
    Guid Id,
    string EntraObjectId,
    string Email,
    string? DisplayName,
    UserRole Role,
    bool IsActive,
    DateTimeOffset? LastSeenAt);

/// <summary>
/// Body of <c>PUT /api/users/{id}/role</c>. Enums travel as strings on this API
/// (<c>JsonStringEnumConverter</c>), so the wire form is <c>{ "role": "Contributor" }</c>.
/// </summary>
public record UpdateUserRoleRequest(UserRole? Role = null);
