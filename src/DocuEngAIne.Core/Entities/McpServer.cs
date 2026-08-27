using DocuEngAIne.Core.Common;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Core.Entities;

public class McpServer : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public required string Name { get; set; }
    public McpTransport Transport { get; set; } = McpTransport.Http;
    public string? EndpointUrl { get; set; }
    public string? Command { get; set; }
    public string? ArgsJson { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Key Vault secret name. Never store the secret value in SQL.</summary>
    public string? AuthSecretName { get; set; }
    public string? Notes { get; set; }

    public ICollection<IntegrationConnection> Integrations { get; set; } = [];
}
