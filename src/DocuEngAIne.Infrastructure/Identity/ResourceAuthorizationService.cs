using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Infrastructure.Identity;

public class ResourceAuthorizationService : IResourceAuthorizationService
{
    private readonly DocuEngAIneDbContext _db;
    private readonly ICurrentUser _currentUser;

    public ResourceAuthorizationService(DocuEngAIneDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<UserRole> GetEffectiveRoleAsync(Guid resourceId, string resourceType, CancellationToken cancellationToken = default)
    {
        if (_currentUser.TenantId is null)
            return UserRole.None;

        if (!_currentUser.IsAuthenticated)
            return UserRole.None;

        var user = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.TenantId == _currentUser.TenantId && u.EntraObjectId == _currentUser.ObjectId, cancellationToken);

        // A deactivated row confers nothing — not its tenant role, not its per-resource grants.
        if (user is null || !user.IsActive)
            return UserRole.None;

        var assignment = await _db.ResourceRoleAssignments
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.TenantId == _currentUser.TenantId
                && r.UserId == user.Id
                && r.ResourceType == resourceType
                && r.ResourceId == resourceId, cancellationToken);

        if (assignment is not null)
            return assignment.Role;

        return user.Role;
    }

    public async Task<bool> CanReadAsync(Guid resourceId, string resourceType, CancellationToken cancellationToken = default)
    {
        var role = await GetEffectiveRoleAsync(resourceId, resourceType, cancellationToken);
        return role >= UserRole.Reader;
    }

    public async Task<bool> CanWriteAsync(Guid resourceId, string resourceType, CancellationToken cancellationToken = default)
    {
        var role = await GetEffectiveRoleAsync(resourceId, resourceType, cancellationToken);
        return role >= UserRole.Contributor;
    }

    public async Task<bool> CanAdminAsync(Guid resourceId, string resourceType, CancellationToken cancellationToken = default)
    {
        var role = await GetEffectiveRoleAsync(resourceId, resourceType, cancellationToken);
        return role >= UserRole.Admin;
    }

    public async Task EnforceAsync(Guid resourceId, string resourceType, UserRole minimumRole, CancellationToken cancellationToken = default)
    {
        var role = await GetEffectiveRoleAsync(resourceId, resourceType, cancellationToken);
        if (role < minimumRole)
            throw new UnauthorizedAccessException($"Access denied to {resourceType} {resourceId}. Required role: {minimumRole}.");
    }

    public async Task<CompanyAccessLevel> GetCompanyAccessAsync(Guid resourceId, string resourceType, CancellationToken cancellationToken = default)
    {
        var scope = _db.CompanyScope;
        if (scope.IsUnrestricted)
            return CompanyAccessLevel.Manage;
        if (_currentUser.TenantId is not Guid tenantId)
            return CompanyAccessLevel.None;

        // Past the soft-delete filter only: the company filter still hides records in companies
        // the caller cannot see, which then resolve to None.
        string[] softDelete = [ModelBuilderExtensions.SoftDeleteFilter];
        var owner = resourceType switch
        {
            ResourceType.Asset => await _db.Assets.IgnoreQueryFilters(softDelete)
                .Where(x => x.TenantId == tenantId && x.Id == resourceId)
                .Select(x => new CompanyOwner(x.CompanyId)).FirstOrDefaultAsync(cancellationToken),
            ResourceType.Document => await _db.Documents.IgnoreQueryFilters(softDelete)
                .Where(x => x.TenantId == tenantId && x.Id == resourceId)
                .Select(x => new CompanyOwner(x.CompanyId)).FirstOrDefaultAsync(cancellationToken),
            ResourceType.Runbook => await _db.Runbooks.IgnoreQueryFilters(softDelete)
                .Where(x => x.TenantId == tenantId && x.Id == resourceId)
                .Select(x => new CompanyOwner(x.CompanyId)).FirstOrDefaultAsync(cancellationToken),
            ResourceType.KeeperLink => await _db.KeeperLinks.IgnoreQueryFilters(softDelete)
                .Where(x => x.TenantId == tenantId && x.Id == resourceId)
                .Select(x => new CompanyOwner(x.CompanyId)).FirstOrDefaultAsync(cancellationToken),
            ResourceType.DocumentFolder => await _db.DocumentFolders
                .Where(x => x.TenantId == tenantId && x.Id == resourceId)
                .Select(x => new CompanyOwner(x.CompanyId)).FirstOrDefaultAsync(cancellationToken),
            _ => null,
        };

        return owner is null ? CompanyAccessLevel.None : scope.LevelFor(owner.CompanyId);
    }

    private sealed record CompanyOwner(Guid? CompanyId);
}
