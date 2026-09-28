using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Infrastructure.Identity;

/// <summary>
/// Resolves a plaintext API token to a <see cref="TokenCurrentUser"/>. Lookup is by hash only —
/// there is no tenant yet, so this query is deliberately <em>not</em> <c>ForTenant</c>. Every
/// subsequent query the caller runs must go through <c>ForTenant</c> on the returned identity.
/// </summary>
public static class ApiTokenAuthenticator
{
    /// <summary>
    /// How stale <see cref="Core.Entities.ApiToken.LastUsedAt"/> may get before it is written
    /// again. An MCP client makes many calls per minute and each write is an UPDATE inside the
    /// auth path; last-used is an audit hint, not a metric, so once a minute is plenty.
    /// </summary>
    public static readonly TimeSpan LastUsedWriteInterval = TimeSpan.FromMinutes(1);

    public static async Task<TokenCurrentUser?> AuthenticateAsync(
        string? plaintext,
        DocuEngAIneDbContext db,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
            return null;

        var hash = ApiTokenHasher.Hash(plaintext.Trim());
        var token = await db.ApiTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.RevokedAt == null, cancellationToken);

        if (token is null)
            return null;

        var now = DateTimeOffset.UtcNow;
        if (token.ExpiresAt is DateTimeOffset expiresAt && expiresAt <= now)
            return null;

        var tenantActive = await db.Tenants.AsNoTracking()
            .AnyAsync(t => t.Id == token.TenantId && t.IsActive, cancellationToken);
        if (!tenantActive)
            return null;

        if (token.LastUsedAt is not DateTimeOffset lastUsed || now - lastUsed >= LastUsedWriteInterval)
        {
            token.LastUsedAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }

        return new TokenCurrentUser(token.TenantId, token.Id, token.Name);
    }

    public static string? ReadPresentedToken(string? authorizationHeader, string? apiTokenHeader)
    {
        if (!string.IsNullOrWhiteSpace(apiTokenHeader))
            return apiTokenHeader.Trim();

        if (string.IsNullOrWhiteSpace(authorizationHeader))
            return null;

        const string bearer = "Bearer ";
        var header = authorizationHeader.Trim();
        if (header.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
            return header[bearer.Length..].Trim();

        return null;
    }
}
