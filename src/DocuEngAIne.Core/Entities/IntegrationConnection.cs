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

    public ICollection<IntegrationMapping> Mappings { get; set; } = [];
    public ICollection<SyncRun> SyncRuns { get; set; } = [];
}
