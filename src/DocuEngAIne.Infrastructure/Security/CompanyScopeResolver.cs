using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Infrastructure.Security;

/// <summary>
/// Resolves a caller's <see cref="CompanyAccessScope"/> from their security groups. Admins and
/// Owners (Entra app role or database role, the same rule the Admin policy uses) are never
/// restricted; a user in no group, or only in groups without company grants, is unrestricted; a
/// member of any group with grants is confined to the granted companies at the highest level any
/// of their groups grants.
/// </summary>
public sealed class CompanyScopeResolver
{
    private readonly DocuEngAIneDbContext _db;

    public CompanyScopeResolver(DocuEngAIneDbContext db) => _db = db;

    public async Task<CompanyAccessScope> ResolveAsync(ICurrentUser user, CancellationToken cancellationToken = default)
    {
        if (user.TenantId is not Guid tenantId || string.IsNullOrEmpty(user.ObjectId))
            return CompanyAccessScope.Unrestricted;
        if (user.HasRole(UserRole.Admin))
            return CompanyAccessScope.Unrestricted;

        var objectId = user.ObjectId;
        var grants = await GrantsQuery(tenantId)
            .Where(g => g.SecurityGroup.Members.Any(m => m.User.TenantId == tenantId && m.User.EntraObjectId == objectId))
            .Select(g => new Grant(g.CompanyId, g.Level, g.SecurityGroup.IncludeTenantWide))
            .ToListAsync(cancellationToken);
        if (grants.Count == 0)
            return CompanyAccessScope.Unrestricted;

        // Only reached for users with grants, so unrestricted callers pay one query, not two.
        var isDatabaseAdmin = await _db.Users.AsNoTracking().AnyAsync(
            u => u.TenantId == tenantId && u.EntraObjectId == objectId && u.IsActive && u.Role >= UserRole.Admin,
            cancellationToken);
        return isDatabaseAdmin ? CompanyAccessScope.Unrestricted : Build(grants);
    }

    /// <summary>
    /// The scope a stored user would get, for the admin preview. Their Entra app roles are not
    /// known here, so an Entra-only Admin shows as whatever their groups say.
    /// </summary>
    public async Task<CompanyAccessScope> ResolveForUserAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default)
    {
        var isDatabaseAdmin = await _db.Users.AsNoTracking().AnyAsync(
            u => u.TenantId == tenantId && u.Id == userId && u.IsActive && u.Role >= UserRole.Admin,
            cancellationToken);
        if (isDatabaseAdmin)
            return CompanyAccessScope.Unrestricted;

        var grants = await GrantsQuery(tenantId)
            .Where(g => g.SecurityGroup.Members.Any(m => m.UserId == userId))
            .Select(g => new Grant(g.CompanyId, g.Level, g.SecurityGroup.IncludeTenantWide))
            .ToListAsync(cancellationToken);
        return grants.Count == 0 ? CompanyAccessScope.Unrestricted : Build(grants);
    }

    private IQueryable<SecurityGroupCompanyGrant> GrantsQuery(Guid tenantId)
        => _db.SecurityGroupCompanyGrants
            .IgnoreQueryFilters([DocuEngAIneDbContext.CompanyScopeFilter])
            .AsNoTracking()
            .Where(g => g.TenantId == tenantId);

    private static CompanyAccessScope Build(IReadOnlyList<Grant> grants)
    {
        var levels = new Dictionary<Guid, CompanyAccessLevel>();
        foreach (var grant in grants)
        {
            if (grant.Level < CompanyAccessLevel.View)
                continue;
            if (!levels.TryGetValue(grant.CompanyId, out var existing) || grant.Level > existing)
                levels[grant.CompanyId] = grant.Level;
        }

        return CompanyAccessScope.Restricted(grants.Any(g => g.IncludeTenantWide), levels);
    }

    private sealed record Grant(Guid CompanyId, CompanyAccessLevel Level, bool IncludeTenantWide);
}
