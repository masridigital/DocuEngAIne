using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

public static class ProfileEndpoints
{
    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").RequireAuthorization();

        group.MapGet("/me", async (
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            if (user.TenantId is null)
                return Results.Unauthorized();

            var dbUser = await db.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.TenantId == user.TenantId.Value && u.EntraObjectId == user.ObjectId, cancellationToken);

            if (dbUser is null)
            {
                // Auto-provision user on first login if tenant exists.
                var tenantExists = await db.Tenants.AnyAsync(t => t.Id == user.TenantId.Value, cancellationToken);
                if (!tenantExists)
                {
                    return Results.Ok(new
                    {
                        user.ObjectId,
                        user.Email,
                        user.DisplayName,
                        user.TenantId,
                        OnboardingRequired = true,
                    });
                }

                // Bootstrap: the first person to sign in to a freshly onboarded tenant becomes Owner.
                // Without this the tenant would have no administrator at all — nothing else in the
                // system ever writes User.Role, and Entra app roles are an optional setup step, so a
                // Reader-only tenant could never reach the admin-gated integration surfaces.
                var isFirstUserInTenant = !await db.Users
                    .AnyAsync(u => u.TenantId == user.TenantId.Value, cancellationToken);

                dbUser = new User
                {
                    TenantId = user.TenantId.Value,
                    EntraObjectId = user.ObjectId!,
                    Email = user.Email ?? "unknown",
                    DisplayName = user.DisplayName,
                    // Everyone after the first starts read-only: admin rights are granted deliberately,
                    // not handed out by the act of signing in.
                    Role = isFirstUserInTenant ? UserRole.Owner : UserRole.Reader,
                };

                db.Users.Add(dbUser);
                await db.SaveChangesAsync(cancellationToken);
            }

            dbUser.LastSeenAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            var tenant = await db.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == user.TenantId.Value, cancellationToken);

            return Results.Ok(new
            {
                dbUser.Id,
                dbUser.EntraObjectId,
                dbUser.Email,
                dbUser.DisplayName,
                dbUser.Role,
                dbUser.LastSeenAt,
                Tenant = tenant is null ? null : new { tenant.Id, tenant.Name, tenant.Slug, tenant.PrimaryDomain },
            });
        });

        group.MapGet("/tenant/settings", async (
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            if (user.TenantId is null)
                return Results.Unauthorized();

            var tenant = await db.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == user.TenantId.Value, cancellationToken);

            return tenant is null ? Results.NotFound() : Results.Ok(new
            {
                tenant.Id,
                tenant.Name,
                tenant.Slug,
                tenant.PrimaryDomain,
                tenant.IsActive,
                tenant.CreatedAt,
            });
        });

        return app;
    }
}
