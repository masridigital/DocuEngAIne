using System.Globalization;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;

namespace DocuEngAIne.Infrastructure.Assets;

/// <summary>
/// How asset layouts are validated and how field values are checked and stored. Values keep one
/// string column, normalized per type so they compare and sort consistently: numbers in invariant
/// culture, dates as <c>yyyy-MM-dd</c>, date-times as round-trip UTC, checkboxes as
/// <c>true</c>/<c>false</c>, multi-selects as a JSON array of option values.
/// </summary>
public static partial class AssetFieldRules
{
    public const int TextMaxLength = 4000;
    public const int MarkdownMaxLength = 100_000;
    public const int UrlMaxLength = 2048;
    public const int EmailMaxLength = 320;
    public const int PhoneMaxLength = 50;

    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);

    [GeneratedRegex(@"^\+?[0-9 ().\-]{3,40}(\s*(x|ext\.?)\s*[0-9]{1,10})?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PhonePattern();

    /// <summary>Something that stops a layout from being published, or from staying valid while it is.</summary>
    public sealed record LayoutProblem(string Code, string Message, Guid? FieldId = null);

    /// <summary>
    /// Validates and normalizes one submitted value. <paramref name="stored"/> null means no value
    /// (the value is cleared). <paramref name="options"/> is the field's option list with its items,
    /// or null when it has none. <paramref name="includeInactiveOptions"/> accepts options that can
    /// no longer be chosen — for re-checking values that are already stored.
    /// </summary>
    public static bool TryNormalize(
        FieldDefinition field,
        JsonElement value,
        OptionList? options,
        out string? stored,
        out string? error,
        bool includeInactiveOptions = false)
    {
        stored = null;
        error = null;
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return true;

        var type = AssetFieldType.TryNormalize(field.FieldType, out var canonical) ? canonical : AssetFieldType.Text;
        switch (type)
        {
            case AssetFieldType.Number:
                return TryNumber(field, value, out stored, out error);
            case AssetFieldType.Checkbox:
                return TryCheckbox(field, value, out stored, out error);
            case AssetFieldType.MultiSelect:
                return TryMultiSelect(field, value, Choosable(options, includeInactiveOptions), out stored, out error);
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            error = $"{field.Name} must be text.";
            return false;
        }

        var text = value.GetString()!.Trim();
        if (text.Length == 0)
            return true;

        switch (type)
        {
            case AssetFieldType.Markdown:
                return Limit(field, text, MarkdownMaxLength, out stored, out error);

            case AssetFieldType.Date:
                if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    stored = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    return true;
                }

                error = $"{field.Name} must be a date in YYYY-MM-DD format.";
                return false;

            case AssetFieldType.DateTime:
                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment))
                {
                    stored = moment.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                    return true;
                }

                error = $"{field.Name} must be a date and time.";
                return false;

            case AssetFieldType.Url:
                // http and https only: javascript:, data: and friends are refused outright.
                if (text.Length <= UrlMaxLength
                    && Uri.TryCreate(text, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    stored = text;
                    return true;
                }

                error = $"{field.Name} must be an http or https link.";
                return false;

            case AssetFieldType.Email:
                if (text.Length <= EmailMaxLength && MailAddress.TryCreate(text, out var address) && address.Address == text)
                {
                    stored = text;
                    return true;
                }

                error = $"{field.Name} must be an email address.";
                return false;

            case AssetFieldType.Phone:
                if (text.Length <= PhoneMaxLength && PhonePattern().IsMatch(text) && text.Count(char.IsAsciiDigit) >= 3)
                {
                    stored = text;
                    return true;
                }

                error = $"{field.Name} must be a phone number.";
                return false;

            case AssetFieldType.Select:
            {
                var item = Find(Choosable(options, includeInactiveOptions), text);
                if (item is null)
                {
                    error = NotAnOption(field, text, options);
                    return false;
                }

                stored = item.Value;
                return true;
            }

            default:
                return Limit(field, text, TextMaxLength, out stored, out error);
        }
    }

    /// <summary>Re-checks a value already stored for <paramref name="field"/> as it is now.</summary>
    public static bool IsCompatible(FieldDefinition field, string? stored, OptionList? options)
        => TryConvertStored(field, options, field, options, stored, out _);

    /// <summary>
    /// Converts a value stored for <paramref name="source"/> into what <paramref name="target"/> (its new
    /// type or option list) would store — used before such a change, which is refused if any value
    /// does not survive it. A single choice becomes a one-option multi-select and back; leaving a
    /// choice type keeps the labels people saw. Options that are no longer active still count: the
    /// value was valid when it was chosen. <paramref name="converted"/> null means the value is empty.
    /// </summary>
    public static bool TryConvertStored(
        FieldDefinition source,
        OptionList? fromOptions,
        FieldDefinition target,
        OptionList? toOptions,
        string? stored,
        out string? converted)
    {
        converted = null;
        if (string.IsNullOrEmpty(stored))
            return true;

        var fromType = AssetFieldType.TryNormalize(source.FieldType, out var sourceCanonical) ? sourceCanonical : AssetFieldType.Text;
        var toType = AssetFieldType.TryNormalize(target.FieldType, out var targetCanonical) ? targetCanonical : AssetFieldType.Text;

        // What a choice field holds: option values.
        List<string>? choices = null;
        if (fromType == AssetFieldType.MultiSelect)
        {
            if (!TryParseStringArray(stored, out var values))
                return false;
            choices = values;
        }
        else if (fromType == AssetFieldType.Select)
        {
            choices = [stored];
        }

        JsonElement element;
        if (choices is not null && !AssetFieldType.UsesOptions(toType))
        {
            element = JsonSerializer.SerializeToElement(string.Join(", ", choices.Select(v => LabelFor(fromOptions, v))));
        }
        else if (toType == AssetFieldType.MultiSelect)
        {
            element = JsonSerializer.SerializeToElement(choices ?? new List<string> { stored });
        }
        else if (choices is not null)
        {
            if (choices.Count == 0)
                return true;
            if (choices.Count > 1)
                return false;
            element = JsonSerializer.SerializeToElement(choices[0]);
        }
        else
        {
            element = JsonSerializer.SerializeToElement(stored);
        }

        return TryNormalize(target, element, toOptions, out converted, out _, includeInactiveOptions: true);
    }

    /// <summary>
    /// Everything that stops a layout from being published. A published layout is kept inside the
    /// same rules: changes that would break them are refused.
    /// </summary>
    public static IReadOnlyList<LayoutProblem> Validate(
        IReadOnlyCollection<FieldDefinition> fields,
        IReadOnlyDictionary<Guid, OptionList> lists)
    {
        var problems = new List<LayoutProblem>();
        if (fields.Count == 0)
            problems.Add(new LayoutProblem("no_fields", "Add at least one field."));

        foreach (var duplicate in fields.GroupBy(f => f.Name.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add(new LayoutProblem("duplicate_name", $"More than one field is named '{duplicate.Key}'."));

        foreach (var field in fields.OrderBy(f => f.SortOrder))
            problems.AddRange(ValidateField(field, lists, requireUsableOptions: true));

        return problems;
    }

    /// <summary>
    /// The rules for one field. <paramref name="requireUsableOptions"/> adds the publish-time rule
    /// that a choice field's list is active and offers at least one active option; a draft may point
    /// at a list that is still being filled in.
    /// </summary>
    public static IReadOnlyList<LayoutProblem> ValidateField(
        FieldDefinition field,
        IReadOnlyDictionary<Guid, OptionList> lists,
        bool requireUsableOptions)
    {
        var problems = new List<LayoutProblem>();
        if (!AssetFieldType.TryNormalize(field.FieldType, out var type))
        {
            problems.Add(new LayoutProblem("unknown_type", $"Field '{field.Name}' has an unknown type '{field.FieldType}'.", field.Id));
            return problems;
        }

        if (field.IsExpiration && !AssetFieldType.IsDateLike(type))
        {
            problems.Add(new LayoutProblem(
                "expiration_not_date",
                $"Field '{field.Name}' tracks an expiration, so it must be a Date or DateTime field.",
                field.Id));
        }

        if (!AssetFieldType.UsesOptions(type))
            return problems;

        if (field.OptionListId is not Guid listId)
            problems.Add(new LayoutProblem("options_required", $"Field '{field.Name}' needs an option list.", field.Id));
        else if (!lists.TryGetValue(listId, out var list))
            problems.Add(new LayoutProblem("options_missing", $"The option list for field '{field.Name}' was not found.", field.Id));
        else if (requireUsableOptions && !list.IsActive)
            problems.Add(new LayoutProblem("options_inactive", $"The option list '{list.Name}' used by field '{field.Name}' is inactive.", field.Id));
        else if (requireUsableOptions && !list.Items.Any(i => i.IsActive))
            problems.Add(new LayoutProblem("options_empty", $"The option list '{list.Name}' used by field '{field.Name}' has no active options.", field.Id));

        return problems;
    }

    /// <summary>The schema a version records: the layout, its fields in order, and each choice field's options.</summary>
    public static string Snapshot(
        AssetType layout,
        IEnumerable<FieldDefinition> fields,
        IReadOnlyDictionary<Guid, OptionList> lists)
        => JsonSerializer.Serialize(
            new
            {
                layout.Name,
                layout.Description,
                layout.AvailableToAllCompanies,
                Fields = fields
                    .OrderBy(f => f.SortOrder)
                    .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(f => new
                    {
                        f.Id,
                        f.Name,
                        f.FieldType,
                        f.IsRequired,
                        f.IsExpiration,
                        f.SortOrder,
                        f.Section,
                        f.HelpText,
                        OptionList = f.OptionListId is Guid id && lists.TryGetValue(id, out var list)
                            ? new
                            {
                                list.Id,
                                list.Name,
                                Options = list.Items
                                    .OrderBy(i => i.SortOrder)
                                    .Select(i => new { i.Value, i.Label, i.IsActive })
                                    .ToList(),
                            }
                            : null,
                    })
                    .ToList(),
            },
            SnapshotJson);

    /// <summary>An option value from a label: lower-case ASCII letters and digits joined by single hyphens.</summary>
    public static string OptionValueFrom(string label)
    {
        var builder = new StringBuilder();
        foreach (var ch in label.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch))
                builder.Append(ch);
            else if (builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
        }

        var value = builder.ToString().Trim('-');
        if (value.Length > 90)
            value = value[..90].Trim('-');
        return value.Length == 0 ? "option" : value;
    }

    private static IReadOnlyList<OptionListItem> Choosable(OptionList? options, bool includeInactive)
    {
        if (options is null)
            return [];
        if (includeInactive)
            return options.Items.ToList();
        return options.IsActive ? options.Items.Where(i => i.IsActive).ToList() : [];
    }

    /// <summary>By value first, then by label, both ignoring case.</summary>
    private static OptionListItem? Find(IReadOnlyList<OptionListItem> items, string text)
        => items.FirstOrDefault(i => string.Equals(i.Value, text, StringComparison.OrdinalIgnoreCase))
            ?? items.FirstOrDefault(i => string.Equals(i.Label, text, StringComparison.OrdinalIgnoreCase));

    private static string LabelFor(OptionList? options, string value)
        => options?.Items.FirstOrDefault(i => string.Equals(i.Value, value, StringComparison.OrdinalIgnoreCase))?.Label ?? value;

    private static bool TryParseStringArray(string json, out List<string> values)
    {
        values = [];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String)
                    return false;
                values.Add(element.GetString()!);
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string NotAnOption(FieldDefinition field, string text, OptionList? options)
        => options is null || !options.IsActive || !options.Items.Any(i => i.IsActive)
            ? $"{field.Name} has no options to choose from."
            : $"'{text}' is not an option for {field.Name}.";

    private static bool Limit(FieldDefinition field, string text, int maxLength, out string? stored, out string? error)
    {
        if (text.Length > maxLength)
        {
            stored = null;
            error = $"{field.Name} is longer than {maxLength} characters.";
            return false;
        }

        stored = text;
        error = null;
        return true;
    }

    private static bool TryNumber(FieldDefinition field, JsonElement value, out string? stored, out string? error)
    {
        stored = null;
        error = null;
        decimal number;
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (!value.TryGetDecimal(out number))
            {
                error = $"{field.Name} is out of range.";
                return false;
            }
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!.Trim();
            if (text.Length == 0)
                return true;
            if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            {
                error = $"{field.Name} must be a number.";
                return false;
            }
        }
        else
        {
            error = $"{field.Name} must be a number.";
            return false;
        }

        stored = number.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryCheckbox(FieldDefinition field, JsonElement value, out string? stored, out string? error)
    {
        stored = null;
        error = null;
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                stored = "true";
                return true;
            case JsonValueKind.False:
                stored = "false";
                return true;
            case JsonValueKind.String:
                var text = value.GetString()!.Trim().ToLowerInvariant();
                if (text.Length == 0)
                    return true;
                if (text is "true" or "yes" or "1")
                {
                    stored = "true";
                    return true;
                }

                if (text is "false" or "no" or "0")
                {
                    stored = "false";
                    return true;
                }

                break;
        }

        error = $"{field.Name} must be true or false.";
        return false;
    }

    private static bool TryMultiSelect(
        FieldDefinition field,
        JsonElement value,
        IReadOnlyList<OptionListItem> choosable,
        out string? stored,
        out string? error)
    {
        stored = null;
        error = null;
        if (value.ValueKind != JsonValueKind.Array)
        {
            error = $"{field.Name} must be a list of options.";
            return false;
        }

        var chosen = new List<OptionListItem>();
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                error = $"{field.Name} must be a list of options.";
                return false;
            }

            var text = element.GetString()!.Trim();
            if (text.Length == 0)
                continue;

            var item = Find(choosable, text);
            if (item is null)
            {
                error = choosable.Count == 0 ? $"{field.Name} has no options to choose from." : $"'{text}' is not an option for {field.Name}.";
                return false;
            }

            if (!chosen.Contains(item))
                chosen.Add(item);
        }

        if (chosen.Count == 0)
            return true;

        stored = JsonSerializer.Serialize(chosen
            .OrderBy(i => i.SortOrder)
            .ThenBy(i => i.Label, StringComparer.OrdinalIgnoreCase)
            .Select(i => i.Value));
        return true;
    }
}
