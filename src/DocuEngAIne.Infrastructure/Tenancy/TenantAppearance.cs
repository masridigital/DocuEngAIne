using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocuEngAIne.Core.Enums;

namespace DocuEngAIne.Infrastructure.Tenancy;

/// <summary>
/// The tenant's white-label settings: what the app calls things (<see cref="TenantTerms"/>), the
/// name in its header, and its accent color. No custom CSS and no uploaded images: free-form CSS
/// can read page content out through selectors, and images wait on blob storage.
/// </summary>
public static partial class TenantAppearance
{
    public const int DisplayNameMaxLength = 80;

    /// <summary>The app's page background; the accent is drawn on it (links) and under dark text (buttons).</summary>
    public const string Background = "#0f172a";

    /// <summary>WCAG AA for normal text.</summary>
    public const double MinimumContrast = 4.5;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record Term(string Singular, string Plural);

    [GeneratedRegex("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColor();

    /// <summary>The stored overrides; anything unreadable or no longer registered is ignored.</summary>
    public static Dictionary<string, Term> ParseTerms(string? json)
    {
        var result = new Dictionary<string, Term>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
            return result;

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, Term?>>(json, Json);
            foreach (var (key, term) in parsed ?? new Dictionary<string, Term?>())
            {
                if (TenantTerms.Find(key) is not null
                    && term is not null
                    && TryCleanLabel(term.Singular, TenantTerms.MaxLength, out var singular)
                    && TryCleanLabel(term.Plural, TenantTerms.MaxLength, out var plural))
                {
                    result[key] = new Term(singular, plural);
                }
            }
        }
        catch (JsonException)
        {
            // A damaged value falls back to the defaults rather than breaking every page.
        }

        return result;
    }

    public static string? SerializeTerms(IReadOnlyDictionary<string, Term> overrides)
        => overrides.Count == 0
            ? null
            : JsonSerializer.Serialize(
                overrides.OrderBy(o => o.Key, StringComparer.Ordinal).ToDictionary(o => o.Key, o => o.Value),
                Json);

    /// <summary>Every term as the tenant sees it: its override, else the default.</summary>
    public static Dictionary<string, Term> EffectiveTerms(IReadOnlyDictionary<string, Term> overrides)
        => TenantTerms.All.ToDictionary(
            t => t.Key,
            t => overrides.TryGetValue(t.Key, out var term) ? term : new Term(t.Singular, t.Plural),
            StringComparer.Ordinal);

    /// <summary>A label as stored: trimmed, runs of whitespace collapsed, no control characters, 1–<paramref name="maxLength"/> long.</summary>
    public static bool TryCleanLabel(string? value, int maxLength, out string label)
    {
        label = "";
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var ch in value.Trim())
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = true;
                continue;
            }

            if (char.IsControl(ch) || char.GetUnicodeCategory(ch) == UnicodeCategory.Format)
                return false;
            if (pendingSpace)
                builder.Append(' ');
            pendingSpace = false;
            builder.Append(ch);
        }

        if (builder.Length == 0 || builder.Length > maxLength)
            return false;
        label = builder.ToString();
        return true;
    }

    /// <summary>
    /// Null or blank clears the display name (the product name shows again); otherwise it must be
    /// a clean label of at most <see cref="DisplayNameMaxLength"/> characters.
    /// </summary>
    public static bool TryCleanDisplayName(string? value, out string? displayName, out string? error)
    {
        displayName = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;
        if (TryCleanLabel(value, DisplayNameMaxLength, out var cleaned))
        {
            displayName = cleaned;
            return true;
        }

        error = $"The display name needs 1–{DisplayNameMaxLength} characters and no control characters.";
        return false;
    }

    /// <summary>
    /// Null or blank clears the accent (the default returns); otherwise <c>#RRGGBB</c> that reads
    /// against <see cref="Background"/> at <see cref="MinimumContrast"/> or better, stored lower-case.
    /// </summary>
    public static bool TryNormalizeColor(string? value, out string? color, out string? error)
    {
        color = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var text = value.Trim();
        if (!HexColor().IsMatch(text))
        {
            error = "Use a color written like #38bdf8.";
            return false;
        }

        var normalized = text.ToLowerInvariant();
        var ratio = ContrastRatio(normalized, Background);
        if (ratio < MinimumContrast)
        {
            error = "That color is too dark to read on the app's dark background "
                + $"(contrast {ratio.ToString("0.0", CultureInfo.InvariantCulture)}:1, needs "
                + $"{MinimumContrast.ToString("0.0", CultureInfo.InvariantCulture)}:1). Choose a lighter one.";
            return false;
        }

        color = normalized;
        return true;
    }

    /// <summary>WCAG 2 contrast ratio between two <c>#RRGGBB</c> colors (1 to 21).</summary>
    public static double ContrastRatio(string first, string second)
    {
        var a = RelativeLuminance(first);
        var b = RelativeLuminance(second);
        var lighter = Math.Max(a, b);
        var darker = Math.Min(a, b);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(string hex)
        => 0.2126 * Channel(hex, 1) + 0.7152 * Channel(hex, 3) + 0.0722 * Channel(hex, 5);

    private static double Channel(string hex, int offset)
    {
        var value = int.Parse(hex.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}
