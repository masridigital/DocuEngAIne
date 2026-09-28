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
