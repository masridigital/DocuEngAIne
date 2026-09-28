using DocuEngAIne.Core.Enums;

namespace DocuEngAIne.Infrastructure.Tenancy;

/// <summary>
/// A tenant's time zone, and the calendar day it puts an instant on. Only IANA names are accepted:
/// the SPA shows times through the browser's Intl, which knows no Windows zone ids, so the server
/// and the browser must read the same name.
/// </summary>
public static class TenantClock
{
    /// <summary>The tenant's zone: UTC when none is set, or when this host does not know the stored name.</summary>
    public static TimeZoneInfo ZoneFor(string? timeZoneId)
        => TryFindZone(timeZoneId, out var zone) ? zone : TimeZoneInfo.Utc;

    /// <summary>Finds an IANA zone this host knows. UTC is always known.</summary>
    public static bool TryFindZone(string? timeZoneId, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(timeZoneId) || timeZoneId.Length > TenantRegional.TimeZoneMaxLength)
            return false;
        if (string.Equals(timeZoneId, TenantRegional.DefaultTimeZone, StringComparison.Ordinal))
            return true;
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var found) || !found.HasIanaId)
            return false;

        zone = found;
        return true;
    }

    /// <summary>Today's date in the zone.</summary>
    public static DateOnly Today(TimeZoneInfo zone, DateTimeOffset? utcNow = null)
        => DayOf(utcNow ?? DateTimeOffset.UtcNow, zone);

    /// <summary>The calendar day an instant falls on in the zone.</summary>
    public static DateOnly DayOf(DateTimeOffset instant, TimeZoneInfo zone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
}
