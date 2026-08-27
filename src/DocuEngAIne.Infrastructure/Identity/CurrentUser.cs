using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace DocuEngAIne.Infrastructure.Identity;

public class CurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUser(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private ClaimsPrincipal? Principal => _httpContextAccessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;
    public string? ObjectId => Principal?.GetObjectId();
    public string? Email => Principal?.GetEmail();
    public string? DisplayName => Principal?.GetDisplayName();

    // TenantId is sourced from the signed-in user's claims (tid) for this skeleton.
    // In production, map the Entra tenant to your internal Tenant row on first login.
    public Guid? TenantId
    {
        get
        {
            var tid = Principal?.FindFirst("tid")?.Value;
            if (Guid.TryParse(tid, out var id))
                return id;
            return null;
        }
    }

    public bool HasRole(UserRole role)
    {
        if (Principal is null)
            return false;

        return role switch
        {
            UserRole.Owner => Principal.HasRole("Owner"),
            UserRole.Admin => Principal.HasRole("Admin") || Principal.HasRole("Owner"),
            UserRole.Contributor => Principal.HasRole("Contributor") || Principal.HasRole("Admin") || Principal.HasRole("Owner"),
            UserRole.Reader => true,
            _ => false,
        };
    }
}
