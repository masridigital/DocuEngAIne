namespace DocuEngAIne.Core.Enums;

/// <summary>The types an asset layout field can have, stored by name.</summary>
public static class AssetFieldType
{
    public const string Text = nameof(Text);
    public const string Markdown = nameof(Markdown);
    public const string Number = nameof(Number);
    public const string Date = nameof(Date);
    public const string DateTime = nameof(DateTime);
    public const string Url = nameof(Url);
    public const string Email = nameof(Email);
    public const string Phone = nameof(Phone);
    public const string Checkbox = nameof(Checkbox);
    public const string Select = nameof(Select);
    public const string MultiSelect = nameof(MultiSelect);

    public static readonly IReadOnlyList<string> All =
    [
        Text,
        Markdown,
        Number,
        Date,
        DateTime,
        Url,
        Email,
        Phone,
        Checkbox,
        Select,
        MultiSelect,
    ];

    /// <summary>Case-insensitive match to the canonical name.</summary>
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = All.FirstOrDefault(t => string.Equals(t, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "";
        return normalized.Length > 0;
    }

    public static bool UsesOptions(string? type) => type is Select or MultiSelect;

    public static bool IsDateLike(string? type)
        => string.Equals(type, Date, StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, DateTime, StringComparison.OrdinalIgnoreCase);
}
