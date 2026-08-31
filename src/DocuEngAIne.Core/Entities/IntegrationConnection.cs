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

    /// <summary>
    /// When a sync run last <em>started</em>, success or failure. Cadence is derived from this as
    /// well as <see cref="LastSyncAt"/>: a connection whose runs keep failing must wait out its
    /// interval like everyone else, otherwise the scheduler retries every poll tick and a broken
    /// Free-tier connector burns its whole monthly allowance in hours.
    /// </summary>
    public DateTimeOffset? LastAttemptAt { get; set; }

    public string? LastError { get; set; }
    public bool IsEnabled { get; set; } = true;

    /// <summary>Skip inactive remote accounts. Default on (safe).</summary>
    public bool SkipInactive { get; set; } = true;
    /// <summary>Skip contacts on later live pull. Default off (match Hudu).</summary>
    public bool SkipContacts { get; set; }
    /// <summary>Skip locations/sites. Default off (import them).</summary>
    public bool SkipLocations { get; set; }
    /// <summary>Skip assets. Ninja skip-devices maps here when Provider is NinjaOne. Default off.</summary>
    public bool SkipAssets { get; set; }
    /// <summary>Overwrite local asset names from the remote. Default off.</summary>
    public bool AutoUpdateAssetNames { get; set; }
    /// <summary>Overwrite Name/Address/City/State/Website/PrimaryDomain on mapped companies. Default off (refuse clobber).</summary>
    public bool UpdateCompanyDetails { get; set; }

    /// <summary>
    /// StackJack tier for this connector, detected from <c>stackjack_session_info</c>. StackJack meters
    /// per connector subscription, so this is a property of the connection, not of the MCP server.
    /// </summary>
    public StackJackPlan StackJackPlan { get; set; } = StackJackPlan.Unknown;

    /// <summary>
    /// Successful tool calls allowed per billing cycle, as reported by StackJack. Authoritative over the
    /// tier's published number, because StackJack can set a custom limit on an individual subscription.
    /// Null when detection has not run. <see cref="int.MaxValue"/> means unlimited.
    /// </summary>
    public int? MonthlyCallLimit { get; set; }

    /// <summary>When the tier and limit above were last read from StackJack.</summary>
    public DateTimeOffset? PlanDetectedAt { get; set; }

    /// <summary>
    /// Explicit override for the scheduled-check interval, in minutes. Null means derive it from the
    /// detected allowance. Clamped to the floor in <c>SyncCadencePolicy</c> so an override cannot
    /// out-run the plan.
    /// </summary>
    public int? SyncIntervalMinutesOverride { get; set; }

    public ICollection<IntegrationMapping> Mappings { get; set; } = [];
    public ICollection<SyncRun> SyncRuns { get; set; } = [];
}
