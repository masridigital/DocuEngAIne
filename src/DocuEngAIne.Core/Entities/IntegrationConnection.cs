using DocuEngAIne.Core.Common;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Core.Entities;

public class IntegrationConnection : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public IntegrationProvider Provider { get; set; }
    public required string DisplayName { get; set; }
    public IntegrationStatus Status { get; set; } = IntegrationStatus.Disconnected;
    public string? ConfigJson { get; set; }
    /// <summary>Key Vault secret name for API credentials.</summary>
    public string? AuthSecretName { get; set; }
    public Guid? McpServerId { get; set; }
    public McpServer? McpServer { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
    public string? LastError { get; set; }
    public bool IsEnabled { get; set; } = true;

    /// <summary>Skip inactive remote accounts. Default on (safe).</summary>
    public bool SkipInactive { get; set; } = true;
    /// <summary>Skip contacts on later live pull. Default on (safe).</summary>
    public bool SkipContacts { get; set; } = true;
    /// <summary>Skip locations/sites. Default off (import them).</summary>
    public bool SkipLocations { get; set; }
    /// <summary>Skip assets. Ninja skip-devices maps here when Provider is NinjaOne. Default off.</summary>
    public bool SkipAssets { get; set; }
    /// <summary>Overwrite local asset names from the remote. Default off.</summary>
    public bool AutoUpdateAssetNames { get; set; }
    /// <summary>Overwrite Name/Address/City/State/Website/PrimaryDomain on mapped companies. Default off (refuse clobber).</summary>
    public bool UpdateCompanyDetails { get; set; }

    public ICollection<IntegrationMapping> Mappings { get; set; } = [];
    public ICollection<SyncRun> SyncRuns { get; set; } = [];
}
