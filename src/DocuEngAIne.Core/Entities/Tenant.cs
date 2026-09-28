using DocuEngAIne.Core.Common;

namespace DocuEngAIne.Core.Entities;

public class Tenant : EntityBase
{
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string? PrimaryDomain { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// When on, API requests for this tenant are refused unless they come from an active
    /// <see cref="IpAllowlistEntry"/>. Off by default; turning it on is guarded against locking out
    /// the administrator doing it.
    /// </summary>
    public bool IpAllowlistEnabled { get; set; }

    /// <summary>Shown in place of the product name in the app header; null = the product name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// <c>#RRGGBB</c> used for links and buttons; null = the default. Must read against the app's
    /// dark background (WCAG AA), which also keeps dark button text readable on it.
    /// </summary>
    public string? AccentColor { get; set; }

    /// <summary>
    /// Overrides of <see cref="Enums.TenantTerms"/>, as <c>{"company":{"singular":"Client","plural":"Clients"}}</c>.
    /// Only overridden terms are stored; null = every default.
    /// </summary>
    public string? TerminologyJson { get; set; }

    public ICollection<User> Users { get; set; } = [];
    public ICollection<Company> Companies { get; set; } = [];
    public ICollection<AssetType> AssetTypes { get; set; } = [];
    public ICollection<Asset> Assets { get; set; } = [];
    public ICollection<Document> Documents { get; set; } = [];
    public ICollection<DocumentFolder> DocumentFolders { get; set; } = [];
    public ICollection<KeeperLink> KeeperLinks { get; set; } = [];
    public ICollection<Runbook> Runbooks { get; set; } = [];
    public ICollection<ResourceRoleAssignment> ResourceRoleAssignments { get; set; } = [];
    public ICollection<McpServer> McpServers { get; set; } = [];
    public ICollection<IntegrationConnection> IntegrationConnections { get; set; } = [];
    public ICollection<FlagDefinition> FlagDefinitions { get; set; } = [];
    public ICollection<ApiToken> ApiTokens { get; set; } = [];
}
