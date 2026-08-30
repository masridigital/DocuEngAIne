using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

/// <summary>
/// Read-only client portal. Surfaces are company documents, expirations, and Keeper link
/// metadata. Every query is <c>ForTenant</c>. Companies must have <see cref="Company.PortalEnabled"/>.
/// There is no password vault and no Keeper reveal — <c>POST /api/keeper/{id}/reveal</c> stays
/// off this group.
/// </summary>
public static class PortalEndpoints
{
    public const string CompanyNotFoundMessage = "Company not found.";
    public const string DocumentNotFoundMessage = "Document not found.";

    public static readonly string[] Surfaces = ["documents", "expirations", "keeperLinks"];

    public static IEndpointRouteBuilder MapPortalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/portal").RequireAuthorization();

        group.MapGet("", () => Results.Ok(Describe()));
        group.MapGet("/companies", ListCompaniesAsync);
        group.MapGet("/companies/{companyId:guid}", GetCompanyAsync);
        group.MapGet("/companies/{companyId:guid}/documents", ListDocumentsAsync);
        group.MapGet("/companies/{companyId:guid}/documents/{id:guid}", GetDocumentAsync);
        group.MapGet("/companies/{companyId:guid}/expirations", ListExpirationsAsync);
        group.MapGet("/companies/{companyId:guid}/keeper-links", ListKeeperLinksAsync);

        return app;
    }

    public static object Describe() => new
    {
        readOnly = true,
        passwordVault = false,
        forTenant = true,
        surfaces = Surfaces,
        keeper = new { metadataOnly = true, reveal = false },
        notes = new[]
        {
            "Read-only client portal. Every query is ForTenant.",
            "Only companies with PortalEnabled appear. Other-tenant or disabled companies are not found.",
            "Keeper links return metadata only (title). Reveal is not a portal path and is not audit-logged here.",
            "No password vault. Keeper remains the vault.",
        },
    };

    public static async Task<IResult> ListCompaniesAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var items = await db.Companies.ForTenant(user).AsNoTracking()
            .Where(c => c.PortalEnabled)
            .OrderBy(c => c.Name)
            .Select(c => new PortalCompanyListItem(c.Id, c.Name, c.Slug, c.Website))
            .ToListAsync(cancellationToken);
        return Results.Ok(items);
    }

    public static async Task<IResult> GetCompanyAsync(
        Guid companyId,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var company = await FindPortalCompanyAsync(db, user, companyId, cancellationToken);
        if (company is null)
            return Results.NotFound(CompanyNotFoundMessage);

        var detail = await MapCompanyAsync(db, user, company, cancellationToken);
        return Results.Ok(detail);
    }

    public static async Task<IResult> ListDocumentsAsync(
        Guid companyId,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        if (await FindPortalCompanyAsync(db, user, companyId, cancellationToken) is null)
            return Results.NotFound(CompanyNotFoundMessage);

        var docs = await PublishedCompanyDocuments(db, user, companyId)
            .OrderBy(d => d.Title)
            .Select(d => new PortalDocumentListItem(d.Id, d.Title, d.Slug, d.Summary, d.Tags, d.UpdatedAt))
            .ToListAsync(cancellationToken);
        return Results.Ok(docs);
    }

    public static async Task<IResult> GetDocumentAsync(
        Guid companyId,
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        if (await FindPortalCompanyAsync(db, user, companyId, cancellationToken) is null)
            return Results.NotFound(CompanyNotFoundMessage);

        var doc = await PublishedCompanyDocuments(db, user, companyId)
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (doc is null)
            return Results.NotFound(DocumentNotFoundMessage);

        return Results.Ok(new PortalDocumentDetail(
            doc.Id, doc.Title, doc.Slug, doc.Summary, doc.Content, doc.Tags, doc.UpdatedAt));
    }

    public static async Task<IResult> ListExpirationsAsync(
        Guid companyId,
        [FromQuery] bool showExpired,
        [FromQuery] string? q,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        if (await FindPortalCompanyAsync(db, user, companyId, cancellationToken) is null)
            return Results.NotFound(CompanyNotFoundMessage);

        var items = await ExpirationEndpoints.QueryAsync(db, user, companyId, showExpired, q, cancellationToken);
        return Results.Ok(items);
    }

    public static async Task<IResult> ListKeeperLinksAsync(
        Guid companyId,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        if (await FindPortalCompanyAsync(db, user, companyId, cancellationToken) is null)
            return Results.NotFound(CompanyNotFoundMessage);

        // Metadata only. Title + timestamps + whether a Keeper record exists. Not a reveal:
        // no URL, UID, username hint, or notes, and no KeeperLink.Reveal audit row.
        var items = await db.KeeperLinks.ForTenant(user).AsNoTracking()
            .Where(k => k.CompanyId == companyId)
            .OrderBy(k => k.Name)
            .Select(k => new PortalKeeperLinkItem(
                k.Id,
                k.Name,
                k.CompanyId,
                k.UpdatedAt,
                k.KeeperRecordUrl != null && k.KeeperRecordUrl != ""))
            .ToListAsync(cancellationToken);
        return Results.Ok(items);
    }

    /// <summary>
    /// Other-tenant, unknown, or portal-disabled companies are not found. Never return a
    /// cross-tenant row.
    /// </summary>
    public static async Task<Company?> FindPortalCompanyAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        Guid companyId,
        CancellationToken cancellationToken = default)
    {
        return await db.Companies.ForTenant(user).AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == companyId && c.PortalEnabled, cancellationToken);
    }

    private static IQueryable<Document> PublishedCompanyDocuments(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        Guid companyId) =>
        db.Documents.ForTenant(user).AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.IsPublished);

    private static async Task<PortalCompanyDetail> MapCompanyAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        Company company,
        CancellationToken cancellationToken)
    {
        var documentCount = await PublishedCompanyDocuments(db, user, company.Id).CountAsync(cancellationToken);
        var keeperCount = await db.KeeperLinks.ForTenant(user).AsNoTracking()
            .CountAsync(k => k.CompanyId == company.Id, cancellationToken);
        var expirationCount = (await ExpirationEndpoints.QueryAsync(
            db, user, company.Id, showExpired: false, cancellationToken: cancellationToken)).Count;

        return new PortalCompanyDetail(
            company.Id,
            company.Name,
            company.Slug,
            company.Website,
            company.Phone,
            company.HoursOfOperation,
            new PortalCounts(documentCount, expirationCount, keeperCount));
    }
}

public sealed record PortalCompanyListItem(Guid Id, string Name, string Slug, string? Website);

public sealed record PortalCounts(int Documents, int Expirations, int KeeperLinks);

public sealed record PortalCompanyDetail(
    Guid Id,
    string Name,
    string Slug,
    string? Website,
    string? Phone,
    string? HoursOfOperation,
    PortalCounts Counts);

public sealed record PortalDocumentListItem(
    Guid Id,
    string Title,
    string? Slug,
    string? Summary,
    string? Tags,
    DateTimeOffset UpdatedAt);

public sealed record PortalDocumentDetail(
    Guid Id,
    string Title,
    string? Slug,
    string? Summary,
    string? Content,
    string? Tags,
    DateTimeOffset UpdatedAt);

public sealed record PortalKeeperLinkItem(
    Guid Id,
    string Title,
    Guid? CompanyId,
    DateTimeOffset UpdatedAt,
    bool HasRecordUrl);
