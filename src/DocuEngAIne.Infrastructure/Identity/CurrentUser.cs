using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace DocuEngAIne.Infrastructure.Identity;

public class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IBackgroundTenantContext? _background;

    public CurrentUser(IHttpContextAccessor httpContextAccessor, IBackgroundTenantContext? background = null)
    {
        _httpContextAccessor = httpContextAccessor;
        _background = background;
    }

    private ClaimsPrincipal? Principal => _httpContextAccessor.HttpContext?.User;

    /// <summary>
    /// Explicit ambient identity (an API token on <c>/mcp</c>) wins when set. MCP is not a browser
    /// JWT, so <see cref="CurrentUserScope"/> is how that caller becomes <see cref="ICurrentUser"/>.
    /// </summary>
    private ICurrentUser? Ambient => CurrentUserScope.Current;

    /// <summary>
    /// Scheduler identity. Used only when there is no ambient token and no authenticated browser
    /// session, so a background scope never inherits another tenant's JWT.
    /// </summary>
    private ICurrentUser? Background =>
        Principal?.Identity?.IsAuthenticated == true
            ? null
            : _background?.TenantId is Guid tenantId
                ? BackgroundCurrentUser.ForTenant(tenantId)
                : null;

    public bool IsAuthenticated =>
        Ambient?.IsAuthenticated == true
        || Principal?.Identity?.IsAuthenticated == true
        || Background is not null;

    public string? ObjectId => Ambient?.ObjectId ?? Principal?.GetObjectId() ?? Background?.ObjectId;
    public string? Email => Ambient?.Email ?? Principal?.GetEmail() ?? Background?.Email;
    public string? DisplayName => Ambient?.DisplayName ?? Principal?.GetDisplayName() ?? Background?.DisplayName;

    // TenantId: ambient token, then Entra tid on HTTP, then the scheduler-bound tenant.
    public Guid? TenantId
    {
        get
        {
            if (Ambient is not null)
                return Ambient.TenantId;

            var tid = Principal?.FindFirst("tid")?.Value;
            if (Guid.TryParse(tid, out var id))
                return id;
            return Background?.TenantId;
        }
    }

    public bool HasRole(UserRole role)
    {
        if (Ambient is not null)
            return Ambient.HasRole(role);

        if (Principal?.Identity?.IsAuthenticated == true)
        {
            return role switch
            {
                UserRole.Owner => Principal.HasRole("Owner"),
                UserRole.Admin => Principal.HasRole("Admin") || Principal.HasRole("Owner"),
                UserRole.Contributor => Principal.HasRole("Contributor") || Principal.HasRole("Admin") || Principal.HasRole("Owner"),
                UserRole.Reader => true,
                _ => false,
            };
        }

        return Background?.HasRole(role) ?? false;
    }
}
