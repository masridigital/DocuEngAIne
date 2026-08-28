using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

public static class FlagEndpoints
{
    public const string EntityNotFoundMessage = "Entity not found.";
    public const string UnknownEntityTypeMessage = "Unknown entity type.";
    public const string InvalidColorMessage = "Color must be a hex value such as #DC2626.";

    public static IEndpointRouteBuilder MapFlagEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/flags").RequireAuthorization();

        group.MapGet("", async (
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var items = await ListDefinitionsAsync(db, user, cancellationToken);
            return Results.Ok(items);
        });

        group.MapPost("", async (
            [FromBody] CreateFlagDefinitionRequest request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
            await CreateDefinitionAsync(request, db, user, cancellationToken));

        group.MapGet("/review", async (
            [FromQuery] string? entityType,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            if (!string.IsNullOrWhiteSpace(entityType) && !FlagEntityType.TryNormalize(entityType, out _))
                return Results.BadRequest(UnknownEntityTypeMessage);

            var items = await QueryReviewAsync(db, user, entityType, cancellationToken);
            return Results.Ok(items);
        });

        group.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] UpdateFlagDefinitionRequest request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
            await UpdateDefinitionAsync(id, request, db, user, cancellationToken));

        group.MapDelete("/{id:guid}", async (
            Guid id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
            await DeleteDefinitionAsync(id, db, user, cancellationToken));

        group.MapPost("/{id:guid}/assign", async (
            Guid id,
            [FromBody] AssignFlagRequest request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
            await AssignAsync(id, request, db, user, cancellationToken));

        group.MapDelete("/{id:guid}/assign/{entityType}/{entityId:guid}", async (
            Guid id,
            string entityType,
            Guid entityId,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
            await UnassignAsync(id, entityType, entityId, db, user, cancellationToken));

        return app;
    }

    public static async Task<IReadOnlyList<FlagDefinitionItem>> ListDefinitionsAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var flags = await db.FlagDefinitions.ForTenant(user).AsNoTracking()
            .OrderBy(f => f.Name)
            .ToListAsync(cancellationToken);
        return flags.Select(MapDefinition).ToList();
    }

    public static async Task<IResult> CreateDefinitionAsync(
        CreateFlagDefinitionRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        var name = request.Name?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(name))
            return Results.BadRequest("Name is required.");
        if (!TryNormalizeColor(request.Color, out var color))
            return Results.BadRequest(InvalidColorMessage);

        if (await db.FlagDefinitions.ForTenant(user).AnyAsync(f => f.Name == name, cancellationToken))
            return Results.Conflict("Flag name already exists.");

        var flag = new FlagDefinition
        {
            TenantId = user.TenantId.Value,
            Name = name,
            Color = color,
            IsActive = request.IsActive ?? true,
        };
        db.FlagDefinitions.Add(flag);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/flags/{flag.Id}", MapDefinition(flag));
    }

    public static async Task<IResult> UpdateDefinitionAsync(
        Guid id,
        UpdateFlagDefinitionRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var flag = await db.FlagDefinitions.ForTenant(user).FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (flag is null)
            return Results.NotFound();

        if (request.Name is not null)
        {
            var name = request.Name.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest("Name is required.");
            if (await db.FlagDefinitions.ForTenant(user).AnyAsync(f => f.Id != id && f.Name == name, cancellationToken))
                return Results.Conflict("Flag name already exists.");
            flag.Name = name;
        }

        if (request.Color is not null)
        {
            if (!TryNormalizeColor(request.Color, out var color))
                return Results.BadRequest(InvalidColorMessage);
            flag.Color = color;
        }

        if (request.IsActive.HasValue)
            flag.IsActive = request.IsActive.Value;

        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    public static async Task<IResult> DeleteDefinitionAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var flag = await db.FlagDefinitions.ForTenant(user).FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (flag is null)
            return Results.NotFound();

        db.FlagDefinitions.Remove(flag);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    public static async Task<IResult> AssignAsync(
        Guid id,
        AssignFlagRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        var flag = await db.FlagDefinitions.ForTenant(user).FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (flag is null)
            return Results.NotFound();

        if (!FlagEntityType.TryNormalize(request.EntityType, out var entityType))
            return Results.BadRequest(UnknownEntityTypeMessage);

        var exists = await EntityExistsInTenantAsync(db, user, entityType, request.EntityId, cancellationToken);
        if (!exists)
            return Results.BadRequest(EntityNotFoundMessage);

        var already = await db.FlagAssignments.ForTenant(user).AnyAsync(
            a => a.FlagDefinitionId == id && a.EntityType == entityType && a.EntityId == request.EntityId,
            cancellationToken);
        if (already)
            return Results.Conflict("Flag already assigned.");

        var assignment = new FlagAssignment
        {
            TenantId = user.TenantId.Value,
            FlagDefinitionId = flag.Id,
            EntityType = entityType,
            EntityId = request.EntityId,
        };
        db.FlagAssignments.Add(assignment);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/flags/{flag.Id}/assign/{entityType}/{request.EntityId}", MapAssignment(assignment, flag));
    }

    public static async Task<IResult> UnassignAsync(
        Guid id,
        string entityTypeRaw,
        Guid entityId,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        if (!FlagEntityType.TryNormalize(entityTypeRaw, out var entityType))
            return Results.BadRequest(UnknownEntityTypeMessage);

        var flag = await db.FlagDefinitions.ForTenant(user).FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (flag is null)
            return Results.NotFound();

        var assignment = await db.FlagAssignments.ForTenant(user)
            .FirstOrDefaultAsync(
                a => a.FlagDefinitionId == id && a.EntityType == entityType && a.EntityId == entityId,
                cancellationToken);
        if (assignment is null)
            return Results.NotFound();

        db.FlagAssignments.Remove(assignment);
        await db.SaveChangesAsync(cancellationToken);
        return Results.NoContent();
    }

    public static async Task<IReadOnlyList<FlagReviewItem>> QueryReviewAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        string? entityType = null,
        CancellationToken cancellationToken = default)
    {
        string? typeFilter = null;
        if (!string.IsNullOrWhiteSpace(entityType) && FlagEntityType.TryNormalize(entityType, out var normalized))
            typeFilter = normalized;

        var query = db.FlagAssignments.ForTenant(user).AsNoTracking()
            .Include(a => a.FlagDefinition)
            .AsQueryable();
        if (typeFilter is not null)
            query = query.Where(a => a.EntityType == typeFilter);

        var assignments = await query
            .OrderByDescending(a => a.CreatedAt)
            .ThenBy(a => a.FlagDefinition.Name)
            .ToListAsync(cancellationToken);

        if (assignments.Count == 0)
            return [];

        var names = await LoadEntityNamesAsync(db, user, assignments, cancellationToken);
        var items = new List<FlagReviewItem>(assignments.Count);
        foreach (var assignment in assignments)
        {
            if (!names.TryGetValue((assignment.EntityType, assignment.EntityId), out var named))
                continue;
            items.Add(new FlagReviewItem(
                assignment.Id,
                assignment.FlagDefinitionId,
                assignment.FlagDefinition.Name,
                assignment.FlagDefinition.Color,
                assignment.EntityType,
                assignment.EntityId,
                named.Name,
                named.CompanyId,
                named.CompanyName,
                assignment.CreatedAt));
        }

        return items;
    }

    public static async Task<bool> EntityExistsInTenantAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        string entityType,
        Guid entityId,
        CancellationToken cancellationToken = default)
    {
        return entityType switch
        {
            FlagEntityType.Company => await db.Companies.ForTenant(user).AnyAsync(c => c.Id == entityId, cancellationToken),
            FlagEntityType.Asset => await db.Assets.ForTenant(user).AnyAsync(a => a.Id == entityId, cancellationToken),
            FlagEntityType.Document => await db.Documents.ForTenant(user).AnyAsync(d => d.Id == entityId, cancellationToken),
            FlagEntityType.Runbook => await db.Runbooks.ForTenant(user).AnyAsync(r => r.Id == entityId, cancellationToken),
            FlagEntityType.KeeperLink => await db.KeeperLinks.ForTenant(user).AnyAsync(k => k.Id == entityId, cancellationToken),
            _ => false,
        };
    }

    internal static bool TryNormalizeColor(string? color, out string hex)
    {
        hex = "";
        if (string.IsNullOrWhiteSpace(color))
            return false;
        var raw = color.Trim();
        if (!raw.StartsWith('#'))
            raw = "#" + raw;
        if (raw.Length == 4 && IsHex(raw.AsSpan(1)))
        {
            hex = string.Concat("#",
                char.ToUpperInvariant(raw[1]), char.ToUpperInvariant(raw[1]),
                char.ToUpperInvariant(raw[2]), char.ToUpperInvariant(raw[2]),
                char.ToUpperInvariant(raw[3]), char.ToUpperInvariant(raw[3]));
            return true;
        }
        if (raw.Length == 7 && IsHex(raw.AsSpan(1)))
        {
            hex = "#" + raw[1..].ToUpperInvariant();
            return true;
        }
        return false;
    }

    private static bool IsHex(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }
        return true;
    }

    private static async Task<Dictionary<(string Type, Guid Id), NamedEntity>> LoadEntityNamesAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IReadOnlyList<FlagAssignment> assignments,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<(string Type, Guid Id), NamedEntity>();
        var companyIds = assignments.Where(a => a.EntityType == FlagEntityType.Company).Select(a => a.EntityId).Distinct().ToList();
        var assetIds = assignments.Where(a => a.EntityType == FlagEntityType.Asset).Select(a => a.EntityId).Distinct().ToList();
        var documentIds = assignments.Where(a => a.EntityType == FlagEntityType.Document).Select(a => a.EntityId).Distinct().ToList();
        var runbookIds = assignments.Where(a => a.EntityType == FlagEntityType.Runbook).Select(a => a.EntityId).Distinct().ToList();
        var keeperIds = assignments.Where(a => a.EntityType == FlagEntityType.KeeperLink).Select(a => a.EntityId).Distinct().ToList();

        var relatedCompanyIds = new HashSet<Guid>();

        if (companyIds.Count > 0)
        {
            var companies = await db.Companies.ForTenant(user).AsNoTracking()
                .Where(c => companyIds.Contains(c.Id))
                .Select(c => new { c.Id, c.Name })
                .ToListAsync(cancellationToken);
            foreach (var c in companies)
                result[(FlagEntityType.Company, c.Id)] = new NamedEntity(c.Name, c.Id, c.Name);
        }

        if (assetIds.Count > 0)
        {
            var assets = await db.Assets.ForTenant(user).AsNoTracking()
                .Where(a => assetIds.Contains(a.Id))
                .Select(a => new { a.Id, a.Name, a.CompanyId })
                .ToListAsync(cancellationToken);
            foreach (var a in assets)
            {
                result[(FlagEntityType.Asset, a.Id)] = new NamedEntity(a.Name, a.CompanyId, null);
                if (a.CompanyId is Guid cid)
                    relatedCompanyIds.Add(cid);
            }
        }

        if (documentIds.Count > 0)
        {
            var docs = await db.Documents.ForTenant(user).AsNoTracking()
                .Where(d => documentIds.Contains(d.Id))
                .Select(d => new { d.Id, d.Title, d.CompanyId })
                .ToListAsync(cancellationToken);
            foreach (var d in docs)
            {
                result[(FlagEntityType.Document, d.Id)] = new NamedEntity(d.Title, d.CompanyId, null);
                if (d.CompanyId is Guid cid)
                    relatedCompanyIds.Add(cid);
            }
        }

        if (runbookIds.Count > 0)
        {
            var runbooks = await db.Runbooks.ForTenant(user).AsNoTracking()
                .Where(r => runbookIds.Contains(r.Id))
                .Select(r => new { r.Id, r.Title, r.CompanyId })
                .ToListAsync(cancellationToken);
            foreach (var r in runbooks)
            {
                result[(FlagEntityType.Runbook, r.Id)] = new NamedEntity(r.Title, r.CompanyId, null);
                if (r.CompanyId is Guid cid)
                    relatedCompanyIds.Add(cid);
            }
        }

        if (keeperIds.Count > 0)
        {
            var keepers = await db.KeeperLinks.ForTenant(user).AsNoTracking()
                .Where(k => keeperIds.Contains(k.Id))
                .Select(k => new { k.Id, k.Name, k.CompanyId })
                .ToListAsync(cancellationToken);
            foreach (var k in keepers)
            {
                result[(FlagEntityType.KeeperLink, k.Id)] = new NamedEntity(k.Name, k.CompanyId, null);
                if (k.CompanyId is Guid cid)
                    relatedCompanyIds.Add(cid);
            }
        }

        Dictionary<Guid, string> companyNames = result
            .Where(kv => kv.Key.Type == FlagEntityType.Company)
            .ToDictionary(kv => kv.Key.Id, kv => kv.Value.Name);
        relatedCompanyIds.ExceptWith(companyNames.Keys);
        if (relatedCompanyIds.Count > 0)
        {
            var extra = await db.Companies.ForTenant(user).AsNoTracking()
                .Where(c => relatedCompanyIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
            foreach (var pair in extra)
                companyNames[pair.Key] = pair.Value;
        }

        foreach (var key in result.Keys.ToList())
        {
            if (key.Type == FlagEntityType.Company)
                continue;
            var named = result[key];
            string? companyName = null;
            if (named.CompanyId is Guid cid && companyNames.TryGetValue(cid, out var n))
                companyName = n;
            result[key] = named with { CompanyName = companyName };
        }

        return result;
    }

    private static FlagDefinitionItem MapDefinition(FlagDefinition f) =>
        new(f.Id, f.Name, f.Color, f.IsActive, f.CreatedAt, f.UpdatedAt);

    private static FlagAssignmentItem MapAssignment(FlagAssignment a, FlagDefinition f) =>
        new(a.Id, f.Id, f.Name, f.Color, a.EntityType, a.EntityId, a.CreatedAt);

    private readonly record struct NamedEntity(string Name, Guid? CompanyId, string? CompanyName);
}

public sealed record FlagDefinitionItem(
    Guid Id,
    string Name,
    string Color,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record FlagAssignmentItem(
    Guid Id,
    Guid FlagDefinitionId,
    string FlagName,
    string FlagColor,
    string EntityType,
    Guid EntityId,
    DateTimeOffset CreatedAt);

public sealed record FlagReviewItem(
    Guid AssignmentId,
    Guid FlagDefinitionId,
    string FlagName,
    string FlagColor,
    string EntityType,
    Guid EntityId,
    string EntityName,
    Guid? CompanyId,
    string? CompanyName,
    DateTimeOffset CreatedAt);

public record CreateFlagDefinitionRequest(string Name, string Color, bool? IsActive = null);

public record UpdateFlagDefinitionRequest(string? Name = null, string? Color = null, bool? IsActive = null);

public record AssignFlagRequest(string EntityType, Guid EntityId);
