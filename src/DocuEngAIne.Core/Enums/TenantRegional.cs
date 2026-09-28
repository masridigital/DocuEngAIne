namespace DocuEngAIne.Core.Enums;

/// <summary>A way the app writes a date. <see cref="Example"/> is 28 September 2026 written that way.</summary>
public sealed record DateFormatDefinition(string Key, string Example);

/// <summary>A way the app writes a time of day. <see cref="Example"/> is half past two in the afternoon.</summary>
public sealed record TimeFormatDefinition(string Key, string Name, string Example);

/// <summary>
/// A tenant's time zone and how its dates and times are written. The zone decides what "today" is
/// for expirations and the local time every stored instant is shown in; the formats are how the app
/// writes them. Only the listed formats can be stored. A tenant that sets nothing is on UTC,
/// <see cref="DefaultDateFormat"/> and a 24-hour clock, which is what the app did before.
/// </summary>
public static class TenantRegional
{
    public const string DefaultTimeZone = "UTC";
    public const string DefaultDateFormat = "yyyy-MM-dd";
    public const string TimeFormat24 = "24h";
    public const string TimeFormat12 = "12h";
    public const string DefaultTimeFormat = TimeFormat24;

    /// <summary>Long enough for every IANA name (the longest is 32 characters).</summary>
    public const int TimeZoneMaxLength = 64;

    public static readonly IReadOnlyList<DateFormatDefinition> DateFormats =
    [
        new("yyyy-MM-dd", "2026-09-28"),
        new("dd/MM/yyyy", "28/09/2026"),
        new("MM/dd/yyyy", "09/28/2026"),
        new("dd.MM.yyyy", "28.09.2026"),
        new("d MMM yyyy", "28 Sep 2026"),
        new("MMM d, yyyy", "Sep 28, 2026"),
    ];

    public static readonly IReadOnlyList<TimeFormatDefinition> TimeFormats =
    [
        new(TimeFormat24, "24-hour", "14:30"),
        new(TimeFormat12, "12-hour", "2:30 PM"),
    ];

    public static DateFormatDefinition? FindDateFormat(string? key)
        => DateFormats.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));

    public static TimeFormatDefinition? FindTimeFormat(string? key)
        => TimeFormats.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));
}
