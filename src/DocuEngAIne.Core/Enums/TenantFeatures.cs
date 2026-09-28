namespace DocuEngAIne.Core.Enums;

/// <summary>An optional capability a tenant administrator can switch off.</summary>
public sealed record TenantFeatureDefinition(string Key, string Name, string Description, bool EnabledByDefault);

/// <summary>
/// The registered feature catalog: a key that is not listed here can be neither stored nor gated on.
/// Only optional modules belong here. Sign-in, users, roles, security groups, audit and the IP
/// allowlist are core and are never features. Everything that already shipped defaults to on, so
/// adding the catalog changed no tenant's behaviour.
/// </summary>
public static class TenantFeatures
{
    public const string ClientPortal = "client_portal";
    public const string AiAssistant = "ai_assistant";
    public const string McpServer = "mcp_server";
    public const string AccessReviews = "access_reviews";

    public const string DisabledError = "feature_disabled";

    public static readonly IReadOnlyList<TenantFeatureDefinition> All =
    [
        new(ClientPortal, "Client portal", "The read-only portal for portal-enabled companies.", true),
        new(AiAssistant, "AI assistant", "LLM chat, and summarize / rewrite on documents.", true),
        new(McpServer, "MCP server", "AI agents reading this tenant's data through /mcp with API tokens.", true),
        new(AccessReviews, "Access reviews", "Periodic user-access certification and its CSV evidence.", true),
    ];

    public static TenantFeatureDefinition? Find(string? key)
        => All.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));
}

/// <summary>A name the tenant can change in the app (white-labelling): what it calls companies, assets…</summary>
public sealed record TenantTermDefinition(string Key, string Describes, string Singular, string Plural);

public static class TenantTerms
{
    public const string Company = "company";
    public const string Asset = "asset";
    public const string Document = "document";
    public const string Runbook = "runbook";

    public const int MaxLength = 40;

    public static readonly IReadOnlyList<TenantTermDefinition> All =
    [
        new(Company, "The client spaces you document", "Company", "Companies"),
        new(Asset, "Devices, licenses and other tracked items", "Asset", "Assets"),
        new(Document, "Knowledge base articles", "Document", "Documents"),
        new(Runbook, "Procedures and checklists", "Runbook", "Runbooks"),
    ];

    public static TenantTermDefinition? Find(string? key)
        => All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.Ordinal));
}
