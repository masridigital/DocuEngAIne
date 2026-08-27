using DocuEngAIne.Core.Entities;
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
            await db.SaveChangesAsync(cancellationToken);

            return Results.Created($"/api/tenant/me", new { tenant.Id, tenant.Name, tenant.Slug });
        });

        return app;
    }
}

public record OnboardTenantRequest(string Name, string Slug, string? PrimaryDomain);
