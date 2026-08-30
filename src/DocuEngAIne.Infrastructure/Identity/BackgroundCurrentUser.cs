using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Identity;

/// <summary>
/// Tenant-scoped identity for unattended work (the sync scheduler). There is no browser session
/// and no Entra JWT, so HTTP <see cref="CurrentUser"/> cannot supply a tenant.
/// </summary>
/// <remarks>
/// Construct only through <see cref="ForTenant"/>. The instance is immutable: a single background
/// identity never hops tenants, and <c>ForTenant</c> on a query still filters to this
/// <see cref="TenantId"/> alone.
/// </remarks>
public sealed class BackgroundCurrentUser : ICurrentUser
{
    /// <summary>Stable id written to audit rows so a scheduled sync is distinguishable from a person.</summary>
    public const string SystemObjectId = "system:sync-scheduler";

    public const string SystemDisplayName = "Sync scheduler";

    private BackgroundCurrentUser(Guid tenantId)
    {
        TenantId = tenantId;
    }

    /// <summary>Service principal bound to exactly one tenant for the lifetime of the instance.</summary>
    public static BackgroundCurrentUser ForTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("A background identity requires a tenant.", nameof(tenantId));

        return new BackgroundCurrentUser(tenantId);
    }

    public bool IsAuthenticated => true;
    public string? ObjectId => SystemObjectId;
    public string? Email => null;
    public string? DisplayName => SystemDisplayName;
    public Guid? TenantId { get; }

    /// <summary>The scheduler acts as the tenant's system principal, equivalent to Owner.</summary>
    public bool HasRole(UserRole role) => role <= UserRole.Owner;
}
