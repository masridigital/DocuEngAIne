using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Assets;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

/// <summary>
/// Option lists: reusable choices for Select and MultiSelect fields, shared across layouts. Stored
/// values are the option's <see cref="OptionListItem.Value"/>, fixed when the option is created, so
/// labels can be renamed freely. Options are retired by deactivating them, never deleted, so no
/// stored value is orphaned; and nothing may leave a published layout's choice field with no active
/// option to pick.
/// </summary>
public static class OptionListEndpoints
{
    public const string NameRequiredMessage = "Name is required.";
    public const string LabelRequiredMessage = "Every option needs a label.";
    public const string DuplicateNameMessage = "An option list with that name already exists.";

    public static IEndpointRouteBuilder MapOptionListEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/option-lists").RequireAuthorization();
        group.MapGet("", ListAsync);
        group.MapPost("", CreateAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPut("/{id:guid}", UpdateAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);
        group.MapPost("/{id:guid}/items", AddItemAsync);
        group.MapPut("/{id:guid}/items/{itemId:guid}", UpdateItemAsync);
        return app;
    }

    public sealed record OptionItemInput(string? Label, string? Value = null);
    public sealed record CreateOptionListRequest(string? Name, string? Description = null, IReadOnlyList<OptionItemInput>? Items = null);
    public sealed record UpdateOptionListRequest(string? Name = null, string? Description = null, bool? IsActive = null);
    public sealed record UpdateOptionItemRequest(string? Label = null, int? SortOrder = null, bool? IsActive = null);

    public sealed record OptionItemView(Guid Id, string Label, string Value, int SortOrder, bool IsActive);

    /// <param name="UsedBy">"Layout › Field" for every field that draws on the list.</param>
    public sealed record OptionListView(
        Guid Id,
        string Name,
        string? Description,
        bool IsActive,
        IReadOnlyList<OptionItemView> Items,
        IReadOnlyList<string> UsedBy);

    public static async Task<IResult> ListAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var lists = await db.OptionLists.ForTenant(user).AsNoTracking()
            .Include(l => l.Items)
            .OrderBy(l => l.Name)
            .ToListAsync(cancellationToken);
        var usage = await UsageAsync(db, user, lists.Select(l => l.Id).ToList(), cancellationToken);
        return Results.Ok(lists.Select(l => Map(l, usage)).ToList());
    }

    public static async Task<IResult> GetAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var view = await LoadViewAsync(db, user, id, cancellationToken);
        return view is null ? Results.NotFound() : Results.Ok(view);
    }

    public static async Task<IResult> CreateAsync(
        [FromBody] CreateOptionListRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IResourceAuthorizationService authorization,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (await RequireWriteAsync(db, user, authorization, cancellationToken) is { } denied)
            return denied;

        var name = Clean(request.Name, 100);
        if (name is null)
            return Results.BadRequest(NameRequiredMessage);
        if (await db.OptionLists.ForTenant(user).AnyAsync(l => l.Name == name, cancellationToken))
            return Results.Conflict(new { error = DuplicateNameMessage });

        var list = new OptionList { TenantId = user.TenantId!.Value, Name = name, Description = Clean(request.Description, 500) };
        foreach (var input in request.Items ?? [])
        {
            if (AddItem(list, input).Error is { } error)
                return error;
        }

        db.OptionLists.Add(list);
        await db.SaveChangesAsync(cancellationToken);
        await LogAsync(audit, "OptionList.Create", list.Id, $"Created option list '{name}' with {list.Items.Count} option(s)", name, cancellationToken);
        return Results.Created($"/api/option-lists/{list.Id}", await LoadViewAsync(db, user, list.Id, cancellationToken));
    }

    public static async Task<IResult> UpdateAsync(
        Guid id,
        [FromBody] UpdateOptionListRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IResourceAuthorizationService authorization,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (await RequireWriteAsync(db, user, authorization, cancellationToken) is { } denied)
            return denied;

        var list = await db.OptionLists.ForTenant(user).Include(l => l.Items).FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (list is null)
            return Results.NotFound();

        var changes = new List<string>();
        if (request.Name is not null)
        {
            var name = Clean(request.Name, 100);
            if (name is null)
                return Results.BadRequest(NameRequiredMessage);
            if (name != list.Name)
            {
                if (await db.OptionLists.ForTenant(user).AnyAsync(l => l.Id != id && l.Name == name, cancellationToken))
                    return Results.Conflict(new { error = DuplicateNameMessage });
                changes.Add($"renamed from '{list.Name}'");
                list.Name = name;
            }
        }

        if (request.Description is not null && Clean(request.Description, 500) != list.Description)
        {
            list.Description = Clean(request.Description, 500);
            changes.Add("description changed");
        }

        if (request.IsActive is bool active && active != list.IsActive)
        {
            if (!active && await BlockingLayoutsAsync(db, user, id, cancellationToken) is { Count: > 0 } layouts)
                return InUse(layouts);
            list.IsActive = active;
            changes.Add(active ? "activated" : "deactivated");
        }

        if (changes.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            await LogAsync(audit, "OptionList.Update", id, $"Option list {string.Join(", ", changes)}", list.Name, cancellationToken);
        }

        return Results.Ok(await LoadViewAsync(db, user, id, cancellationToken));
    }

    /// <summary>Only a list no field uses can be deleted.</summary>
    public static async Task<IResult> DeleteAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IResourceAuthorizationService authorization,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (await RequireWriteAsync(db, user, authorization, cancellationToken) is { } denied)
            return denied;

        var list = await db.OptionLists.ForTenant(user).Include(l => l.Items).FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (list is null)
            return Results.NotFound();

        var usage = await UsageAsync(db, user, [id], cancellationToken);
        if (usage.TryGetValue(id, out var usedBy) && usedBy.Count > 0)
            return Results.Conflict(new { error = $"Used by {string.Join(", ", usedBy)}. Point those fields at another list first.", usedBy });

        db.OptionListItems.RemoveRange(list.Items);
        db.OptionLists.Remove(list);
        await db.SaveChangesAsync(cancellationToken);
        await LogAsync(audit, "OptionList.Delete", id, $"Deleted option list '{list.Name}'", list.Name, cancellationToken);
        return Results.NoContent();
    }

    public static async Task<IResult> AddItemAsync(
        Guid id,
        [FromBody] OptionItemInput request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IResourceAuthorizationService authorization,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (await RequireWriteAsync(db, user, authorization, cancellationToken) is { } denied)
            return denied;

        var list = await db.OptionLists.ForTenant(user).Include(l => l.Items).FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (list is null)
            return Results.NotFound();

        var added = AddItem(list, request);
        if (added.Error is { } error)
            return error;
        var item = added.Item!;
        db.OptionListItems.Add(item);
        await db.SaveChangesAsync(cancellationToken);

        await LogAsync(audit, "OptionList.AddOption", id, $"Added option '{item.Label}' ({item.Value})", list.Name, cancellationToken);
        return Results.Created($"/api/option-lists/{id}/items/{item.Id}", MapItem(item));
    }

    public static async Task<IResult> UpdateItemAsync(
        Guid id,
        Guid itemId,
        [FromBody] UpdateOptionItemRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IResourceAuthorizationService authorization,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (await RequireWriteAsync(db, user, authorization, cancellationToken) is { } denied)
            return denied;

        var list = await db.OptionLists.ForTenant(user).Include(l => l.Items).FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
        var item = list?.Items.FirstOrDefault(i => i.Id == itemId);
        if (list is null || item is null)
            return Results.NotFound();

        var changes = new List<string>();
        if (request.Label is not null)
        {
            var label = Clean(request.Label, 100);
            if (label is null)
                return Results.BadRequest(LabelRequiredMessage);
            if (label != item.Label)
            {
                changes.Add($"'{item.Label}' renamed to '{label}'");
                item.Label = label;
            }
        }

        if (request.SortOrder is int order && order != item.SortOrder)
        {
            item.SortOrder = order;
            changes.Add($"'{item.Label}' moved");
        }

        if (request.IsActive is bool active && active != item.IsActive)
        {
            // Retiring the last active option would leave published choice fields with nothing to pick.
            if (!active
                && !list.Items.Any(i => i.Id != itemId && i.IsActive)
                && await BlockingLayoutsAsync(db, user, id, cancellationToken) is { Count: > 0 } layouts)
            {
                return InUse(layouts);
            }

            item.IsActive = active;
            changes.Add($"'{item.Label}' {(active ? "activated" : "deactivated")}");
        }

        if (changes.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            await LogAsync(audit, "OptionList.UpdateOption", id, string.Join(", ", changes), list.Name, cancellationToken);
        }

        return Results.Ok(MapItem(item));
    }

    /// <summary>Adds an option with a value unique in the list, derived from the requested value or the label.</summary>
    private static (OptionListItem? Item, IResult? Error) AddItem(OptionList list, OptionItemInput input)
    {
        var label = Clean(input.Label, 100);
        if (label is null)
            return (null, Results.BadRequest(LabelRequiredMessage));

        var baseValue = AssetFieldRules.OptionValueFrom(string.IsNullOrWhiteSpace(input.Value) ? label : input.Value);
        var value = baseValue;
        for (var n = 2; list.Items.Any(i => string.Equals(i.Value, value, StringComparison.OrdinalIgnoreCase)); n++)
            value = $"{baseValue}-{n}";

        var item = new OptionListItem
        {
            OptionListId = list.Id,
            Label = label,
            Value = value,
            SortOrder = list.Items.Count == 0 ? 0 : list.Items.Max(i => i.SortOrder) + 1,
        };
        list.Items.Add(item);
        return (item, null);
    }

    /// <summary>Published layouts with a choice field on the list — the ones that would break if it offered nothing.</summary>
    private static async Task<List<string>> BlockingLayoutsAsync(DocuEngAIneDbContext db, ICurrentUser user, Guid listId, CancellationToken cancellationToken)
        => await db.FieldDefinitions
            .Where(f => f.OptionListId == listId
                && (f.FieldType == AssetFieldType.Select || f.FieldType == AssetFieldType.MultiSelect)
                && db.AssetTypes.ForTenant(user).Any(t => t.Id == f.AssetTypeId && t.IsPublished))
            .Select(f => f.AssetType.Name + " › " + f.Name)
            .Distinct()
            .ToListAsync(cancellationToken);

    private static IResult InUse(IReadOnlyList<string> layouts)
        => Results.Conflict(new
        {
            error = $"Published layouts need at least one active option here: {string.Join(", ", layouts)}. Unpublish them or change those fields first.",
            layouts,
        });

    private static async Task<Dictionary<Guid, List<string>>> UsageAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        List<Guid> listIds,
        CancellationToken cancellationToken)
    {
        var rows = await db.FieldDefinitions
            .Where(f => f.OptionListId != null
                && listIds.Contains(f.OptionListId.Value)
                && db.AssetTypes.ForTenant(user).Any(t => t.Id == f.AssetTypeId))
            .Select(f => new { ListId = f.OptionListId!.Value, Use = f.AssetType.Name + " › " + f.Name })
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(r => r.ListId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Use).OrderBy(u => u, StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static async Task<OptionListView?> LoadViewAsync(DocuEngAIneDbContext db, ICurrentUser user, Guid id, CancellationToken cancellationToken)
    {
        var list = await db.OptionLists.ForTenant(user).AsNoTracking().Include(l => l.Items).FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (list is null)
            return null;
        return Map(list, await UsageAsync(db, user, [id], cancellationToken));
    }

    private static async Task<IResult?> RequireWriteAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IResourceAuthorizationService authorization,
        CancellationToken cancellationToken)
    {
        // Option lists are tenant-wide asset schema, gated like layouts.
        if (await ResourceWriteGuard.RequireTenantWriteAsync(authorization, user, ResourceType.Asset, cancellationToken) is { } denied)
            return denied;
        return CompanyEndpoints.RequireCompanyAccess(db, null, CompanyAccessLevel.Edit);
    }

    private static OptionListView Map(OptionList list, IReadOnlyDictionary<Guid, List<string>> usage) => new(
        list.Id,
        list.Name,
        list.Description,
        list.IsActive,
        list.Items.OrderBy(i => i.SortOrder).ThenBy(i => i.Label, StringComparer.OrdinalIgnoreCase).Select(MapItem).ToList(),
        usage.TryGetValue(list.Id, out var usedBy) ? usedBy : []);

    private static OptionItemView MapItem(OptionListItem i) => new(i.Id, i.Label, i.Value, i.SortOrder, i.IsActive);

    private static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static Task LogAsync(IAuditService? audit, string action, Guid listId, string details, string? label, CancellationToken cancellationToken)
        => audit is null
            ? Task.CompletedTask
            : audit.LogAsync(new AuditEntry(action, nameof(OptionList), listId, details, AuditCategories.Resource, label), cancellationToken);
}
