using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

public static class AssetEndpoints
{
    public static IEndpointRouteBuilder MapAssetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/assets").RequireAuthorization();

        group.MapGet("/types", async (
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var types = await db.AssetTypes
                .ForTenant(user)
                .AsNoTracking()
                .Include(t => t.Fields.OrderBy(f => f.SortOrder))
                .ToListAsync(cancellationToken);

            return Results.Ok(types.Select(t => new
            {
                t.Id,
                t.Name,
                t.Description,
                t.Icon,
                Fields = t.Fields.Select(f => new { f.Id, f.Name, f.FieldType, f.IsRequired }),
            }));
        });

        group.MapPost("/types", async (
            [FromBody] CreateAssetTypeRequest request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var assetType = new AssetType
            {
                TenantId = user.TenantId!.Value,
                Name = request.Name,
                Description = request.Description,
                Icon = request.Icon,
                Fields = request.Fields?.Select((f, i) => new FieldDefinition
                {
                    Name = f.Name,
                    FieldType = f.Type,
                    IsRequired = f.IsRequired,
                    SortOrder = i,
                }).ToList() ?? [],
            };

            db.AssetTypes.Add(assetType);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/assets/types/{assetType.Id}", new { assetType.Id, assetType.Name });
        });

        group.MapGet("", async (
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var assets = await db.Assets
                .ForTenant(user)
                .AsNoTracking()
                .Include(a => a.AssetType)
                .OrderBy(a => a.Name)
                .ToListAsync(cancellationToken);

            return Results.Ok(assets.Select(a => new { a.Id, a.Name, a.Location, a.Status, a.CompanyId, AssetType = a.AssetType?.Name }));
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var asset = await db.Assets
                .ForTenant(user)
                .AsNoTracking()
                .Include(a => a.AssetType)
                    .ThenInclude(t => t!.Fields)
                .Include(a => a.CustomFieldValues)
                    .ThenInclude(v => v.FieldDefinition)
                .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

            return asset is null ? Results.NotFound() : Results.Ok(MapAsset(asset));
        });

        group.MapPost("", async (
            [FromBody] CreateAssetRequest request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            if (await CompanyEndpoints.EnsureCompanyInTenantAsync(db, user, request.CompanyId, cancellationToken) is { } badCompany)
                return badCompany;

            var asset = new Asset
            {
                TenantId = user.TenantId!.Value,
                Name = request.Name,
                Location = request.Location,
                Notes = request.Notes,
                Status = request.Status ?? "Active",
                AssetTypeId = request.AssetTypeId,
                CompanyId = request.CompanyId,
            };

            db.Assets.Add(asset);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/assets/{asset.Id}", new { asset.Id, asset.Name });
        });

        group.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] UpdateAssetRequest request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var asset = await db.Assets
                .ForTenant(user)
                .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

            if (asset is null)
                return Results.NotFound();

            if (await CompanyEndpoints.EnsureCompanyInTenantAsync(db, user, request.CompanyId, cancellationToken) is { } badCompany)
                return badCompany;
            if (request.CompanyId is Guid companyId)
                asset.CompanyId = companyId;

            asset.Name = request.Name ?? asset.Name;
            asset.Location = request.Location ?? asset.Location;
            asset.Notes = request.Notes ?? asset.Notes;
            asset.Status = request.Status ?? asset.Status;
            asset.AssetTypeId = request.AssetTypeId ?? asset.AssetTypeId;

            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        group.MapDelete("/{id:guid}", async (
            Guid id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var asset = await db.Assets
                .ForTenant(user)
                .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

            if (asset is null)
                return Results.NotFound();

            db.Assets.Remove(asset);
            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        return app;
    }

    private static object MapAsset(Asset asset)
    {
        return new
        {
            asset.Id,
            asset.Name,
            asset.Location,
            asset.Status,
            asset.Notes,
            asset.CompanyId,
            AssetType = new { asset.AssetType?.Id, asset.AssetType?.Name },
            Fields = asset.CustomFieldValues.Select(v => new
            {
                v.FieldDefinition?.Name,
                v.FieldDefinition?.FieldType,
                v.Value,
            }),
        };
    }
}

public record CreateAssetTypeRequest(string Name, string? Description, string? Icon, List<AssetTypeFieldRequest>? Fields);
public record AssetTypeFieldRequest(string Name, string Type, bool IsRequired);
public record CreateAssetRequest(string Name, Guid AssetTypeId, string? Location, string? Notes, string? Status, Guid? CompanyId = null);
public record UpdateAssetRequest(string? Name, Guid? AssetTypeId, string? Location, string? Notes, string? Status, Guid? CompanyId = null);
