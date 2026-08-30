using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

public static class CompanyEndpoints
{
    public const int RelatedTake = 8;
    public const string ParentCompanyNotFoundMessage = "Parent company not found.";
    public const string CompanyCannotBeOwnParentMessage = "Company cannot be its own parent.";

    public static IEndpointRouteBuilder MapCompanyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/companies").RequireAuthorization();

        group.MapGet("", async (
            [FromQuery] string? q,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
            await ListAsync(q, db, user, cancellationToken));

        group.MapGet("/{id:guid}", async (
            Guid id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
            await GetAsync(id, db, user, cancellationToken));

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
            await CreateAsync(request, db, user, cancellationToken));

        group.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] UpdateCompanyRequest request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
            await UpdateAsync(id, request, db, user, cancellationToken));

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

    public static async Task<IResult> ListAsync(
        string? q,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
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
    }

    public static async Task<IResult> GetAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var company = await db.Companies.ForTenant(user).AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (company is null)
            return Results.NotFound();

        var related = await LoadRelatedAsync(db, user, id, RelatedTake, cancellationToken);
        return Results.Ok(Map(company, related));
    }

    public static async Task<IResult> CreateAsync(
        CreateCompanyRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        if (await db.Companies.ForTenant(user).AnyAsync(c => c.Slug == request.Slug, cancellationToken))
            return Results.Conflict("Slug already exists.");

        if (await EnsureParentCompanyInTenantAsync(db, user, request.ParentCompanyId, cancellationToken: cancellationToken) is { } badParent)
            return badParent;

        var company = new Company
        {
            TenantId = user.TenantId.Value,
            Name = request.Name,
            Slug = request.Slug,
            CompanyNumber = request.CompanyNumber,
            CompanyType = request.CompanyType,
            Nickname = request.Nickname,
            ParentCompanyId = request.ParentCompanyId,
            PrimaryDomain = request.PrimaryDomain,
            Address = request.Address,
            City = request.City,
            State = request.State,
            Country = request.Country,
            PostalCode = request.PostalCode,
            Phone = request.Phone,
            Fax = request.Fax,
            Website = request.Website,
            Notes = request.Notes,
            HoursOfOperation = request.HoursOfOperation,
            HaloClientId = NullIfEmpty(request.HaloClientId),
            NinjaOrganizationId = NullIfEmpty(request.NinjaOrganizationId),
            HaloPortalUrl = NullIfEmpty(request.HaloPortalUrl),
            NinjaPortalUrl = NullIfEmpty(request.NinjaPortalUrl),
            IsActive = request.IsActive ?? true,
        };

        db.Companies.Add(company);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/companies/{company.Id}", Map(company));
    }

    public static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateCompanyRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var company = await db.Companies.ForTenant(user).FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (company is null)
            return Results.NotFound();

        if (await EnsureParentCompanyInTenantAsync(db, user, request.ParentCompanyId, excludeId: id, cancellationToken) is { } badParent)
            return badParent;

        company.Name = request.Name ?? company.Name;
        company.Slug = request.Slug ?? company.Slug;
        company.CompanyNumber = request.CompanyNumber ?? company.CompanyNumber;
        company.CompanyType = request.CompanyType ?? company.CompanyType;
        company.Nickname = request.Nickname ?? company.Nickname;
        if (request.ParentCompanyId.HasValue)
            company.ParentCompanyId = request.ParentCompanyId.Value;
        company.PrimaryDomain = request.PrimaryDomain ?? company.PrimaryDomain;
        company.Address = request.Address ?? company.Address;
        company.City = request.City ?? company.City;
        company.State = request.State ?? company.State;
        company.Country = request.Country ?? company.Country;
        company.PostalCode = request.PostalCode ?? company.PostalCode;
        company.Phone = request.Phone ?? company.Phone;
        company.Fax = request.Fax ?? company.Fax;
        company.Website = request.Website ?? company.Website;
        company.Notes = request.Notes ?? company.Notes;
        company.HoursOfOperation = request.HoursOfOperation ?? company.HoursOfOperation;
        company.HaloClientId = request.HaloClientId ?? company.HaloClientId;
        company.NinjaOrganizationId = request.NinjaOrganizationId ?? company.NinjaOrganizationId;
        if (request.HaloPortalUrl is not null)
            company.HaloPortalUrl = NullIfEmpty(request.HaloPortalUrl);
        if (request.NinjaPortalUrl is not null)
            company.NinjaPortalUrl = NullIfEmpty(request.NinjaPortalUrl);
        if (request.IsActive.HasValue)
            company.IsActive = request.IsActive.Value;

        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
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

    /// <summary>
    /// Applies a company attachment on update. <paramref name="companyId"/> <c>null</c> with
    /// <paramref name="companyIdClear"/> false leaves the stored value unchanged. Detach with
    /// <paramref name="companyIdClear"/> true or the empty GUID sentinel. A real id is validated
    /// through <see cref="EnsureCompanyInTenantAsync"/> (other-tenant → 400).
    /// </summary>
    public static async Task<IResult?> ApplyCompanyIdOnUpdateAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        Guid? companyId,
        bool companyIdClear,
        Action<Guid?> assign,
        CancellationToken cancellationToken = default)
    {
        if (companyIdClear || companyId == Guid.Empty)
        {
            assign(null);
            return null;
        }

        if (companyId is not Guid id)
            return null;

        if (await EnsureCompanyInTenantAsync(db, user, id, cancellationToken) is { } badCompany)
            return badCompany;

        assign(id);
        return null;
    }

    public static async Task<IResult?> EnsureParentCompanyInTenantAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        Guid? parentCompanyId,
        Guid? excludeId = null,
        CancellationToken cancellationToken = default)
    {
        if (parentCompanyId is not Guid id)
            return null;

        if (excludeId is Guid self && self == id)
            return Results.BadRequest(CompanyCannotBeOwnParentMessage);

        var exists = await db.Companies.ForTenant(user).AnyAsync(c => c.Id == id, cancellationToken);
        return exists ? null : Results.BadRequest(ParentCompanyNotFoundMessage);
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

        var relatedLinks = await LinkEndpoints.LoadRelatedForEntityAsync(
            db, user, LinkEntityType.Company, companyId, take, cancellationToken);

        return new CompanyRelatedSnapshot(
            await assetsQ.CountAsync(cancellationToken),
            await assetsQ.OrderByDescending(a => a.UpdatedAt).Take(take)
                .Select(a => new RelatedListItem(a.Id, a.Name, a.UpdatedAt)).ToListAsync(cancellationToken),
            await docsQ.CountAsync(cancellationToken),
            await docsQ.OrderByDescending(d => d.UpdatedAt).Take(take)
                .Select(d => new RelatedListItem(d.Id, d.Title, d.UpdatedAt)).ToListAsync(cancellationToken),
            await runbooksQ.CountAsync(cancellationToken),
            await runbooksQ.OrderByDescending(r => r.UpdatedAt).Take(take)
                .Select(r => new RelatedListItem(r.Id, r.Title, r.UpdatedAt, r.Runs.Count())).ToListAsync(cancellationToken),
            await keeperQ.CountAsync(cancellationToken),
            await keeperQ.OrderByDescending(k => k.UpdatedAt).Take(take)
                .Select(k => new RelatedListItem(k.Id, k.Name, k.UpdatedAt)).ToListAsync(cancellationToken),
            relatedLinks.Count,
            relatedLinks.Items);
    }

    private static object Map(Company c, CompanyRelatedSnapshot? related = null)
    {
        return new
        {
            c.Id,
            c.Name,
            c.Slug,
            c.CompanyNumber,
            c.CompanyType,
            c.Nickname,
            c.ParentCompanyId,
            c.PrimaryDomain,
            c.Address,
            c.City,
            c.State,
            c.Country,
            c.PostalCode,
            c.Phone,
            c.Fax,
            c.Website,
            c.Notes,
            c.HoursOfOperation,
            c.IsActive,
            c.PortalEnabled,
            c.HaloClientId,
            c.NinjaOrganizationId,
            c.HaloPortalUrl,
            c.NinjaPortalUrl,
            c.CreatedAt,
            c.UpdatedAt,
            Counts = related is null ? null : MapCounts(related),
            Assets = related?.Assets,
            Documents = related?.Documents,
            Runbooks = related?.Runbooks,
            KeeperLinks = related?.KeeperLinks,
            RelatedLinks = related?.RelatedLinks,
        };
    }

    private static object MapRelated(CompanyRelatedSnapshot related) => new
    {
        Counts = MapCounts(related),
        related.Assets,
        related.Documents,
        related.Runbooks,
        related.KeeperLinks,
        related.RelatedLinks,
    };

    private static object MapCounts(CompanyRelatedSnapshot related) => new
    {
        Assets = related.AssetCount,
        Documents = related.DocumentCount,
        Runbooks = related.RunbookCount,
        KeeperLinks = related.KeeperLinkCount,
        RelatedLinks = related.RelatedLinkCount,
    };

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record RelatedListItem(Guid Id, string Name, DateTimeOffset UpdatedAt, int? RunCount = null);

public sealed record CompanyRelatedSnapshot(
    int AssetCount,
    IReadOnlyList<RelatedListItem> Assets,
    int DocumentCount,
    IReadOnlyList<RelatedListItem> Documents,
    int RunbookCount,
    IReadOnlyList<RelatedListItem> Runbooks,
    int KeeperLinkCount,
    IReadOnlyList<RelatedListItem> KeeperLinks,
    int RelatedLinkCount,
    IReadOnlyList<RelatedLinkListItem> RelatedLinks);

public record CreateCompanyRequest(
    string Name,
    string Slug,
    string? CompanyNumber = null,
    string? CompanyType = null,
    string? Nickname = null,
    Guid? ParentCompanyId = null,
    string? PrimaryDomain = null,
    string? Address = null,
    string? City = null,
    string? State = null,
    string? Country = null,
    string? PostalCode = null,
    string? Phone = null,
    string? Fax = null,
    string? Website = null,
    string? Notes = null,
    string? HoursOfOperation = null,
    string? HaloClientId = null,
    string? NinjaOrganizationId = null,
    string? HaloPortalUrl = null,
    string? NinjaPortalUrl = null,
    bool? IsActive = null);

public record UpdateCompanyRequest(
    string? Name = null,
    string? Slug = null,
    string? CompanyNumber = null,
    string? CompanyType = null,
    string? Nickname = null,
    Guid? ParentCompanyId = null,
    string? PrimaryDomain = null,
    string? Address = null,
    string? City = null,
    string? State = null,
    string? Country = null,
    string? PostalCode = null,
    string? Phone = null,
    string? Fax = null,
    string? Website = null,
    string? Notes = null,
    string? HoursOfOperation = null,
    string? HaloClientId = null,
    string? NinjaOrganizationId = null,
    string? HaloPortalUrl = null,
    string? NinjaPortalUrl = null,
    bool? IsActive = null);
