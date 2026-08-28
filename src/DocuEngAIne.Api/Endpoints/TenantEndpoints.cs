using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

public static class TenantEndpoints
{
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
                    Message = "Tenant has not been onboarded yet.",
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
                return Results.Conflict("Tenant already onboarded.");

            var tenant = new Tenant
            {
                Id = user.TenantId.Value,
                Name = request.Name,
                Slug = request.Slug,
                PrimaryDomain = request.PrimaryDomain,
            };

            db.Tenants.Add(tenant);

            // The person who onboards the tenant becomes its Owner, in the same save as the tenant
            // itself. Nothing else in the system writes User.Role and Entra app roles are an optional
            // setup step, so without a deliberate grant here a tenant could hold no administrator at
            // all. Doing it on the onboarding call rather than on first GET /api/me also removes a
            // check-then-act race in which whichever tenant member's browser happened to revalidate
            // first became the sole permanent Owner.
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

        return app;
    }
}

public record OnboardTenantRequest(string Name, string Slug, string? PrimaryDomain);
