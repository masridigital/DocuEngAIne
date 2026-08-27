using DocuEngAIne.Core.Common;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Core.Entities;

public class Company : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string? CompanyNumber { get; set; }
    public string? PrimaryDomain { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? Phone { get; set; }
    public string? Website { get; set; }
    public string? Notes { get; set; }
    public string? HoursOfOperation { get; set; }
    public bool IsActive { get; set; } = true;
    public bool PortalEnabled { get; set; }

    public string? HaloClientId { get; set; }
    public string? NinjaOrganizationId { get; set; }
    public string? ExternalIdsJson { get; set; }

    public ICollection<Asset> Assets { get; set; } = [];
    public ICollection<Document> Documents { get; set; } = [];
    public ICollection<Runbook> Runbooks { get; set; } = [];
    public ICollection<KeeperLink> KeeperLinks { get; set; } = [];
}
