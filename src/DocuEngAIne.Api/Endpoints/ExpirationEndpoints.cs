using System.Globalization;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

public static class ExpirationEndpoints
{
    public const string SourceAsset = "Asset";
    public const string SourceAssetField = "AssetField";

    public static IEndpointRouteBuilder MapExpirationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/expirations").RequireAuthorization();

        group.MapGet("", async (
            [FromQuery] Guid? companyId,
            [FromQuery] bool showExpired,
            [FromQuery] string? q,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var items = await QueryAsync(db, user, companyId, showExpired, q, cancellationToken);
            return Results.Ok(items);
        });

        return app;
    }

    public static async Task<IReadOnlyList<ExpirationItem>> QueryAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        Guid? companyId = null,
        bool showExpired = false,
        string? q = null,
        CancellationToken cancellationToken = default)
    {
        var assetsQuery = db.Assets
            .ForTenant(user)
            .AsNoTracking()
            .Include(a => a.CustomFieldValues)
                .ThenInclude(v => v.FieldDefinition)
            .AsQueryable();

        if (companyId is Guid cid)
        {
            // ForTenant on company: unknown / other-tenant ids yield empty, never 500.
            var companyInTenant = await db.Companies.ForTenant(user)
                .AsNoTracking()
                .AnyAsync(c => c.Id == cid, cancellationToken);
            if (!companyInTenant)
                return [];

            assetsQuery = assetsQuery.Where(a => a.CompanyId == cid);
        }

        var assets = await assetsQuery.ToListAsync(cancellationToken);

        var companyIds = assets.Where(a => a.CompanyId.HasValue).Select(a => a.CompanyId!.Value).Distinct().ToList();
        var companyNames = companyIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Companies.ForTenant(user).AsNoTracking()
                .Where(c => companyIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var items = new List<ExpirationItem>();

        foreach (var asset in assets)
        {
            var companyName = asset.CompanyId is Guid companyKey && companyNames.TryGetValue(companyKey, out var name)
                ? name
                : null;

            if (asset.ExpiresAt is DateTimeOffset assetExpiry)
            {
                items.Add(ToItem(SourceAsset, asset.Id, asset.Name, asset.CompanyId, companyName, "Expiration", assetExpiry, today));
            }

            foreach (var value in asset.CustomFieldValues)
            {
                var field = value.FieldDefinition;
                if (field is null || !field.IsExpiration || !IsDateField(field.FieldType))
                    continue;
                if (!TryParseDate(value.Value, out var when))
                    continue;

                items.Add(ToItem(SourceAssetField, value.Id, asset.Name, asset.CompanyId, companyName, field.Name, when, today));
            }
        }

        if (!showExpired)
            items = items.Where(i => i.DaysUntil >= 0).ToList();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            items = items.Where(i =>
                i.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (i.CompanyName != null && i.CompanyName.Contains(term, StringComparison.OrdinalIgnoreCase))
                || i.FieldName.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return items
            .OrderBy(i => i.ExpiresAt)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static bool IsDateField(string? fieldType) =>
        fieldType is not null && (
            fieldType.Equals("Date", StringComparison.OrdinalIgnoreCase)
            || fieldType.Equals("DateTime", StringComparison.OrdinalIgnoreCase));

    internal static bool TryParseDate(string? value, out DateTimeOffset when)
    {
        when = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out when)
            || DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out when)
            || DateTimeOffset.TryParse(value, out when);
    }

    private static ExpirationItem ToItem(
        string sourceType,
        Guid id,
        string name,
        Guid? companyId,
        string? companyName,
        string fieldName,
        DateTimeOffset expiresAt,
        DateOnly today)
    {
        var day = DateOnly.FromDateTime(expiresAt.UtcDateTime);
        var daysUntil = day.DayNumber - today.DayNumber;
        return new ExpirationItem(sourceType, id, name, companyId, companyName, fieldName, expiresAt, daysUntil);
    }
}

public sealed record ExpirationItem(
    string SourceType,
    Guid Id,
    string Name,
    Guid? CompanyId,
    string? CompanyName,
    string FieldName,
    DateTimeOffset ExpiresAt,
    int DaysUntil);
