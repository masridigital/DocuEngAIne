using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

public static class CompanyEndpoints
{
    public static IEndpointRouteBuilder MapCompanyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/companies").RequireAuthorization();

        group.MapGet("", async (
            [FromQuery] string? q,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var query = db.Companies.ForTenant(user).AsNoTracking();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                query = query.Where(c =>
                    c.Name.Contains(term)
                    || c.Slug.Contains(term)
                    || (c.HaloClientId != null && c.HaloClientId.Contains(term))
                    || (c.NinjaOrganizationId != null && c.NinjaOrganizationId.Contains(term)));
            }

            var items = await query.OrderBy(c => c.Name).ToListAsync(cancellationToken);
            return Results.Ok(items.Select(Map));
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var company = await db.Companies.ForTenant(user).AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
            return company is null ? Results.NotFound() : Results.Ok(Map(company));
        });

        group.MapPost("", async (
            [FromBody] CreateCompanyRequest request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            if (user.TenantId is null)
                return Results.Unauthorized();

            if (await db.Companies.ForTenant(user).AnyAsync(c => c.Slug == request.Slug, cancellationToken))
                return Results.Conflict("Slug already exists.");

            var company = new Company
            {
                TenantId = user.TenantId.Value,
                Name = request.Name,
                Slug = request.Slug,
                CompanyNumber = request.CompanyNumber,
                PrimaryDomain = request.PrimaryDomain,
                Address = request.Address,
                City = request.City,
                State = request.State,
                Phone = request.Phone,
                Website = request.Website,
                Notes = request.Notes,
                HoursOfOperation = request.HoursOfOperation,
                HaloClientId = request.HaloClientId,
                NinjaOrganizationId = request.NinjaOrganizationId,
                IsActive = request.IsActive ?? true,
            };

            db.Companies.Add(company);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/companies/{company.Id}", Map(company));
        });

        group.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] UpdateCompanyRequest request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var company = await db.Companies.ForTenant(user).FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
            if (company is null)
                return Results.NotFound();

            company.Name = request.Name ?? company.Name;
            company.Slug = request.Slug ?? company.Slug;
            company.CompanyNumber = request.CompanyNumber ?? company.CompanyNumber;
            company.PrimaryDomain = request.PrimaryDomain ?? company.PrimaryDomain;
            company.Address = request.Address ?? company.Address;
            company.City = request.City ?? company.City;
            company.State = request.State ?? company.State;
            company.Phone = request.Phone ?? company.Phone;
            company.Website = request.Website ?? company.Website;
            company.Notes = request.Notes ?? company.Notes;
            company.HoursOfOperation = request.HoursOfOperation ?? company.HoursOfOperation;
            company.HaloClientId = request.HaloClientId ?? company.HaloClientId;
            company.NinjaOrganizationId = request.NinjaOrganizationId ?? company.NinjaOrganizationId;
            if (request.IsActive.HasValue)
                company.IsActive = request.IsActive.Value;

            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        group.MapDelete("/{id:guid}", async (
            Guid id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var company = await db.Companies.ForTenant(user).FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
            if (company is null)
                return Results.NotFound();

            db.Companies.Remove(company);
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        return app;
    }

    private static object Map(Company c) => new
    {
        c.Id,
        c.Name,
        c.Slug,
        c.CompanyNumber,
        c.PrimaryDomain,
        c.Address,
        c.City,
        c.State,
        c.Phone,
        c.Website,
        c.Notes,
        c.HoursOfOperation,
        c.IsActive,
        c.PortalEnabled,
        c.HaloClientId,
        c.NinjaOrganizationId,
        c.CreatedAt,
        c.UpdatedAt,
    };
}

public record CreateCompanyRequest(
    string Name,
    string Slug,
    string? CompanyNumber = null,
    string? PrimaryDomain = null,
    string? Address = null,
    string? City = null,
    string? State = null,
    string? Phone = null,
    string? Website = null,
    string? Notes = null,
    string? HoursOfOperation = null,
    string? HaloClientId = null,
    string? NinjaOrganizationId = null,
    bool? IsActive = null);

public record UpdateCompanyRequest(
    string? Name = null,
    string? Slug = null,
    string? CompanyNumber = null,
    string? PrimaryDomain = null,
    string? Address = null,
    string? City = null,
    string? State = null,
    string? Phone = null,
    string? Website = null,
    string? Notes = null,
    string? HoursOfOperation = null,
    string? HaloClientId = null,
    string? NinjaOrganizationId = null,
    bool? IsActive = null);
