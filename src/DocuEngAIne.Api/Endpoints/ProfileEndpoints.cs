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

                dbUser = new User
                {
                    TenantId = user.TenantId.Value,
                    EntraObjectId = user.ObjectId!,
                    Email = user.Email ?? "unknown",
                    DisplayName = user.DisplayName,
                    // Signing in never confers admin rights. The tenant's first Owner is granted
                    // deliberately by POST /api/tenant/onboard; bootstrapping here instead made the
                    // grant a race won by whichever member's browser revalidated first.
                    Role = UserRole.Reader,
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
