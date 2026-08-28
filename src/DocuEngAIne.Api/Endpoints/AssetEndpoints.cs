using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
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
                Fields = t.Fields.Select(f => new { f.Id, f.Name, f.FieldType, f.IsRequired, f.IsExpiration }),
            }));
        });

        group.MapPost("/types", async (
            [FromBody] CreateAssetTypeRequest request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            IResourceAuthorizationService authorization,
            CancellationToken cancellationToken) =>
        {
            // Asset types are tenant-wide schema, not a resource anyone can hold a grant on, so this
            // resolves to the caller's tenant-wide role.
            if (await ResourceWriteGuard.RequireTenantWriteAsync(authorization, user, ResourceType.Asset, cancellationToken) is { } denied)
                return denied;

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
                    IsExpiration = f.IsExpiration,
                    SortOrder = i,
                }).ToList() ?? [],
            };

            db.AssetTypes.Add(assetType);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/assets/types/{assetType.Id}", new { assetType.Id, assetType.Name });
        });

        group.MapPut("/fields/{id:guid}", async (
            Guid id,
            [FromBody] UpdateFieldDefinitionRequest request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            IResourceAuthorizationService authorization,
            CancellationToken cancellationToken) =>
        {
            // The route id is a FieldDefinition, not an Asset, so a per-asset grant cannot apply to
            // it: field definitions are tenant-wide schema and gate on the tenant-wide role.
            if (await ResourceWriteGuard.RequireTenantWriteAsync(authorization, user, ResourceType.Asset, cancellationToken) is { } denied)
                return denied;

            var field = await db.FieldDefinitions
                .Where(f => db.AssetTypes.ForTenant(user).Any(t => t.Id == f.AssetTypeId))
                .FirstOrDefaultAsync(f => f.Id == id, cancellationToken);

            if (field is null)
                return Results.NotFound();

            field.Name = request.Name ?? field.Name;
            field.FieldType = request.FieldType ?? field.FieldType;
            if (request.IsRequired.HasValue)
                field.IsRequired = request.IsRequired.Value;
            if (request.IsExpiration.HasValue)
                field.IsExpiration = request.IsExpiration.Value;
            if (request.SortOrder.HasValue)
                field.SortOrder = request.SortOrder.Value;

            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
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

            return Results.Ok(assets.Select(a => new { a.Id, a.Name, a.Location, a.Status, a.CompanyId, a.ExpiresAt, AssetType = a.AssetType?.Name }));
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
            IResourceAuthorizationService authorization,
            CancellationToken cancellationToken) =>
        {
            // The asset does not exist yet, so no grant can name it: creation gates on the
            // tenant-wide role.
            if (await ResourceWriteGuard.RequireTenantWriteAsync(authorization, user, ResourceType.Asset, cancellationToken) is { } denied)
                return denied;

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
                ExpiresAt = request.ExpiresAt,
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
            IResourceAuthorizationService authorization,
            CancellationToken cancellationToken) =>
        {
            if (await ResourceWriteGuard.RequireWriteAsync(authorization, user, id, ResourceType.Asset, cancellationToken) is { } denied)
                return denied;

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
            if (request.ExpiresAt.HasValue)
                asset.ExpiresAt = request.ExpiresAt;

            await db.SaveChangesAsync(cancellationToken);
            return Results.NoContent();
        });

        group.MapDelete("/{id:guid}", async (
            Guid id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            IResourceAuthorizationService authorization,
            CancellationToken cancellationToken) =>
        {
            if (await ResourceWriteGuard.RequireWriteAsync(authorization, user, id, ResourceType.Asset, cancellationToken) is { } denied)
                return denied;

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
            asset.ExpiresAt,
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

/// <summary>
/// Turns an <see cref="IResourceAuthorizationService"/> decision into a minimal-API result, so the
/// object-level grants stored in <c>ResourceRoleAssignment</c> actually gate the write routes of the
/// four resource endpoint families (assets, documents, runbooks, Keeper links).
/// </summary>
/// <remarks>
/// <para>
/// The boolean <c>CanWriteAsync</c> is used in preference to <c>EnforceAsync</c>. <c>EnforceAsync</c>
/// signals denial by throwing <see cref="UnauthorizedAccessException"/>, and nothing in this
/// application's pipeline translates that exception, so a denied caller would receive a 500 that
/// looks like a server fault instead of a 403 that tells them to ask for a grant.
/// </para>
/// <para>
/// Denial is <c>Results.StatusCode(403)</c> rather than <c>Results.Forbid()</c> because <c>Forbid</c>
/// defers to the authentication scheme's forbid handler — an indirection that yields no status code
/// a unit test can observe, and that is scheme-dependent for a decision this code has already made.
/// </para>
/// <para>
/// An Entra app-role claim is accepted as an alternative to the stored role, for the same reason
/// <c>TenantAdminAuthorizationHandler</c> accepts it: DocuEngAIne has two independent sources of
/// truth for a caller's rank, and only one of them (the <c>User</c> row) is visible to the
/// resource service. Without this, a tenant that configures app roles but whose members were
/// provisioned as <c>Reader</c> by <c>GET /api/me</c> would see every one of its writers locked out
/// the moment these guards landed. The claim is checked first because it costs no database
/// round-trip.
/// </para>
/// </remarks>
public static class ResourceWriteGuard
{
    /// <summary>
    /// Returns <see langword="null"/> when the caller may write <paramref name="resourceId"/>, or a
    /// 403 result to return from the handler when they may not.
    /// </summary>
    /// <remarks>
    /// Deliberately runs before the handler loads the row. The answer does not depend on whether the
    /// row exists, and checking first keeps a denied caller from learning which ids are real.
    /// </remarks>
    public static async Task<IResult?> RequireWriteAsync(
        IResourceAuthorizationService authorization,
        ICurrentUser user,
        Guid resourceId,
        string resourceType,
        CancellationToken cancellationToken = default)
    {
        if (user.HasRole(UserRole.Contributor))
            return null;

        return await authorization.CanWriteAsync(resourceId, resourceType, cancellationToken)
            ? null
            : Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Write gate for operations that have no resource to name yet — creating a record, or editing
    /// tenant-wide schema such as asset types and field definitions.
    /// </summary>
    /// <remarks>
    /// <see cref="Guid.Empty"/> can never identify a stored entity (<c>EntityBase.Id</c> is always a
    /// generated GUID), so no <c>ResourceRoleAssignment</c> can match it and the lookup falls through
    /// to the caller's tenant-wide role. That is the intended answer: a grant on one document cannot
    /// confer the right to create new ones.
    /// </remarks>
    public static Task<IResult?> RequireTenantWriteAsync(
        IResourceAuthorizationService authorization,
        ICurrentUser user,
        string resourceType,
        CancellationToken cancellationToken = default) =>
        RequireWriteAsync(authorization, user, Guid.Empty, resourceType, cancellationToken);
}

public record CreateAssetTypeRequest(string Name, string? Description, string? Icon, List<AssetTypeFieldRequest>? Fields);
public record AssetTypeFieldRequest(string Name, string Type, bool IsRequired, bool IsExpiration = false);
public record UpdateFieldDefinitionRequest(string? Name = null, string? FieldType = null, bool? IsRequired = null, bool? IsExpiration = null, int? SortOrder = null);
public record CreateAssetRequest(string Name, Guid AssetTypeId, string? Location, string? Notes, string? Status, Guid? CompanyId = null, DateTimeOffset? ExpiresAt = null);
public record UpdateAssetRequest(string? Name, Guid? AssetTypeId, string? Location, string? Notes, string? Status, Guid? CompanyId = null, DateTimeOffset? ExpiresAt = null);
