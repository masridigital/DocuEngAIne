using System.Globalization;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

/// <summary>
/// Every expiration date across a tenant's assets. Days are counted in the tenant's time zone: a
/// Date field or the asset's expiration shortcut is a calendar day already, a date-time is the day
/// it falls on there, and "today" is the tenant's today.
/// </summary>
public static class ExpirationEndpoints
{
    public const string SourceAsset = "Asset";
    public const string SourceAssetField = "AssetField";

    public static IEndpointRouteBuilder MapExpirationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/expirations").RequireAuthorization();

        group.MapGet("", async (
            [FromQuery] Guid? companyId,
            // Nullable: a non-nullable value-type query param without a default is REQUIRED in
            // minimal APIs, and the SPA omits it when false — those calls were 400s.
            [FromQuery] bool? showExpired,
            [FromQuery] string? q,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            var items = await QueryAsync(db, user, companyId, showExpired ?? false, q, cancellationToken);
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
        CancellationToken cancellationToken = default,
        DateTimeOffset? utcNow = null)
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

        var timeZoneId = user.TenantId is Guid tenantId
            ? await db.Tenants.AsNoTracking()
                .Where(t => t.Id == tenantId)
                .Select(t => t.TimeZoneId)
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        var zone = TenantClock.ZoneFor(timeZoneId);
        var today = TenantClock.Today(zone, utcNow);
        var items = new List<ExpirationItem>();

        foreach (var asset in assets)
        {
            var companyName = asset.CompanyId is Guid companyKey && companyNames.TryGetValue(companyKey, out var name)
                ? name
                : null;

            if (asset.ExpiresAt is DateTimeOffset assetExpiry)
            {
                // A date shortcut: the day it was written for, in the offset it was written with.
                var day = DateOnly.FromDateTime(assetExpiry.DateTime);
                items.Add(ToItem(SourceAsset, asset.Id, asset.Name, asset.CompanyId, companyName, "Expiration", assetExpiry, day, today));
            }

            foreach (var value in asset.CustomFieldValues)
            {
                var field = value.FieldDefinition;
                if (field is null || !field.IsExpiration || !IsDateField(field.FieldType))
                    continue;
                if (!TryParseDate(value.Value, out var when))
                    continue;

                var day = DayOf(field.FieldType, value.Value, when, zone);
                items.Add(ToItem(SourceAssetField, value.Id, asset.Name, asset.CompanyId, companyName, field.Name, when, day, today));
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
            .OrderBy(i => i.Day)
            .ThenBy(i => i.ExpiresAt)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The calendar day an expiration field falls on. A Date field stores a day (<c>yyyy-MM-dd</c>),
    /// which no zone moves; a date-time is the day it falls on in the tenant's zone. A Date value in
    /// some older form falls back to its UTC day, as before.
    /// </summary>
    internal static DateOnly DayOf(string fieldType, string? stored, DateTimeOffset when, TimeZoneInfo zone)
    {
        if (!fieldType.Equals("Date", StringComparison.OrdinalIgnoreCase))
            return TenantClock.DayOf(when, zone);

        return DateOnly.TryParseExact(stored?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : DateOnly.FromDateTime(when.UtcDateTime);
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
        DateOnly day,
        DateOnly today)
        => new(sourceType, id, name, companyId, companyName, fieldName, expiresAt, day.DayNumber - today.DayNumber, day);
}

/// <param name="Day">The calendar day it expires on, in the tenant's time zone (see <see cref="ExpirationEndpoints"/>).</param>
public sealed record ExpirationItem(
    string SourceType,
    Guid Id,
    string Name,
    Guid? CompanyId,
    string? CompanyName,
    string FieldName,
    DateTimeOffset ExpiresAt,
    int DaysUntil,
    DateOnly Day = default);
