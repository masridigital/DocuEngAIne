using DocuEngAIne.Core.Entities;
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
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
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

            var docs = await query
                .OrderBy(d => d.Title)
                .Select(d => new { d.Id, d.Title, d.Slug, d.Summary, d.Tags, d.UpdatedAt })
                .ToListAsync(cancellationToken);

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
                doc.IsPublished,
                doc.UpdatedAt,
            });
        });

        group.MapPost("", async (
            [FromBody] CreateDocumentRequest request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var doc = new Document
            {
                TenantId = user.TenantId!.Value,
                Title = request.Title,
                Slug = request.Slug ?? request.Title.ToLowerInvariant().Replace(' ', '-'),
                Summary = request.Summary,
                Content = request.Content,
                Tags = request.Tags,
                IsPublished = request.IsPublished,
            };

            db.Documents.Add(doc);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/documents/{doc.Id}", new { doc.Id, doc.Title, doc.Slug });
        });

        group.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] UpdateDocumentRequest request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var doc = await db.Documents
                .ForTenant(user)
                .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

            if (doc is null)
                return Results.NotFound();

            doc.Title = request.Title ?? doc.Title;
            doc.Slug = request.Slug ?? doc.Slug;
            doc.Summary = request.Summary ?? doc.Summary;
            doc.Content = request.Content ?? doc.Content;
            doc.Tags = request.Tags ?? doc.Tags;
            doc.IsPublished = request.IsPublished ?? doc.IsPublished;

            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        group.MapDelete("/{id:guid}", async (
            Guid id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var doc = await db.Documents
                .ForTenant(user)
                .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

            if (doc is null)
                return Results.NotFound();

            db.Documents.Remove(doc);
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        return app;
    }
}

public record CreateDocumentRequest(
    string Title,
    string? Slug,
    string? Summary,
    string? Content,
    string? Tags,
    bool IsPublished = true);

public record UpdateDocumentRequest(
    string? Title,
    string? Slug,
    string? Summary,
    string? Content,
    string? Tags,
    bool? IsPublished);
