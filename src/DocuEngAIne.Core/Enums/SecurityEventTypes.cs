namespace DocuEngAIne.Core.Enums;

/// <summary>A kind of security event, its name for people, and how serious it is.</summary>
public sealed record SecurityEventDefinition(string Type, string Name, string Severity);

/// <summary>
/// The security events the platform records: refusals that say something about who is trying to
/// get in, and from where. Changes to security settings (roles, the allowlist, security groups) are
/// already in the audit log under the security category; sign-in, MFA and sessions belong to Entra
/// and appear in its sign-in logs.
/// </summary>
public static class SecurityEventTypes
{
    public const string SeverityInfo = "info";
    public const string SeverityWarning = "warning";
    public const string SeverityCritical = "critical";

    public const string IpBlocked = "ip.blocked";
    public const string TenantClosedAccess = "tenant.closed_access";
    public const string DeactivatedUserAccess = "user.deactivated_access";
    public const string TokenExpired = "token.expired";
    public const string TokenRevoked = "token.revoked";

    public static readonly IReadOnlyList<string> Severities = [SeverityInfo, SeverityWarning, SeverityCritical];

    public static readonly IReadOnlyList<SecurityEventDefinition> All =
    [
        new(IpBlocked, "Refused by the IP allowlist", SeverityWarning),
        new(TenantClosedAccess, "Tried to use a suspended or archived tenant", SeverityInfo),
        new(DeactivatedUserAccess, "A deactivated user tried to sign in", SeverityWarning),
        new(TokenExpired, "An expired API token was used", SeverityWarning),
        // Someone still holds a token that was revoked, perhaps because it leaked.
        new(TokenRevoked, "A revoked API token was used", SeverityCritical),
    ];

    public static SecurityEventDefinition? Find(string? type)
        => All.FirstOrDefault(d => string.Equals(d.Type, type, StringComparison.Ordinal));
}
