using System.Text.Json;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Assets;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

/// <summary>
/// Asset layouts (asset types): the fields an asset of one kind carries. A layout built here starts
/// as a draft, is published once it validates, and while published every schema change is checked
/// against the same rules and recorded as a new version. A layout can be limited to chosen
/// companies. Writing schema needs Contributor and access to every company (it is tenant-wide);
/// choosing a company for a layout needs Manage on that company.
/// </summary>
public static class AssetLayoutEndpoints
{
    public const string NameRequiredMessage = "Name is required.";
    public const string DuplicateLayoutMessage = "A layout with that name already exists.";
    public const string DuplicateFieldMessage = "This layout already has a field with that name.";
    public const string UnknownFieldTypeMessage = "Unknown field type.";
    public const string OptionListNotFoundMessage = "Option list not found.";
    public const string CompanyNotFoundMessage = "Company not found.";
    public const string InvalidLayoutMessage = "The layout is not valid.";

    private const int NameMaxLength = 200;
    private const int FieldNameMaxLength = 100;

    public static IEndpointRouteBuilder MapAssetLayoutEndpoints(this IEndpointRouteBuilder app)
    {
        var layouts = app.MapGroup("/api/assets/types").RequireAuthorization();
        layouts.MapGet("", ListAsync);
        layouts.MapPost("", CreateAsync);
        layouts.MapGet("/{id:guid}", GetAsync);
        layouts.MapPut("/{id:guid}", UpdateAsync);
        layouts.MapDelete("/{id:guid}", DeleteAsync);
        layouts.MapPost("/{id:guid}/publish", PublishAsync);
        layouts.MapPost("/{id:guid}/unpublish", UnpublishAsync);
        layouts.MapPost("/{id:guid}/fields", AddFieldAsync);
        layouts.MapGet("/{id:guid}/versions", ListVersionsAsync);
        layouts.MapGet("/{id:guid}/versions/{number:int}", GetVersionAsync);
        layouts.MapPut("/{id:guid}/companies/{companyId:guid}", ActivateAsync);
        layouts.MapDelete("/{id:guid}/companies/{companyId:guid}", DeactivateAsync);

        var fields = app.MapGroup("/api/assets/fields").RequireAuthorization();
        fields.MapPut("/{id:guid}", UpdateFieldAsync);
        fields.MapDelete("/{id:guid}", DeleteFieldAsync);

        return app;
    }

    public sealed record UpdateAssetTypeRequest(
        string? Name = null,
        string? Description = null,
        string? Icon = null,
        bool? AvailableToAllCompanies = null);

    public sealed record FieldView(
        Guid Id,
        string Name,
        string FieldType,
        bool IsRequired,
        bool IsExpiration,
        int SortOrder,
        string? Section,
        string? HelpText,
        Guid? OptionListId,
        string? OptionListName);

    /// <param name="EnabledCompanyIds">
    /// Companies a layout limited to chosen companies is enabled for — of those the caller can see.
    /// </param>
    public sealed record LayoutSummary(
        Guid Id,
        string Name,
        string? Description,
        string? Icon,
        bool IsPublished,
        DateTimeOffset? PublishedAt,
        bool AvailableToAllCompanies,
        int CurrentVersion,
        IReadOnlyList<FieldView> Fields,
        IReadOnlyList<Guid> EnabledCompanyIds);

    public sealed record ActivationView(Guid CompanyId, string CompanyName, DateTimeOffset ActivatedAt);

    /// <param name="Problems">What would stop the layout from being published as it stands.</param>
    public sealed record LayoutDetail(
        LayoutSummary Layout,
        IReadOnlyList<ActivationView> Companies,
        int AssetCount,
        IReadOnlyList<AssetFieldRules.LayoutProblem> Problems);

    public sealed record VersionSummary(int VersionNumber, string? Summary, string? CreatedByName, DateTimeOffset CreatedAt);
    public sealed record VersionDetail(int VersionNumber, string? Summary, string? CreatedByName, DateTimeOffset CreatedAt, JsonElement Schema);

    public static async Task<IResult> ListAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var layouts = await db.AssetTypes.ForTenant(user).AsNoTracking()
            .Include(t => t.Fields).ThenInclude(f => f.OptionList)
            .Include(t => t.CompanyActivations)
            .OrderBy(t => t.Name)
            .ToListAsync(cancellationToken);
        return Results.Ok(layouts.Select(MapSummary).ToList());
    }

    public static async Task<IResult> GetAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var detail = await LoadDetailAsync(db, user, id, cancellationToken);
        return detail is null ? Results.NotFound() : Results.Ok(detail);
    }

    /// <summary>A new layout is a draft: it is not offered for new assets until it is published.</summary>
    public static async Task<IResult> CreateAsync(
        [FromBody] CreateAssetTypeRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IResourceAuthorizationService authorization,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (await RequireSchemaWriteAsync(db, user, authorization, cancellationToken) is { } denied)
            return denied;

        var name = Clean(request.Name, NameMaxLength);
        if (name is null)
            return Results.BadRequest(NameRequiredMessage);
        if (await db.AssetTypes.ForTenant(user).AnyAsync(t => t.Name == name, cancellationToken))
            return Results.Conflict(new { error = DuplicateLayoutMessage });

        var inputs = request.Fields ?? [];
        var lists = await LoadListsAsync(db, user, inputs.Select(f => f.OptionListId), cancellationToken);
        var layout = new AssetType
        {
            TenantId = user.TenantId!.Value,
            Name = name,
            Description = Clean(request.Description, 1000),
            Icon = Clean(request.Icon, 100),
            IsPublished = false,
            AvailableToAllCompanies = request.AvailableToAllCompanies,
        };

        for (var i = 0; i < inputs.Count; i++)
        {
            var built = BuildField(inputs[i], i, lists);
            if (built.Error is { } error)
                return error;
            if (layout.Fields.Any(f => string.Equals(f.Name, built.Field!.Name, StringComparison.OrdinalIgnoreCase)))
                return Results.BadRequest(DuplicateFieldMessage);
            layout.Fields.Add(built.Field!);
        }

        db.AssetTypes.Add(layout);
        await db.SaveChangesAsync(cancellationToken);
        await LogAsync(audit, "AssetLayout.Create", nameof(AssetType), layout.Id, $"Created layout '{name}' (draft)", name, cancellationToken);

        return Results.Created($"/api/assets/types/{layout.Id}", await LoadDetailAsync(db, user, layout.Id, cancellationToken));
    }

    public static async Task<IResult> UpdateAsync(
        Guid id,
        [FromBody] UpdateAssetTypeRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IResourceAuthorizationService authorization,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (await RequireSchemaWriteAsync(db, user, authorization, cancellationToken) is { } denied)
            return denied;

        var layout = await LoadLayoutAsync(db, user, id, cancellationToken);
        if (layout is null)
            return Results.NotFound();

        var changes = new List<string>();
        if (request.Name is not null)
        {
            var name = Clean(request.Name, NameMaxLength);
            if (name is null)
                return Results.BadRequest(NameRequiredMessage);
            if (name != layout.Name)
            {
                if (await db.AssetTypes.ForTenant(user).AnyAsync(t => t.Id != id && t.Name == name, cancellationToken))
                    return Results.Conflict(new { error = DuplicateLayoutMessage });
                changes.Add($"renamed from '{layout.Name}'");
                layout.Name = name;
            }
        }

        if (request.Description is not null && Clean(request.Description, 1000) != layout.Description)
        {
            layout.Description = Clean(request.Description, 1000);
            changes.Add("description changed");
        }

        if (request.Icon is not null && Clean(request.Icon, 100) != layout.Icon)
        {
            layout.Icon = Clean(request.Icon, 100);
            changes.Add("icon changed");
        }

        if (request.AvailableToAllCompanies is bool all && all != layout.AvailableToAllCompanies)
        {
            layout.AvailableToAllCompanies = all;
            changes.Add(all ? "available to every company" : "limited to chosen companies");
        }

        if (changes.Count > 0)
        {
            var summary = $"Layout {string.Join(", ", changes)}";
            if (layout.IsPublished)
                await RecordVersionAsync(db, user, layout, layout.Fields, summary, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await LogAsync(audit, "AssetLayout.Update", nameof(AssetType), id, summary, layout.Name, cancellationToken);
        }

        return Results.Ok(await LoadDetailAsync(db, user, id, cancellationToken));
    }

    /// <summary>Only a layout no asset uses — archived ones included — can be deleted.</summary>
    public static async Task<IResult> DeleteAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IResourceAuthorizationService authorization,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (await RequireSchemaWriteAsync(db, user, authorization, cancellationToken) is { } denied)
            return denied;

        var layout = await LoadLayoutAsync(db, user, id, cancellationToken);
        if (layout is null)
            return Results.NotFound();

        var assets = await db.Assets.IgnoreQueryFilters().ForTenant(user).CountAsync(a => a.AssetTypeId == id, cancellationToken);
        if (assets > 0)
            return Results.Conflict(new { error = $"{assets} asset(s) use this layout, including any in the Museum. Move or delete them first.", assets });
        // Values can outlive a move to another layout in older data; they still point at these fields.
        var fieldIds = layout.Fields.Select(f => f.Id).ToList();
        if (fieldIds.Count > 0
            && await db.CustomFieldValues.IgnoreQueryFilters().AnyAsync(v => fieldIds.Contains(v.FieldDefinitionId), cancellationToken))
        {
            return Results.Conflict(new { error = "Some assets still hold values for this layout's fields. Clear those values first." });
        }

        db.AssetTypeCompanyActivations.RemoveRange(
            await db.AssetTypeCompanyActivations.IgnoreQueryFilters().Where(a => a.AssetTypeId == id).ToListAsync(cancellationToken));
        db.AssetTypeVersions.RemoveRange(await db.AssetTypeVersions.Where(v => v.AssetTypeId == id).ToListAsync(cancellationToken));
        db.FieldDefinitions.RemoveRange(layout.Fields);
        db.AssetTypes.Remove(layout);
        await db.SaveChangesAsync(cancellationToken);

        await LogAsync(audit, "AssetLayout.Delete", nameof(AssetType), id, $"Deleted layout '{layout.Name}'", layout.Name, cancellationToken);
        return Results.NoContent();
    }

    public static async Task<IResult> PublishAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IResourceAuthorizationService authorization,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (await RequireSchemaWriteAsync(db, user, authorization, cancellationToken) is { } denied)
            return denied;

        var layout = await LoadLayoutAsync(db, user, id, cancellationToken);
        if (layout is null)
            return Results.NotFound();

        if (!layout.IsPublished)
        {
            var lists = await LoadListsAsync(db, user, layout.Fields.Select(f => f.OptionListId), cancellationToken);
            var problems = AssetFieldRules.Validate(layout.Fields.ToList(), lists);
            if (problems.Count > 0)
                return Invalid(problems);

            layout.IsPublished = true;
            layout.PublishedAt ??= DateTimeOffset.UtcNow;
            await RecordVersionAsync(db, user, layout, layout.Fields, "Published", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await LogAsync(audit, "AssetLayout.Publish", nameof(AssetType), id, $"Published layout '{layout.Name}' as version {layout.CurrentVersion}", layout.Name, cancellationToken);
        }

        return Results.Ok(await LoadDetailAsync(db, user, id, cancellationToken));
    }

    /// <summary>Stops offering the layout for new assets. Existing assets keep it and stay editable.</summary>
    public static async Task<IResult> UnpublishAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IResourceAuthorizationService authorization,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (await RequireSchemaWriteAsync(db, user, authorization, cancellationToken) is { } denied)
            return denied;

        var layout = await LoadLayoutAsync(db, user, id, cancellationToken);
        if (layout is null)
            return Results.NotFound();

        if (layout.IsPublished)
        {
            layout.IsPublished = false;
            await db.SaveChangesAsync(cancellationToken);
            await LogAsync(audit, "AssetLayout.Unpublish", nameof(AssetType), id, $"Unpublished layout '{layout.Name}'", layout.Name, cancellationToken);
        }

        return Results.Ok(await LoadDetailAsync(db, user, id, cancellationToken));
    }

    public static async Task<IResult> AddFieldAsync(
        Guid id,
        [FromBody] AssetTypeFieldRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IResourceAuthorizationService authorization,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (await RequireSchemaWriteAsync(db, user, authorization, cancellationToken) is { } denied)
            return denied;

        var layout = await LoadLayoutAsync(db, user, id, cancellationToken);
        if (layout is null)
            return Results.NotFound();

        var lists = await LoadListsAsync(db, user, layout.Fields.Select(f => f.OptionListId).Append(request.OptionListId), cancellationToken);
        var nextOrder = layout.Fields.Count == 0 ? 0 : layout.Fields.Max(f => f.SortOrder) + 1;
        var built = BuildField(request, nextOrder, lists);
        if (built.Error is { } error)
            return error;
        var field = built.Field!;
        if (layout.Fields.Any(f => string.Equals(f.Name, field.Name, StringComparison.OrdinalIgnoreCase)))
            return Results.Conflict(new { error = DuplicateFieldMessage });
        if (layout.IsPublished && AssetFieldRules.ValidateField(field, lists, requireUsableOptions: true) is { Count: > 0 } problems)
            return Invalid(problems);

        field.AssetTypeId = layout.Id;
        db.FieldDefinitions.Add(field);
        var summary = $"Added field '{field.Name}' ({field.FieldType})";
        if (layout.IsPublished)
        {
            // Fix-up may already have put the new field in layout.Fields; count it once either way.
            var after = layout.Fields.Where(f => f.Id != field.Id).Append(field).ToList();
            await RecordVersionAsync(db, user, layout, after, summary, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);

        await LogAsync(audit, "AssetLayout.AddField", nameof(AssetType), layout.Id, summary, layout.Name, cancellationToken);
        return Results.Created($"/api/assets/fields/{field.Id}", MapField(field, lists));
    }

    /// <summary>
    /// A type or option-list change is only allowed if every value already stored for the field is
    /// still valid under it, so a change can never leave data the field would reject.
    /// </summary>
    public static async Task<IResult> UpdateFieldAsync(
        Guid id,
        [FromBody] UpdateFieldDefinitionRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IResourceAuthorizationService authorization,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (await RequireSchemaWriteAsync(db, user, authorization, cancellationToken) is { } denied)
            return denied;

        var field = await db.FieldDefinitions
            .Where(f => db.AssetTypes.ForTenant(user).Any(t => t.Id == f.AssetTypeId))
            .FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (field is null)
            return Results.NotFound();
        var layout = (await LoadLayoutAsync(db, user, field.AssetTypeId, cancellationToken))!;

        // Work on a copy so a refused change leaves the tracked field untouched.
        var proposed = new FieldDefinition
        {
            Id = field.Id,
            AssetTypeId = field.AssetTypeId,
            Name = field.Name,
            FieldType = field.FieldType,
            IsRequired = request.IsRequired ?? field.IsRequired,
            IsExpiration = request.IsExpiration ?? field.IsExpiration,
            SortOrder = request.SortOrder ?? field.SortOrder,
            Section = request.Section is null ? field.Section : Clean(request.Section, 100),
            HelpText = request.HelpText is null ? field.HelpText : Clean(request.HelpText, 500),
            OptionListId = request.OptionListClear ? null : request.OptionListId ?? field.OptionListId,
        };

        if (request.Name is not null)
        {
            var name = Clean(request.Name, FieldNameMaxLength);
            if (name is null)
                return Results.BadRequest(NameRequiredMessage);
            if (layout.Fields.Any(f => f.Id != id && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
                return Results.Conflict(new { error = DuplicateFieldMessage });
            proposed.Name = name;
        }

        if (request.FieldType is not null)
        {
            if (!AssetFieldType.TryNormalize(request.FieldType, out var type))
                return Results.BadRequest(UnknownFieldTypeMessage);
            proposed.FieldType = type;
        }

        if (!AssetFieldType.UsesOptions(proposed.FieldType))
            proposed.OptionListId = null;

        var lists = await LoadListsAsync(db, user, layout.Fields.Select(f => f.OptionListId).Append(proposed.OptionListId), cancellationToken);
        if (proposed.OptionListId is Guid listId && !lists.ContainsKey(listId))
            return Results.BadRequest(OptionListNotFoundMessage);
        if (AssetFieldRules.ValidateField(proposed, lists, requireUsableOptions: layout.IsPublished) is { Count: > 0 } problems)
            return Invalid(problems);

        var typeChanged = !string.Equals(proposed.FieldType, field.FieldType, StringComparison.Ordinal);
        if (typeChanged || proposed.OptionListId != field.OptionListId)
        {
            var fromOptions = field.OptionListId is Guid oldList && lists.TryGetValue(oldList, out var previous) ? previous : null;
            var toOptions = proposed.OptionListId is Guid newList ? lists[newList] : null;
            // Every stored value, archived assets included: a restored asset must not bring back data
            // the field rejects. Values that survive are rewritten in the new type's stored form.
            var rows = await db.CustomFieldValues.IgnoreQueryFilters()
                .Where(v => v.FieldDefinitionId == id)
                .ToListAsync(cancellationToken);
            var converted = new List<(CustomFieldValue Row, string? Value)>();
            var incompatible = 0;
            foreach (var row in rows)
            {
                if (AssetFieldRules.TryConvertStored(field, fromOptions, proposed, toOptions, row.Value, out var value))
                    converted.Add((row, value));
                else
                    incompatible++;
            }

            if (incompatible > 0)
            {
                return Results.Conflict(new
                {
                    error = $"{incompatible} stored value(s) for '{field.Name}' would not be valid after this change. Clear or fix them first.",
                    incompatible,
                });
            }

            foreach (var (row, value) in converted)
            {
                if (value is null)
                    db.CustomFieldValues.Remove(row);
                else
                    row.Value = value;
            }
        }

        var before = $"{field.Name} ({field.FieldType})";
        field.Name = proposed.Name;
        field.FieldType = proposed.FieldType;
        field.IsRequired = proposed.IsRequired;
        field.IsExpiration = proposed.IsExpiration;
        field.SortOrder = proposed.SortOrder;
        field.Section = proposed.Section;
        field.HelpText = proposed.HelpText;
        field.OptionListId = proposed.OptionListId;

        if (db.Entry(field).State == EntityState.Modified)
        {
            var summary = $"Changed field {before}";
            if (layout.IsPublished)
                await RecordVersionAsync(db, user, layout, layout.Fields, summary, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await LogAsync(audit, "AssetLayout.UpdateField", nameof(AssetType), layout.Id, summary, layout.Name, cancellationToken);
        }

        return Results.Ok(MapField(field, lists));
    }

    /// <summary>A field that holds values cannot be deleted; nor can a published layout's last field.</summary>
    public static async Task<IResult> DeleteFieldAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IResourceAuthorizationService authorization,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (await RequireSchemaWriteAsync(db, user, authorization, cancellationToken) is { } denied)
            return denied;

        var field = await db.FieldDefinitions
            .Where(f => db.AssetTypes.ForTenant(user).Any(t => t.Id == f.AssetTypeId))
            .FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (field is null)
            return Results.NotFound();
        var layout = (await LoadLayoutAsync(db, user, field.AssetTypeId, cancellationToken))!;

        var values = await db.CustomFieldValues.IgnoreQueryFilters().CountAsync(v => v.FieldDefinitionId == id, cancellationToken);
        if (values > 0)
            return Results.Conflict(new { error = $"{values} asset(s) have a value for '{field.Name}'. Clear those values first.", values });
        if (layout.IsPublished && layout.Fields.Count == 1)
            return Results.Conflict(new { error = "A published layout needs at least one field. Unpublish it first." });

        db.FieldDefinitions.Remove(field);
        var summary = $"Removed field '{field.Name}'";
        if (layout.IsPublished)
            await RecordVersionAsync(db, user, layout, layout.Fields.Where(f => f.Id != id).ToList(), summary, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        await LogAsync(audit, "AssetLayout.RemoveField", nameof(AssetType), layout.Id, summary, layout.Name, cancellationToken);
        return Results.NoContent();
    }

    public static async Task<IResult> ListVersionsAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        if (!await db.AssetTypes.ForTenant(user).AnyAsync(t => t.Id == id, cancellationToken))
            return Results.NotFound();

        var versions = await db.AssetTypeVersions.ForTenant(user).AsNoTracking()
            .Where(v => v.AssetTypeId == id)
            .OrderByDescending(v => v.VersionNumber)
            .Select(v => new VersionSummary(v.VersionNumber, v.Summary, v.CreatedByName, v.CreatedAt))
            .ToListAsync(cancellationToken);
        return Results.Ok(versions);
    }

    public static async Task<IResult> GetVersionAsync(
        Guid id,
        int number,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var version = await db.AssetTypeVersions.ForTenant(user).AsNoTracking()
            .FirstOrDefaultAsync(v => v.AssetTypeId == id && v.VersionNumber == number, cancellationToken);
        if (version is null)
            return Results.NotFound();

        using var schema = JsonDocument.Parse(version.SchemaJson);
        return Results.Ok(new VersionDetail(version.VersionNumber, version.Summary, version.CreatedByName, version.CreatedAt, schema.RootElement.Clone()));
    }

    /// <summary>Lets a company use a layout that is not available to every company.</summary>
    public static async Task<IResult> ActivateAsync(
        Guid id,
        Guid companyId,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IResourceAuthorizationService authorization,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (await RequireCompanyChoiceAsync(db, user, authorization, companyId, cancellationToken) is { } denied)
            return denied;

        var layout = await db.AssetTypes.ForTenant(user).FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (layout is null)
            return Results.NotFound();

        if (!await db.AssetTypeCompanyActivations.ForTenant(user).AnyAsync(a => a.AssetTypeId == id && a.CompanyId == companyId, cancellationToken))
        {
            db.AssetTypeCompanyActivations.Add(new AssetTypeCompanyActivation
            {
                TenantId = layout.TenantId,
                AssetTypeId = id,
                CompanyId = companyId,
                ActivatedByObjectId = user.ObjectId,
            });
            await db.SaveChangesAsync(cancellationToken);
            await LogAsync(audit, "AssetLayout.ActivateCompany", nameof(AssetType), id, $"Layout '{layout.Name}' enabled for a company", layout.Name, cancellationToken);
        }

        return Results.Ok(await LoadDetailAsync(db, user, id, cancellationToken));
    }

    /// <summary>New assets of the layout can no longer be created for the company; existing ones are kept.</summary>
    public static async Task<IResult> DeactivateAsync(
        Guid id,
        Guid companyId,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IResourceAuthorizationService authorization,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (await RequireCompanyChoiceAsync(db, user, authorization, companyId, cancellationToken) is { } denied)
            return denied;

        var layout = await db.AssetTypes.ForTenant(user).FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (layout is null)
            return Results.NotFound();

        var activation = await db.AssetTypeCompanyActivations.ForTenant(user)
            .FirstOrDefaultAsync(a => a.AssetTypeId == id && a.CompanyId == companyId, cancellationToken);
        if (activation is not null)
        {
            db.AssetTypeCompanyActivations.Remove(activation);
            await db.SaveChangesAsync(cancellationToken);
            await LogAsync(audit, "AssetLayout.DeactivateCompany", nameof(AssetType), id, $"Layout '{layout.Name}' disabled for a company", layout.Name, cancellationToken);
        }

        return Results.Ok(await LoadDetailAsync(db, user, id, cancellationToken));
    }

    /// <summary>
    /// Null when <paramref name="layout"/> can be used for a new asset in <paramref name="companyId"/>
    /// (null = a tenant-wide asset); otherwise the 400 to return.
    /// </summary>
    public static async Task<IResult?> EnsureUsableAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        AssetType layout,
        Guid? companyId,
        CancellationToken cancellationToken = default)
    {
        if (!layout.IsPublished)
            return Results.BadRequest($"The layout '{layout.Name}' is a draft. Publish it before creating assets with it.");
        return await EnsureAvailableAsync(db, user, layout, companyId, cancellationToken);
    }

    /// <summary>
    /// Null when <paramref name="layout"/> may be used in <paramref name="companyId"/>, published or
    /// not — the check for moving an existing asset to another company.
    /// </summary>
    public static async Task<IResult?> EnsureAvailableAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        AssetType layout,
        Guid? companyId,
        CancellationToken cancellationToken = default)
    {
        if (layout.AvailableToAllCompanies)
            return null;
        if (companyId is not Guid id)
            return Results.BadRequest($"The layout '{layout.Name}' is limited to chosen companies, so it cannot be used for a tenant-wide asset.");

        var enabled = await db.AssetTypeCompanyActivations.IgnoreQueryFilters().ForTenant(user)
            .AnyAsync(a => a.AssetTypeId == layout.Id && a.CompanyId == id, cancellationToken);
        return enabled ? null : Results.BadRequest($"The layout '{layout.Name}' is not enabled for this company.");
    }

    internal static async Task<Dictionary<Guid, OptionList>> LoadListsAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IEnumerable<Guid?> ids,
        CancellationToken cancellationToken)
    {
        var wanted = ids.OfType<Guid>().Distinct().ToList();
        if (wanted.Count == 0)
            return new Dictionary<Guid, OptionList>();

        return await db.OptionLists.ForTenant(user)
            .Include(l => l.Items)
            .Where(l => wanted.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, cancellationToken);
    }

    private static async Task<AssetType?> LoadLayoutAsync(DocuEngAIneDbContext db, ICurrentUser user, Guid id, CancellationToken cancellationToken)
        => await db.AssetTypes.ForTenant(user)
            .Include(t => t.Fields)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

    private static async Task<LayoutDetail?> LoadDetailAsync(DocuEngAIneDbContext db, ICurrentUser user, Guid id, CancellationToken cancellationToken)
    {
        var layout = await db.AssetTypes.ForTenant(user).AsNoTracking()
            .Include(t => t.Fields).ThenInclude(f => f.OptionList)
            .Include(t => t.CompanyActivations)
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (layout is null)
            return null;

        // Company names come through the scoped Companies set, so a restricted caller sees only theirs.
        var companies = await db.AssetTypeCompanyActivations.ForTenant(user).AsNoTracking()
            .Where(a => a.AssetTypeId == id)
            .Join(db.Companies.ForTenant(user), a => a.CompanyId, c => c.Id, (a, c) => new { a.CompanyId, c.Name, a.CreatedAt })
            .ToListAsync(cancellationToken);
        var assetCount = await db.Assets.ForTenant(user).CountAsync(a => a.AssetTypeId == id, cancellationToken);
        var lists = await LoadListsAsync(db, user, layout.Fields.Select(f => f.OptionListId), cancellationToken);

        return new LayoutDetail(
            MapSummary(layout),
            companies
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => new ActivationView(c.CompanyId, c.Name, c.CreatedAt))
                .ToList(),
            assetCount,
            AssetFieldRules.Validate(layout.Fields.ToList(), lists));
    }

    private static async Task RecordVersionAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        AssetType layout,
        IEnumerable<FieldDefinition> fields,
        string summary,
        CancellationToken cancellationToken)
    {
        var fieldList = fields.ToList();
        var lists = await LoadListsAsync(db, user, fieldList.Select(f => f.OptionListId), cancellationToken);
        layout.CurrentVersion += 1;
        db.AssetTypeVersions.Add(new AssetTypeVersion
        {
            TenantId = layout.TenantId,
            AssetTypeId = layout.Id,
            VersionNumber = layout.CurrentVersion,
            SchemaJson = AssetFieldRules.Snapshot(layout, fieldList, lists),
            Summary = summary.Length <= 500 ? summary : summary[..500],
            CreatedByObjectId = user.ObjectId,
            CreatedByName = user.DisplayName ?? user.Email,
        });
    }

    private static (FieldDefinition? Field, IResult? Error) BuildField(
        AssetTypeFieldRequest input,
        int sortOrder,
        IReadOnlyDictionary<Guid, OptionList> lists)
    {
        var name = Clean(input.Name, FieldNameMaxLength);
        if (name is null)
            return (null, Results.BadRequest("Every field needs a name."));
        if (!AssetFieldType.TryNormalize(input.Type, out var type))
            return (null, Results.BadRequest($"{UnknownFieldTypeMessage} Use one of: {string.Join(", ", AssetFieldType.All)}."));
        var optionListId = AssetFieldType.UsesOptions(type) ? input.OptionListId : null;
        if (optionListId is Guid listId && !lists.ContainsKey(listId))
            return (null, Results.BadRequest(OptionListNotFoundMessage));

        var field = new FieldDefinition
        {
            Name = name,
            FieldType = type,
            IsRequired = input.IsRequired,
            IsExpiration = input.IsExpiration,
            SortOrder = sortOrder,
            Section = Clean(input.Section, 100),
            HelpText = Clean(input.HelpText, 500),
            OptionListId = optionListId,
        };
        var problems = AssetFieldRules.ValidateField(field, lists, requireUsableOptions: false);
        if (problems.Count > 0)
            return (null, Invalid(problems));
        return (field, null);
    }

    private static async Task<IResult?> RequireSchemaWriteAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IResourceAuthorizationService authorization,
        CancellationToken cancellationToken)
    {
        // Layouts are tenant-wide schema: no grant can name them, and a company-restricted user cannot change them.
        if (await ResourceWriteGuard.RequireTenantWriteAsync(authorization, user, ResourceType.Asset, cancellationToken) is { } denied)
            return denied;
        return CompanyEndpoints.RequireCompanyAccess(db, null, CompanyAccessLevel.Edit);
    }

    private static async Task<IResult?> RequireCompanyChoiceAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IResourceAuthorizationService authorization,
        Guid companyId,
        CancellationToken cancellationToken)
    {
        if (await ResourceWriteGuard.RequireTenantWriteAsync(authorization, user, ResourceType.Asset, cancellationToken) is { } denied)
            return denied;
        if (!await db.Companies.ForTenant(user).AnyAsync(c => c.Id == companyId, cancellationToken))
            return Results.NotFound(CompanyNotFoundMessage);
        // Which layouts a company uses is its configuration: Manage, as in Docuengine.
        return CompanyEndpoints.RequireCompanyAccess(db, companyId, CompanyAccessLevel.Manage);
    }

    private static IResult Invalid(IReadOnlyList<AssetFieldRules.LayoutProblem> problems)
        => Results.Json(
            new { error = problems.Count == 1 ? problems[0].Message : InvalidLayoutMessage, problems },
            statusCode: StatusCodes.Status409Conflict);

    private static LayoutSummary MapSummary(AssetType t) => new(
        t.Id,
        t.Name,
        t.Description,
        t.Icon,
        t.IsPublished,
        t.PublishedAt,
        t.AvailableToAllCompanies,
        t.CurrentVersion,
        t.Fields
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new FieldView(f.Id, f.Name, f.FieldType, f.IsRequired, f.IsExpiration, f.SortOrder, f.Section, f.HelpText, f.OptionListId, f.OptionList?.Name))
            .ToList(),
        t.CompanyActivations.Select(a => a.CompanyId).ToList());

    private static FieldView MapField(FieldDefinition f, IReadOnlyDictionary<Guid, OptionList> lists) => new(
        f.Id,
        f.Name,
        f.FieldType,
        f.IsRequired,
        f.IsExpiration,
        f.SortOrder,
        f.Section,
        f.HelpText,
        f.OptionListId,
        f.OptionListId is Guid id && lists.TryGetValue(id, out var list) ? list.Name : null);

    private static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static Task LogAsync(
        IAuditService? audit,
        string action,
        string entityType,
        Guid entityId,
        string details,
        string? label,
        CancellationToken cancellationToken)
        => audit is null
            ? Task.CompletedTask
            : audit.LogAsync(new AuditEntry(action, entityType, entityId, details, AuditCategories.Resource, label), cancellationToken);
}
