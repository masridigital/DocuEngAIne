using System.Text.Json;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Assets;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

public static class AssetEndpoints
{
    public const string LayoutNotFoundMessage = "Asset layout not found.";
    public const string LayoutChangeWithValuesMessage =
        "Clear this asset's field values before changing its layout: they belong to the current layout's fields.";

    public static IEndpointRouteBuilder MapAssetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/assets").RequireAuthorization();

        group.MapGet("", async (
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
            await ListAsync(db, user, cancellationToken));

        group.MapGet("/{id:guid}", async (
            Guid id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
            await GetAsync(id, db, user, cancellationToken));

        group.MapPost("", PostAsync);
        group.MapPut("/{id:guid}", PutAsync);
        group.MapPut("/{id:guid}/fields", PutFieldsAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);

        return app;
    }

    public static async Task<IResult> PostAsync(
        [FromBody] CreateAssetRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IResourceAuthorizationService authorization,
        CancellationToken cancellationToken = default)
    {
        // The asset does not exist yet, so no grant can name it: creation gates on the
        // tenant-wide role.
        if (await ResourceWriteGuard.RequireTenantWriteAsync(authorization, user, ResourceType.Asset, cancellationToken) is { } denied)
            return denied;

        if (await CompanyEndpoints.EnsureCompanyInTenantAsync(db, user, request.CompanyId, cancellationToken) is { } badCompany)
            return badCompany;

        // The layout must be this tenant's: another tenant's id would otherwise attach its fields here.
        var layout = await db.AssetTypes.ForTenant(user)
            .Include(t => t.Fields)
            .FirstOrDefaultAsync(t => t.Id == request.AssetTypeId, cancellationToken);
        if (layout is null)
            return Results.BadRequest(LayoutNotFoundMessage);
        if (await AssetLayoutEndpoints.EnsureUsableAsync(db, user, layout, request.CompanyId, cancellationToken) is { } unusable)
            return unusable;

        var checkedValues = await NormalizeValuesAsync(db, user, layout, request.Fields, creating: true, cancellationToken);
        if (checkedValues.Error is { } invalidValues)
            return invalidValues;

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
            HaloAssetUrl = NullIfEmpty(request.HaloAssetUrl),
            NinjaDeviceUrl = NullIfEmpty(request.NinjaDeviceUrl),
            ExternalIdsJson = NullIfEmpty(request.ExternalIdsJson),
        };

        db.Assets.Add(asset);
        foreach (var (fieldId, value) in checkedValues.Values!)
        {
            if (value is not null)
                db.CustomFieldValues.Add(new CustomFieldValue { AssetId = asset.Id, FieldDefinitionId = fieldId, Value = value });
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/assets/{asset.Id}", new { asset.Id, asset.Name });
    }

    public static async Task<IResult> PutAsync(
        Guid id,
        [FromBody] UpdateAssetRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IResourceAuthorizationService authorization,
        CancellationToken cancellationToken = default)
    {
        if (await ResourceWriteGuard.RequireWriteAsync(authorization, user, id, ResourceType.Asset, cancellationToken) is { } denied)
            return denied;

        return await UpdateAsync(id, request, db, user, cancellationToken);
    }

    /// <summary>Archives to the Museum (restorable); permanent deletion is <c>DELETE /api/archive/{entryId}</c>.</summary>
    public static async Task<IResult> DeleteAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IResourceAuthorizationService authorization,
        IAuditService? audit = null,
        [FromQuery] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (await ResourceWriteGuard.RequireWriteAsync(authorization, user, id, ResourceType.Asset, cancellationToken, CompanyAccessLevel.Manage) is { } denied)
            return denied;

        var asset = await db.Assets
            .ForTenant(user)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

        if (asset is null)
            return Results.NotFound();

        await ArchiveEndpoints.ArchiveAsync(db, user, audit, asset, ResourceType.Asset, asset.Id, asset.Name, reason, cancellationToken);
        return Results.NoContent();
    }

    public static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateAssetRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var asset = await db.Assets
            .ForTenant(user)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

        if (asset is null)
            return Results.NotFound();

        var originalCompanyId = asset.CompanyId;
        if (await CompanyEndpoints.ApplyCompanyIdOnUpdateAsync(
                db, user, request.CompanyId, request.CompanyIdClear, value => asset.CompanyId = value, cancellationToken)
            is { } badCompany)
            return badCompany;

        var layoutChanged = request.AssetTypeId is Guid requested && requested != asset.AssetTypeId;
        if (layoutChanged || asset.CompanyId != originalCompanyId)
        {
            var layoutId = request.AssetTypeId ?? asset.AssetTypeId;
            var layout = await db.AssetTypes.ForTenant(user).FirstOrDefaultAsync(t => t.Id == layoutId, cancellationToken);
            if (layout is null)
                return Results.BadRequest(LayoutNotFoundMessage);
            if (layoutChanged)
            {
                if (await db.CustomFieldValues.AnyAsync(v => v.AssetId == id, cancellationToken))
                    return Results.Conflict(new { error = LayoutChangeWithValuesMessage });
                if (await AssetLayoutEndpoints.EnsureUsableAsync(db, user, layout, asset.CompanyId, cancellationToken) is { } unusable)
                    return unusable;
            }
            else if (await AssetLayoutEndpoints.EnsureAvailableAsync(db, user, layout, asset.CompanyId, cancellationToken) is { } unavailable)
            {
                // Moving to another company: a layout limited to chosen companies must be enabled there.
                return unavailable;
            }

            asset.AssetTypeId = layout.Id;
        }

        asset.Name = request.Name ?? asset.Name;
        asset.Location = request.Location ?? asset.Location;
        asset.Notes = request.Notes ?? asset.Notes;
        asset.Status = request.Status ?? asset.Status;
        if (request.ExpiresAt.HasValue)
            asset.ExpiresAt = request.ExpiresAt;
        if (request.HaloAssetUrl is not null)
            asset.HaloAssetUrl = NullIfEmpty(request.HaloAssetUrl);
        if (request.NinjaDeviceUrl is not null)
            asset.NinjaDeviceUrl = NullIfEmpty(request.NinjaDeviceUrl);
        if (request.ExternalIdsJson is not null)
            asset.ExternalIdsJson = NullIfEmpty(request.ExternalIdsJson);

        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    /// <summary>
    /// Sets or clears field values. Only the fields submitted change; a value of null or empty
    /// clears one, which a required field refuses. Values are checked and stored per field type.
    /// </summary>
    public static async Task<IResult> PutFieldsAsync(
        Guid id,
        [FromBody] UpdateAssetFieldsRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IResourceAuthorizationService authorization,
        CancellationToken cancellationToken = default)
    {
        if (await ResourceWriteGuard.RequireWriteAsync(authorization, user, id, ResourceType.Asset, cancellationToken) is { } denied)
            return denied;

        var asset = await db.Assets
            .ForTenant(user)
            .Include(a => a.AssetType)
                .ThenInclude(t => t!.Fields)
            .Include(a => a.CustomFieldValues)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (asset?.AssetType is null)
            return Results.NotFound();

        var checkedValues = await NormalizeValuesAsync(db, user, asset.AssetType, request.Values, creating: false, cancellationToken);
        if (checkedValues.Error is { } invalid)
            return invalid;

        foreach (var (fieldId, value) in checkedValues.Values!)
        {
            var existing = asset.CustomFieldValues.FirstOrDefault(v => v.FieldDefinitionId == fieldId);
            if (value is null)
            {
                if (existing is not null)
                    db.CustomFieldValues.Remove(existing);
            }
            else if (existing is null)
            {
                db.CustomFieldValues.Add(new CustomFieldValue { AssetId = asset.Id, FieldDefinitionId = fieldId, Value = value });
            }
            else
            {
                existing.Value = value;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, db, user, cancellationToken);
    }

    /// <summary>
    /// Checks submitted values against the layout and returns each one normalized (null = clear).
    /// When <paramref name="creating"/>, every required field must be given a value; otherwise only
    /// the submitted fields are checked, and a required one cannot be cleared.
    /// </summary>
    private static async Task<(Dictionary<Guid, string?>? Values, IResult? Error)> NormalizeValuesAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        AssetType layout,
        Dictionary<Guid, JsonElement>? submitted,
        bool creating,
        CancellationToken cancellationToken)
    {
        submitted ??= new Dictionary<Guid, JsonElement>();
        var fields = layout.Fields.ToDictionary(f => f.Id);
        var errors = new Dictionary<string, string>();
        foreach (var unknown in submitted.Keys.Where(k => !fields.ContainsKey(k)))
            errors[unknown.ToString()] = "This field is not part of the asset's layout.";

        var lists = await AssetLayoutEndpoints.LoadListsAsync(db, user, layout.Fields.Select(f => f.OptionListId), cancellationToken);
        var values = new Dictionary<Guid, string?>();
        foreach (var (fieldId, raw) in submitted)
        {
            if (!fields.TryGetValue(fieldId, out var field))
                continue;
            var options = field.OptionListId is Guid listId && lists.TryGetValue(listId, out var list) ? list : null;
            if (!AssetFieldRules.TryNormalize(field, raw, options, out var stored, out var error))
                errors[fieldId.ToString()] = error!;
            else if (stored is null && field.IsRequired)
                errors[fieldId.ToString()] = $"{field.Name} is required.";
            else
                values[fieldId] = stored;
        }

        if (creating)
        {
            foreach (var missing in layout.Fields.Where(f => f.IsRequired && !submitted.ContainsKey(f.Id)))
                errors[missing.Id.ToString()] = $"{missing.Name} is required.";
        }

        if (errors.Count > 0)
        {
            var message = errors.Count == 1 ? errors.Values.First() : "Some field values are not valid.";
            return (null, Results.BadRequest(new { error = message, fields = errors }));
        }

        return (values, null);
    }

    public static async Task<IResult> ListAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var assets = await db.Assets
            .ForTenant(user)
            .AsNoTracking()
            .Include(a => a.AssetType)
            .OrderBy(a => a.Name)
            .ToListAsync(cancellationToken);

        return Results.Ok(assets.Select(MapListAsset));
    }

    public static async Task<IResult> GetAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
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
    }

    private static object MapListAsset(Asset asset) => new
    {
        asset.Id,
        asset.Name,
        asset.Location,
        asset.Status,
        asset.CompanyId,
        asset.ExpiresAt,
        asset.HaloAssetUrl,
        asset.NinjaDeviceUrl,
        AssetType = asset.AssetType?.Name,
    };

    private static object MapAsset(Asset asset)
    {
        var values = asset.CustomFieldValues.ToDictionary(v => v.FieldDefinitionId, v => v.Value);
        IEnumerable<FieldDefinition> layoutFields = asset.AssetType?.Fields ?? Enumerable.Empty<FieldDefinition>();
        var layoutFieldIds = layoutFields.Select(f => f.Id).ToHashSet();
        var fields = layoutFields
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new AssetFieldValueView(
                f.Id, f.Name, f.FieldType, f.Section, f.HelpText, f.IsRequired, f.OptionListId, values.GetValueOrDefault(f.Id)))
            // Values whose field is no longer on the layout (legacy data) are still shown, never dropped.
            .Concat(asset.CustomFieldValues
                .Where(v => !layoutFieldIds.Contains(v.FieldDefinitionId))
                .Select(v => new AssetFieldValueView(
                    v.FieldDefinitionId,
                    v.FieldDefinition?.Name ?? "Field",
                    v.FieldDefinition?.FieldType ?? AssetFieldType.Text,
                    null,
                    null,
                    false,
                    null,
                    v.Value,
                    OnLayout: false)))
            .ToList();

        return new
        {
            asset.Id,
            asset.Name,
            asset.Location,
            asset.Status,
            asset.Notes,
            asset.CompanyId,
            asset.ExpiresAt,
            asset.HaloAssetUrl,
            asset.NinjaDeviceUrl,
            asset.ExternalIdsJson,
            AssetType = new { asset.AssetType?.Id, asset.AssetType?.Name },
            Fields = fields,
        };
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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
    /// <param name="companyLevel">
    /// The company access the operation needs on the record's company: Edit to change it (the
    /// default), Manage to archive, restore or delete it, View where the record is only the context
    /// for a write elsewhere (starting a run of a runbook). Checked only for a named record.
    /// </param>
    public static async Task<IResult?> RequireWriteAsync(
        IResourceAuthorizationService authorization,
        ICurrentUser user,
        Guid resourceId,
        string resourceType,
        CancellationToken cancellationToken = default,
        CompanyAccessLevel companyLevel = CompanyAccessLevel.Edit)
    {
        if (!user.HasRole(UserRole.Contributor)
            && !await authorization.CanWriteAsync(resourceId, resourceType, cancellationToken))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (resourceId != Guid.Empty
            && await authorization.GetCompanyAccessAsync(resourceId, resourceType, cancellationToken) < companyLevel)
        {
            return CompanyEndpoints.CompanyAccessDenied(tenantWide: false);
        }

        return null;
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

public record CreateAssetTypeRequest(
    string Name,
    string? Description = null,
    string? Icon = null,
    List<AssetTypeFieldRequest>? Fields = null,
    bool AvailableToAllCompanies = true);
public record AssetTypeFieldRequest(
    string Name,
    string Type,
    bool IsRequired = false,
    bool IsExpiration = false,
    string? Section = null,
    string? HelpText = null,
    Guid? OptionListId = null);
public record UpdateFieldDefinitionRequest(
    string? Name = null,
    string? FieldType = null,
    bool? IsRequired = null,
    bool? IsExpiration = null,
    int? SortOrder = null,
    string? Section = null,
    string? HelpText = null,
    Guid? OptionListId = null,
    bool OptionListClear = false);
/// <summary>Field values keyed by field id. A JSON null or empty value clears the field.</summary>
public record UpdateAssetFieldsRequest(Dictionary<Guid, JsonElement>? Values);
/// <param name="OnLayout">False for a value whose field is no longer on the asset's layout (older data): shown, not editable.</param>
public record AssetFieldValueView(
    Guid FieldId,
    string Name,
    string FieldType,
    string? Section,
    string? HelpText,
    bool IsRequired,
    Guid? OptionListId,
    string? Value,
    bool OnLayout = true);
public record CreateAssetRequest(
    string Name,
    Guid AssetTypeId,
    string? Location,
    string? Notes,
    string? Status,
    Guid? CompanyId = null,
    DateTimeOffset? ExpiresAt = null,
    string? HaloAssetUrl = null,
    string? NinjaDeviceUrl = null,
    string? ExternalIdsJson = null,
    Dictionary<Guid, JsonElement>? Fields = null);
public record UpdateAssetRequest(
    string? Name,
    Guid? AssetTypeId,
    string? Location,
    string? Notes,
    string? Status,
    Guid? CompanyId = null,
    DateTimeOffset? ExpiresAt = null,
    bool CompanyIdClear = false,
    string? HaloAssetUrl = null,
    string? NinjaDeviceUrl = null,
    string? ExternalIdsJson = null);
