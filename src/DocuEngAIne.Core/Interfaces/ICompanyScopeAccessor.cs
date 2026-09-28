using DocuEngAIne.Core.Enums;

namespace DocuEngAIne.Core.Interfaces;

/// <summary>
/// The companies the current request may touch, resolved once per request from the caller's
/// security groups. <c>null</c> means no scope was resolved (background work, API tokens, tests),
/// which is unrestricted.
/// </summary>
public interface ICompanyScopeAccessor
{
    CompanyAccessScope? Current { get; set; }
}

public sealed class CompanyScopeAccessor : ICompanyScopeAccessor
{
    public CompanyAccessScope? Current { get; set; }
}

/// <summary>
/// Effective company access. Unrestricted callers (no restricting group, Admin, Owner) get Manage
/// everywhere. A restricted caller gets the highest level any of their groups grants per company,
/// and — only if one of those groups includes it — read-only access to tenant-wide records (those
/// with no company).
/// </summary>
public sealed class CompanyAccessScope
{
    public static readonly CompanyAccessScope Unrestricted = new(true, false, new Dictionary<Guid, CompanyAccessLevel>());

    private CompanyAccessScope(bool isUnrestricted, bool includesTenantWide, IReadOnlyDictionary<Guid, CompanyAccessLevel> levels)
    {
        IsUnrestricted = isUnrestricted;
        IncludesTenantWide = includesTenantWide;
        Levels = levels;
        CompanyIds = [.. levels.Keys];
    }

    public static CompanyAccessScope Restricted(bool includesTenantWide, IReadOnlyDictionary<Guid, CompanyAccessLevel> levels)
        => new(false, includesTenantWide, levels);

    public bool IsUnrestricted { get; }

    /// <summary>Restricted callers only: may they read records that belong to no company?</summary>
    public bool IncludesTenantWide { get; }

    public IReadOnlyDictionary<Guid, CompanyAccessLevel> Levels { get; }

    /// <summary>The granted company ids, as the list the EF query filters bind to.</summary>
    public List<Guid> CompanyIds { get; }

    /// <summary>Access to a record owned by <paramref name="companyId"/>; <c>null</c> is a tenant-wide record.</summary>
    public CompanyAccessLevel LevelFor(Guid? companyId)
    {
        if (IsUnrestricted)
            return CompanyAccessLevel.Manage;
        if (companyId is not Guid id)
            return IncludesTenantWide ? CompanyAccessLevel.View : CompanyAccessLevel.None;
        return Levels.TryGetValue(id, out var level) ? level : CompanyAccessLevel.None;
    }

    public bool Allows(Guid? companyId, CompanyAccessLevel required) => LevelFor(companyId) >= required;
}
