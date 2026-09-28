using DocuEngAIne.Core.Common;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Core.Entities;

/// <summary>
/// A named set of users that can be confined to specific companies. A group with no company grants
/// restricts nothing; a member of any group that has grants sees only the granted companies (at the
/// highest level any of their groups grants). Admins and Owners are never restricted.
/// </summary>
public class SecurityGroup : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// Whether confined members still see tenant-wide content — the central KB, runbook templates and
    /// tenant-wide Keeper links — read-only. Off by default: a restricted user sees only their companies.
    /// </summary>
    public bool IncludeTenantWide { get; set; }

    public string? CreatedByObjectId { get; set; }

    public ICollection<SecurityGroupMember> Members { get; set; } = [];
    public ICollection<SecurityGroupCompanyGrant> CompanyGrants { get; set; } = [];
}

public class SecurityGroupMember : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid SecurityGroupId { get; set; }
    public SecurityGroup SecurityGroup { get; set; } = null!;

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
}

public class SecurityGroupCompanyGrant : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid SecurityGroupId { get; set; }
    public SecurityGroup SecurityGroup { get; set; } = null!;

    public Guid CompanyId { get; set; }
    public Company Company { get; set; } = null!;

    public CompanyAccessLevel Level { get; set; } = CompanyAccessLevel.View;
}
