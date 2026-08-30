using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

public static class TenantEndpoints
{
    public const string TenantNotOnboardedMessage = "Tenant has not been onboarded yet.";
    public const string TenantAlreadyOnboardedMessage = "Tenant already onboarded.";
    public const string OwnerAlreadyExistsMessage = "Tenant already has an Owner.";
    public const string ObjectIdRequiredMessage = "An authenticated Entra user is required.";

    public static IEndpointRouteBuilder MapTenantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tenant").RequireAuthorization();

        group.MapGet("/me", async (
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            if (user.TenantId is null)
                return Results.Unauthorized();

            var tenant = await db.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == user.TenantId.Value, cancellationToken);

            if (tenant is null)
            {
                return Results.Ok(new
                {
                    Id = user.TenantId.Value,
                    user.Email,
                    Message = TenantNotOnboardedMessage,
                });
            }

            return Results.Ok(new
            {
                tenant.Id,
                tenant.Name,
                tenant.Slug,
                tenant.PrimaryDomain,
                tenant.IsActive,
            });
        });

        group.MapPost("/onboard", async (
            [FromBody] OnboardTenantRequest request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            if (user.TenantId is null)
                return Results.Unauthorized();

            if (await db.Tenants.AnyAsync(t => t.Id == user.TenantId.Value, cancellationToken))
                return Results.Conflict(TenantAlreadyOnboardedMessage);

            var tenant = new Tenant
            {
                Id = user.TenantId.Value,
                Name = request.Name,
                Slug = request.Slug,
                PrimaryDomain = request.PrimaryDomain,
            };

            db.Tenants.Add(tenant);

            // The person who onboards the tenant becomes its Owner, in the same save as the tenant
            // itself. Entra app roles are an optional setup step, so without a deliberate grant here
            // a new tenant could hold no administrator at all. Doing it on the onboarding call
            // rather than on first GET /api/me also removes a check-then-act race in which whichever
            // tenant member's browser happened to revalidate first became the sole permanent Owner.
            // Tenants created before this grant recover through POST /api/tenant/claim-owner.
            if (!string.IsNullOrWhiteSpace(user.ObjectId)
                && !await db.Users.AnyAsync(u => u.TenantId == tenant.Id && u.EntraObjectId == user.ObjectId, cancellationToken))
            {
                db.Users.Add(new User
                {
                    TenantId = tenant.Id,
                    EntraObjectId = user.ObjectId!,
                    Email = user.Email ?? "unknown",
                    DisplayName = user.DisplayName,
                    Role = UserRole.Owner,
                });
            }

            await db.SaveChangesAsync(cancellationToken);

            return Results.Created($"/api/tenant/me", new { tenant.Id, tenant.Name, tenant.Slug });
        });

        // Recovery for tenants created before onboard granted Owner. Not behind AdminPolicy: that
        // gate is exactly what an ownerless tenant cannot satisfy. The body itself is the gate —
        // zero active Owner/Admin rows, first authenticated caller wins, everyone after is 409.
        group.MapPost("/claim-owner", async (
            DocuEngAIneDbContext db,
            ICurrentUser user,
            IAuditService audit,
            CancellationToken cancellationToken) =>
            await ClaimOwnerAsync(db, user, audit, cancellationToken));

        return app;
    }

    /// <summary>
    /// Grants <see cref="UserRole.Owner"/> to the caller when the tenant has no active Owner or
    /// Admin. A second caller cannot steal the role.
    /// </summary>
    /// <remarks>
    /// Tenants created before <c>POST /api/tenant/onboard</c> started writing Owner have User rows
    /// (or none) and no administrator. <c>PUT /api/users/{id}/role</c> cannot repair that: it sits
    /// behind the same admin policy it would need to escape. This route is the in-app backfill.
    /// <para>
    /// Active Owner <em>or</em> Admin rows block the claim. An Admin can already appoint an Owner
    /// through the users API, so letting a Reader jump over them would be a steal. Inactive
    /// Owner/Admin rows do not count — they fail the admin policy and are not a set of keys.
    /// </para>
    /// <para>
    /// The Owner/Admin check is a check-then-act, so two concurrent claims can in principle both
    /// observe an empty tenant and both succeed. Role changes are rare and this recovery runs once
    /// per pre-grant tenant; a unique-Owner constraint would be a schema change, which this pass
    /// deliberately does not make.
    /// </para>
    /// </remarks>
    public static async Task<IResult> ClaimOwnerAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IAuditService audit,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(user.ObjectId))
            return Results.BadRequest(ObjectIdRequiredMessage);

        var tenantExists = await db.Tenants.AnyAsync(t => t.Id == user.TenantId.Value, cancellationToken);
        if (!tenantExists)
            return Results.NotFound(TenantNotOnboardedMessage);

        // ForTenant so another tenant's Owner/Admin cannot block (or license) this claim.
        var hasAdministrator = await db.Users.ForTenant(user)
            .AnyAsync(u => u.IsActive && u.Role >= UserRole.Admin, cancellationToken);
        if (hasAdministrator)
            return Results.Conflict(OwnerAlreadyExistsMessage);

        var objectId = user.ObjectId;
        var target = await db.Users.ForTenant(user)
            .FirstOrDefaultAsync(u => u.EntraObjectId == objectId, cancellationToken);

        UserRole? previousRole = target?.Role;
        if (target is null)
        {
            target = new User
            {
                TenantId = user.TenantId.Value,
                EntraObjectId = objectId,
                Email = user.Email ?? "unknown",
                DisplayName = user.DisplayName,
                Role = UserRole.Owner,
                IsActive = true,
            };
            db.Users.Add(target);
        }
        else
        {
            target.Role = UserRole.Owner;
            target.IsActive = true;
        }

        await db.SaveChangesAsync(cancellationToken);

        await audit.LogAsync(
            "User.ClaimOwner",
            nameof(User),
            target.Id,
            previousRole is UserRole prior
                ? $"Owner claimed (was {prior}) by {user.Email ?? objectId}"
                : $"Owner claimed (new user) by {user.Email ?? objectId}",
            cancellationToken);

        return Results.Ok(new
        {
            target.Id,
            target.EntraObjectId,
            target.Email,
            target.Role,
        });
    }
}

public record OnboardTenantRequest(string Name, string Slug, string? PrimaryDomain);
