namespace DocuEngAIne.Infrastructure.Identity;

/// <summary>
/// Per-scope slot the sync scheduler binds before resolving <see cref="Core.Interfaces.ICurrentUser"/>.
/// HTTP requests leave this empty; <see cref="CurrentUser"/> then reads the Entra JWT as before.
/// </summary>
public interface IBackgroundTenantContext
{
    Guid? TenantId { get; }

    /// <summary>
    /// Pins this scope to <paramref name="tenantId"/>. A scope may be bound once; switching to
    /// another tenant throws so two tenants can never share one <c>ICurrentUser</c>.
    /// </summary>
    void Bind(Guid tenantId);
}

public sealed class BackgroundTenantContext : IBackgroundTenantContext
{
    public Guid? TenantId { get; private set; }

    public void Bind(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("A background scope requires a tenant.", nameof(tenantId));

        if (TenantId is Guid existing && existing != tenantId)
        {
            throw new InvalidOperationException(
                "A background scope is bound to one tenant for its lifetime and cannot be switched.");
        }

        TenantId = tenantId;
    }
}
