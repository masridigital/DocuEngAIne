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
    /// HTTP Entra identity wins when a request is authenticated. A bound background tenant is used
    /// only when there is no browser session, so a scheduler scope never inherits another tenant's JWT.
    /// </summary>
    private ICurrentUser? Background =>
        Principal?.Identity?.IsAuthenticated == true
            ? null
            : _background?.TenantId is Guid tenantId
                ? BackgroundCurrentUser.ForTenant(tenantId)
                : null;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true || Background is not null;
    public string? ObjectId => Principal?.GetObjectId() ?? Background?.ObjectId;
    public string? Email => Principal?.GetEmail() ?? Background?.Email;
    public string? DisplayName => Principal?.GetDisplayName() ?? Background?.DisplayName;

    // TenantId is sourced from the signed-in user's claims (tid) on HTTP, or from the
    // scheduler-bound BackgroundCurrentUser when there is no request.
    public Guid? TenantId
    {
        get
        {
            var tid = Principal?.FindFirst("tid")?.Value;
            if (Guid.TryParse(tid, out var id))
                return id;
            return Background?.TenantId;
        }
    }

    public bool HasRole(UserRole role)
    {
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
