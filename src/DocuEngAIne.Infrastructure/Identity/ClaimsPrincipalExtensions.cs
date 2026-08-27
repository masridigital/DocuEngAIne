using System.Security.Claims;

namespace DocuEngAIne.Infrastructure.Identity;

public static class ClaimsPrincipalExtensions
{
    public static string? GetObjectId(this ClaimsPrincipal principal)
        => principal.FindFirst("oid")?.Value
           ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;

    public static string? GetEmail(this ClaimsPrincipal principal)
        => principal.FindFirst("preferred_username")?.Value
           ?? principal.FindFirst(ClaimTypes.Email)?.Value
           ?? principal.FindFirst("emails")?.Value;

    public static string? GetDisplayName(this ClaimsPrincipal principal)
        => principal.FindFirst("name")?.Value
           ?? principal.FindFirst(ClaimTypes.GivenName)?.Value;

    public static bool HasRole(this ClaimsPrincipal principal, string role)
        => principal.IsInRole(role)
           || principal.HasClaim("roles", role)
           || principal.HasClaim(ClaimTypes.Role, role);
}
