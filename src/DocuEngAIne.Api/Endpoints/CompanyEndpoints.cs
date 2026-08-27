using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

public static class CompanyEndpoints
{
    public const int RelatedTake = 8;

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
            return Results.Ok(items.Select(c => Map(c)));
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var company = await db.Companies.ForTenant(user).AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
            if (company is null)
                return Results.NotFound();

            var related = await LoadRelatedAsync(db, user, id, RelatedTake, cancellationToken);
            return Results.Ok(Map(company, related));
        });

        group.MapGet("/{id:guid}/summary", async (
            Guid id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var exists = await db.Companies.ForTenant(user).AsNoTracking()
                .AnyAsync(c => c.Id == id, cancellationToken);
            if (!exists)
                return Results.NotFound();

            var related = await LoadRelatedAsync(db, user, id, RelatedTake, cancellationToken);
            return Results.Ok(MapRelated(related));
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

    public static async Task<IResult?> EnsureCompanyInTenantAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        Guid? companyId,
        CancellationToken cancellationToken = default)
    {
        if (companyId is not Guid id)
            return null;

        var exists = await db.Companies.ForTenant(user).AnyAsync(c => c.Id == id, cancellationToken);
        return exists ? null : Results.BadRequest("Company not found.");
    }

    public static async Task<CompanyRelatedSnapshot> LoadRelatedAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        Guid companyId,
        int take = RelatedTake,
        CancellationToken cancellationToken = default)
    {
        var assetsQ = db.Assets.ForTenant(user).AsNoTracking().Where(a => a.CompanyId == companyId);
        var docsQ = db.Documents.ForTenant(user).AsNoTracking().Where(d => d.CompanyId == companyId);
        var runbooksQ = db.Runbooks.ForTenant(user).AsNoTracking().Where(r => r.CompanyId == companyId);
        var keeperQ = db.KeeperLinks.ForTenant(user).AsNoTracking().Where(k => k.CompanyId == companyId);

        return new CompanyRelatedSnapshot(
            await assetsQ.CountAsync(cancellationToken),
            await assetsQ.OrderByDescending(a => a.UpdatedAt).Take(take)
                .Select(a => new RelatedListItem(a.Id, a.Name, a.UpdatedAt)).ToListAsync(cancellationToken),
            await docsQ.CountAsync(cancellationToken),
            await docsQ.OrderByDescending(d => d.UpdatedAt).Take(take)
                .Select(d => new RelatedListItem(d.Id, d.Title, d.UpdatedAt)).ToListAsync(cancellationToken),
            await runbooksQ.CountAsync(cancellationToken),
            await runbooksQ.OrderByDescending(r => r.UpdatedAt).Take(take)
                .Select(r => new RelatedListItem(r.Id, r.Title, r.UpdatedAt)).ToListAsync(cancellationToken),
            await keeperQ.CountAsync(cancellationToken),
            await keeperQ.OrderByDescending(k => k.UpdatedAt).Take(take)
                .Select(k => new RelatedListItem(k.Id, k.Name, k.UpdatedAt)).ToListAsync(cancellationToken));
    }

    private static object Map(Company c, CompanyRelatedSnapshot? related = null)
    {
        return new
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
            Counts = related is null ? null : MapCounts(related),
            Assets = related?.Assets,
            Documents = related?.Documents,
            Runbooks = related?.Runbooks,
            KeeperLinks = related?.KeeperLinks,
        };
    }

    private static object MapRelated(CompanyRelatedSnapshot related) => new
    {
        Counts = MapCounts(related),
        related.Assets,
        related.Documents,
        related.Runbooks,
        related.KeeperLinks,
    };

    private static object MapCounts(CompanyRelatedSnapshot related) => new
    {
        Assets = related.AssetCount,
        Documents = related.DocumentCount,
        Runbooks = related.RunbookCount,
        KeeperLinks = related.KeeperLinkCount,
    };
}

public sealed record RelatedListItem(Guid Id, string Name, DateTimeOffset UpdatedAt);

public sealed record CompanyRelatedSnapshot(
    int AssetCount,
    IReadOnlyList<RelatedListItem> Assets,
    int DocumentCount,
    IReadOnlyList<RelatedListItem> Documents,
    int RunbookCount,
    IReadOnlyList<RelatedListItem> Runbooks,
    int KeeperLinkCount,
    IReadOnlyList<RelatedListItem> KeeperLinks);

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
