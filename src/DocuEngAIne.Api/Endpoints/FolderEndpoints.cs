using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

public static class FolderEndpoints
{
    public const string FolderNotFoundMessage = "Folder not found.";
    public const string NameRequiredMessage = "Name is required.";
    public const string SelfParentMessage = "Folder cannot be its own parent.";
    public const string NestedUnderDescendantMessage = "Folder cannot be nested under its descendant.";

    public static IEndpointRouteBuilder MapFolderEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/folders").RequireAuthorization();

        group.MapGet("", async (
            [FromQuery] Guid? companyId,
            [FromQuery] Guid? parentId,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var items = await ListAsync(db, user, companyId, parentId, cancellationToken);
            return Results.Ok(items);
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var folder = await db.DocumentFolders.ForTenant(user).AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
            return folder is null ? Results.NotFound() : Results.Ok(Map(folder));
        });

        group.MapPost("", async (
            [FromBody] CreateFolderRequest request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
            await CreateAsync(request, db, user, cancellationToken));

        group.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] UpdateFolderRequest request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
            await UpdateAsync(id, request, db, user, cancellationToken));

        group.MapDelete("/{id:guid}", async (
            Guid id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
            await DeleteAsync(id, db, user, cancellationToken));

        return app;
    }

    public static async Task<IReadOnlyList<FolderItem>> ListAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        Guid? companyId = null,
        Guid? parentId = null,
        CancellationToken cancellationToken = default)
    {
        if (companyId is Guid cid)
        {
            var companyInTenant = await db.Companies.ForTenant(user).AsNoTracking()
                .AnyAsync(c => c.Id == cid, cancellationToken);
            if (!companyInTenant)
                return [];
        }

        var query = db.DocumentFolders.ForTenant(user).AsNoTracking().AsQueryable();
        if (companyId is Guid companyFilter)
            query = query.Where(f => f.CompanyId == companyFilter);
        if (parentId is Guid parentFilter)
            query = query.Where(f => f.ParentId == parentFilter);

        var folders = await query
            .OrderBy(f => f.Name)
            .ToListAsync(cancellationToken);
        return folders.Select(Map).ToList();
    }

    public static async Task<IResult> CreateAsync(
        CreateFolderRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        var name = request.Name?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(NameRequiredMessage);

        if (await CompanyEndpoints.EnsureCompanyInTenantAsync(db, user, request.CompanyId, cancellationToken) is { } badCompany)
            return badCompany;
        if (await EnsureFolderInTenantAsync(db, user, request.ParentId, cancellationToken) is { } badParent)
            return badParent;

        var folder = new DocumentFolder
        {
            TenantId = user.TenantId.Value,
            Name = name,
            CompanyId = request.CompanyId,
            ParentId = request.ParentId,
        };
        db.DocumentFolders.Add(folder);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/folders/{folder.Id}", Map(folder));
    }

    public static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateFolderRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var folder = await db.DocumentFolders.ForTenant(user).FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (folder is null)
            return Results.NotFound();

        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(NameRequiredMessage);
            folder.Name = name;
        }

        if (await CompanyEndpoints.ApplyCompanyIdOnUpdateAsync(
                db, user, request.CompanyId, request.CompanyIdClear, value => folder.CompanyId = value, cancellationToken)
            is { } badCompany)
            return badCompany;

        if (await EnsureFolderInTenantAsync(db, user, request.ParentId, cancellationToken) is { } badParent)
            return badParent;
        if (await EnsureParentNotCycleAsync(db, user, id, request.ParentId, cancellationToken) is { } badCycle)
            return badCycle;
        if (request.ParentId is Guid parentId)
            folder.ParentId = parentId;

        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    public static async Task<IResult> DeleteAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var folder = await db.DocumentFolders.ForTenant(user).FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (folder is null)
            return Results.NotFound();

        var children = await db.DocumentFolders.ForTenant(user)
            .Where(f => f.ParentId == id)
            .ToListAsync(cancellationToken);
        foreach (var child in children)
            child.ParentId = folder.ParentId;

        var docs = await db.Documents.ForTenant(user)
            .Where(d => d.FolderId == id)
            .ToListAsync(cancellationToken);
        foreach (var doc in docs)
            doc.FolderId = null;

        db.DocumentFolders.Remove(folder);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    public static async Task<IResult?> EnsureFolderInTenantAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        Guid? folderId,
        CancellationToken cancellationToken = default)
    {
        if (folderId is not Guid id)
            return null;

        var exists = await db.DocumentFolders.ForTenant(user).AnyAsync(f => f.Id == id, cancellationToken);
        return exists ? null : Results.BadRequest(FolderNotFoundMessage);
    }

    public static async Task<IResult?> EnsureParentNotCycleAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        Guid folderId,
        Guid? parentId,
        CancellationToken cancellationToken = default)
    {
        if (parentId is not Guid pid)
            return null;
        if (pid == folderId)
            return Results.BadRequest(SelfParentMessage);

        Guid? current = pid;
        var seen = new HashSet<Guid>();
        while (current is Guid cid && seen.Add(cid))
        {
            if (cid == folderId)
                return Results.BadRequest(NestedUnderDescendantMessage);

            var row = await db.DocumentFolders.ForTenant(user).AsNoTracking()
                .FirstOrDefaultAsync(f => f.Id == cid, cancellationToken);
            if (row is null)
                break;
            current = row.ParentId;
        }

        return null;
    }

    private static FolderItem Map(DocumentFolder f) =>
        new(f.Id, f.Name, f.ParentId, f.CompanyId, f.UpdatedAt);
}

public sealed record FolderItem(
    Guid Id,
    string Name,
    Guid? ParentId,
    Guid? CompanyId,
    DateTimeOffset UpdatedAt);

public record CreateFolderRequest(string Name, Guid? ParentId = null, Guid? CompanyId = null);

public record UpdateFolderRequest(string? Name = null, Guid? ParentId = null, Guid? CompanyId = null, bool CompanyIdClear = false);
