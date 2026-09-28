namespace DocuEngAIne.Core.Interfaces;

public interface IAuditService
{
    Task LogAsync(string action, string entityType, Guid? entityId = null, string? details = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rich form carrying category / target label / structured changes. Default implementation
    /// drops the extra fields and delegates to the simple overload, so existing implementations
    /// (test fakes included) keep working; <c>AuditService</c> overrides it to persist everything.
    /// </summary>
    Task LogAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        => LogAsync(entry.Action, entry.EntityType, entry.EntityId, entry.Details, cancellationToken);
}

/// <summary>
/// One audit event. <see cref="ChangesJson"/> is a structured before/after diff
/// (<c>{"field":{"from":...,"to":...}}</c>) and must never contain secrets — no passwords,
/// hashes, token plaintext, or Keeper URLs.
/// </summary>
public sealed record AuditEntry(
    string Action,
    string EntityType,
    Guid? EntityId = null,
    string? Details = null,
    string? Category = null,
    string? TargetLabel = null,
    string? ChangesJson = null);

/// <summary>Well-known <see cref="AuditEntry.Category"/> values; free-form strings are allowed too.</summary>
public static class AuditCategories
{
    public const string Resource = "resource";
    public const string Access = "access";
    public const string Archive = "archive";
    public const string Export = "export";
    public const string System = "system";
    public const string Security = "security";
}
