using DocuEngAIne.Core.Common;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Core.Entities;

/// <summary>
/// Per-tenant credential for non-browser callers (the outbound MCP server, later the sync scheduler).
/// The plaintext is shown once at create; only <see cref="TokenHash"/> is persisted.
/// </summary>
public class ApiToken : EntityBase, ITenantScoped
{
    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public required string Name { get; set; }

    /// <summary>SHA-256 hex of the plaintext token. Never store or return the plaintext after create.</summary>
    public required string TokenHash { get; set; }

    /// <summary>Leading characters of the plaintext, enough to recognise a token in the admin list.</summary>
    public required string TokenPrefix { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// Hard expiry, checked at authentication. Null means the token does not expire — allowed,
    /// because a token dying silently mid-integration is its own outage, but the admin list shows
    /// the expiry so a non-expiring token is a visible choice rather than the invisible default.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Optional Entra object id of the admin who minted the token. No FK — avoids a second cascade path off Users.</summary>
    public string? CreatedByObjectId { get; set; }
}
