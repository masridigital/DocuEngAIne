using DocuEngAIne.Core.Common;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Core.Entities;

public class EncryptedSecret : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public required string Name { get; set; }
    public string? Username { get; set; }
    public string? EncryptedValue { get; set; }
    public string? KeyVersion { get; set; }
    public string? Notes { get; set; }
    public string? Url { get; set; }
}
