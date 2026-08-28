using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

public static class DocumentEndpoints
{
    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/documents").RequireAuthorization();

        group.MapGet("", async (
            [FromQuery] string? search,
            [FromQuery] Guid? folderId,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var docs = await ListAsync(db, user, search, folderId, cancellationToken);
            return Results.Ok(docs);
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var doc = await db.Documents
                .ForTenant(user)
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

            return doc is null ? Results.NotFound() : Results.Ok(new
            {
                doc.Id,
                doc.Title,
                doc.Slug,
                doc.Summary,
                doc.Content,
                doc.Tags,
                doc.CompanyId,
                doc.FolderId,
                doc.IsPublished,
                doc.UpdatedAt,
            });
        });

        group.MapPost("", async (
            [FromBody] CreateDocumentRequest request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            IResourceAuthorizationService authorization,
            CancellationToken cancellationToken) =>
        {
            // The document does not exist yet, so no grant can name it: creation gates on the
            // tenant-wide role.
            if (await ResourceWriteGuard.RequireTenantWriteAsync(authorization, user, ResourceType.Document, cancellationToken) is { } denied)
                return denied;

            return await CreateAsync(request, db, user, cancellationToken);
        });

        group.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] UpdateDocumentRequest request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            IResourceAuthorizationService authorization,
            CancellationToken cancellationToken) =>
        {
            if (await ResourceWriteGuard.RequireWriteAsync(authorization, user, id, ResourceType.Document, cancellationToken) is { } denied)
                return denied;

            return await UpdateAsync(id, request, db, user, cancellationToken);
        });

        group.MapDelete("/{id:guid}", async (
            Guid id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            IResourceAuthorizationService authorization,
            CancellationToken cancellationToken) =>
        {
            if (await ResourceWriteGuard.RequireWriteAsync(authorization, user, id, ResourceType.Document, cancellationToken) is { } denied)
                return denied;

            var doc = await db.Documents
                .ForTenant(user)
                .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

            if (doc is null)
                return Results.NotFound();

            db.Documents.Remove(doc);
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        group.MapGet("/{id:guid}/versions", async (
            Guid id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var versions = await db.DocumentVersions
                .AsNoTracking()
                .Where(v => v.DocumentId == id)
                .OrderByDescending(v => v.VersionNumber)
                .Select(v => new { v.Id, v.VersionNumber, v.ChangeNote, v.Title, v.CreatedAt })
                .ToListAsync(cancellationToken);

            return Results.Ok(versions);
        });

        group.MapGet("/{id:guid}/versions/{versionId:guid}", async (
            Guid id,
            Guid versionId,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var version = await db.DocumentVersions
                .AsNoTracking()
                .FirstOrDefaultAsync(v => v.DocumentId == id && v.Id == versionId, cancellationToken);

            return version is null ? Results.NotFound() : Results.Ok(new
            {
                version.Id,
                version.VersionNumber,
                version.Title,
                version.Slug,
                version.Summary,
                version.Content,
                version.Tags,
                version.ChangeNote,
                version.CreatedAt,
            });
        });

        group.MapPost("/{id:guid}/restore", async (
            Guid id,
            [FromBody] RestoreVersionRequest request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            IResourceAuthorizationService authorization,
            CancellationToken cancellationToken) =>
        {
            // Restoring rewrites the document's current content, so it is a write on the document
            // itself even though the route reads like history navigation.
            if (await ResourceWriteGuard.RequireWriteAsync(authorization, user, id, ResourceType.Document, cancellationToken) is { } denied)
                return denied;

            var doc = await db.Documents.ForTenant(user).FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
            if (doc is null)
                return Results.NotFound();

            var version = await db.DocumentVersions
                .FirstOrDefaultAsync(v => v.DocumentId == id && v.Id == request.VersionId, cancellationToken);

            if (version is null)
                return Results.NotFound();

            var nextVersionNumber = (await db.DocumentVersions.Where(v => v.DocumentId == id).MaxAsync(v => (int?)v.VersionNumber, cancellationToken) ?? 0) + 1;

            db.DocumentVersions.Add(new DocumentVersion
            {
                DocumentId = doc.Id,
                VersionNumber = nextVersionNumber,
                Title = doc.Title,
                Slug = doc.Slug,
                Summary = doc.Summary,
                Content = doc.Content,
                Tags = doc.Tags,
                ChangeNote = $"Restore before version {version.VersionNumber}",
            });

            doc.Title = version.Title;
            doc.Slug = version.Slug;
            doc.Summary = version.Summary;
            doc.Content = version.Content;
            doc.Tags = version.Tags;

            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        return app;
    }

    public static async Task<IReadOnlyList<DocumentListItem>> ListAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        string? search = null,
        Guid? folderId = null,
        CancellationToken cancellationToken = default)
    {
        if (folderId is Guid fid)
        {
            var folderInTenant = await db.DocumentFolders.ForTenant(user).AsNoTracking()
                .AnyAsync(f => f.Id == fid, cancellationToken);
            if (!folderInTenant)
                return [];
        }

        var query = db.Documents
            .ForTenant(user)
            .AsNoTracking()
            .Where(d => d.IsPublished);

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(d =>
                (d.Title != null && d.Title.Contains(search)) ||
                (d.Summary != null && d.Summary.Contains(search)) ||
                (d.Tags != null && d.Tags.Contains(search)));
        }

        if (folderId is Guid folderFilter)
            query = query.Where(d => d.FolderId == folderFilter);

        var docs = await query
            .OrderBy(d => d.Title)
            .Select(d => new DocumentListItem(d.Id, d.Title, d.Slug, d.Summary, d.Tags, d.CompanyId, d.FolderId, d.UpdatedAt))
            .ToListAsync(cancellationToken);

        return docs;
    }

    public static async Task<IResult> CreateAsync(
        CreateDocumentRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        if (await CompanyEndpoints.EnsureCompanyInTenantAsync(db, user, request.CompanyId, cancellationToken) is { } badCompany)
            return badCompany;
        if (await FolderEndpoints.EnsureFolderInTenantAsync(db, user, request.FolderId, cancellationToken) is { } badFolder)
            return badFolder;

        var doc = new Document
        {
            TenantId = user.TenantId.Value,
            Title = request.Title,
            Slug = request.Slug ?? request.Title.ToLowerInvariant().Replace(' ', '-'),
            Summary = request.Summary,
            Content = request.Content,
            Tags = request.Tags,
            IsPublished = request.IsPublished,
            CompanyId = request.CompanyId,
            FolderId = request.FolderId,
        };

        db.Documents.Add(doc);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/documents/{doc.Id}", new { doc.Id, doc.Title, doc.Slug, doc.FolderId });
    }

    public static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateDocumentRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var doc = await db.Documents
            .ForTenant(user)
            .Include(d => d.Versions.OrderByDescending(v => v.VersionNumber).Take(1))
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

        if (doc is null)
            return Results.NotFound();

        if (await CompanyEndpoints.EnsureCompanyInTenantAsync(db, user, request.CompanyId, cancellationToken) is { } badCompany)
            return badCompany;
        if (request.CompanyId is Guid companyId)
            doc.CompanyId = companyId;

        if (await FolderEndpoints.EnsureFolderInTenantAsync(db, user, request.FolderId, cancellationToken) is { } badFolder)
            return badFolder;
        if (request.FolderId is Guid folderId)
            doc.FolderId = folderId;

        var nextVersionNumber = (doc.Versions.Max(v => (int?)v.VersionNumber) ?? 0) + 1;

        db.DocumentVersions.Add(new DocumentVersion
        {
            DocumentId = doc.Id,
            VersionNumber = nextVersionNumber,
            Title = doc.Title,
            Slug = doc.Slug,
            Summary = doc.Summary,
            Content = doc.Content,
            Tags = doc.Tags,
            ChangeNote = request.ChangeNote,
        });

        doc.Title = request.Title ?? doc.Title;
        doc.Slug = request.Slug ?? doc.Slug;
        doc.Summary = request.Summary ?? doc.Summary;
        doc.Content = request.Content ?? doc.Content;
        doc.Tags = request.Tags ?? doc.Tags;
        doc.IsPublished = request.IsPublished ?? doc.IsPublished;

        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }
}

public sealed record DocumentListItem(
    Guid Id,
    string Title,
    string? Slug,
    string? Summary,
    string? Tags,
    Guid? CompanyId,
    Guid? FolderId,
    DateTimeOffset UpdatedAt);

public record CreateDocumentRequest(
    string Title,
    string? Slug,
    string? Summary,
    string? Content,
    string? Tags,
    bool IsPublished = true,
    Guid? CompanyId = null,
    Guid? FolderId = null);

public record UpdateDocumentRequest(
    string? Title,
    string? Slug,
    string? Summary,
    string? Content,
    string? Tags,
    bool? IsPublished,
    string? ChangeNote,
    Guid? CompanyId = null,
    Guid? FolderId = null);

public record RestoreVersionRequest(Guid VersionId);
