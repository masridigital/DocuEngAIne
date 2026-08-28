namespace DocuEngAIne.Core.Enums;

public static class FlagEntityType
{
    public const string Company = nameof(Company);
    public const string Asset = nameof(Asset);
    public const string Document = nameof(Document);
    public const string Runbook = nameof(Runbook);
    public const string KeeperLink = nameof(KeeperLink);

    public static readonly IReadOnlyList<string> All =
    [
        Company,
        Asset,
        Document,
        Runbook,
        KeeperLink,
    ];

    public static bool TryNormalize(string? value, out string entityType)
    {
        entityType = "";
        if (string.IsNullOrWhiteSpace(value))
            return false;

        foreach (var candidate in All)
        {
            if (candidate.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                entityType = candidate;
                return true;
            }
        }

        return false;
    }
}
