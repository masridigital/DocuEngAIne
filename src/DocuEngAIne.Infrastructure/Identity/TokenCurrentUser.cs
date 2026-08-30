using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Identity;

/// <summary>
/// <see cref="ICurrentUser"/> for a per-tenant API token. MCP is not a browser JWT, so this is the
/// identity <c>ForTenant</c> sees once the token has been resolved. Reader only — the outbound
/// tool surface is read-only.
/// </summary>
public sealed class TokenCurrentUser : ICurrentUser
{
    public TokenCurrentUser(Guid tenantId, Guid tokenId, string tokenName)
    {
        TenantId = tenantId;
        TokenId = tokenId;
        DisplayName = tokenName;
        ObjectId = $"apitoken:{tokenId:D}";
    }

    public Guid TokenId { get; }
    public bool IsAuthenticated => true;
    public string? ObjectId { get; }
    public string? Email => null;
    public string? DisplayName { get; }
    public Guid? TenantId { get; }

    public bool HasRole(UserRole role) => UserRole.Reader >= role && role != UserRole.None;
}
