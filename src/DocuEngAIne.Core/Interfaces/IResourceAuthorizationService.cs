using DocuEngAIne.Core.Enums;

namespace DocuEngAIne.Core.Interfaces;

public interface IResourceAuthorizationService
{
    Task<UserRole> GetEffectiveRoleAsync(Guid resourceId, string resourceType, CancellationToken cancellationToken = default);
    Task<bool> CanReadAsync(Guid resourceId, string resourceType, CancellationToken cancellationToken = default);
    Task<bool> CanWriteAsync(Guid resourceId, string resourceType, CancellationToken cancellationToken = default);
    Task<bool> CanAdminAsync(Guid resourceId, string resourceType, CancellationToken cancellationToken = default);
    Task EnforceAsync(Guid resourceId, string resourceType, UserRole minimumRole, CancellationToken cancellationToken = default);

    /// <summary>
    /// The caller's company access to the record's company (see <see cref="CompanyAccessScope"/>).
    /// Archived records count, so restore can be gated; a record the caller cannot see, or that does
    /// not exist, is <see cref="CompanyAccessLevel.None"/>.
    /// </summary>
    Task<CompanyAccessLevel> GetCompanyAccessAsync(Guid resourceId, string resourceType, CancellationToken cancellationToken = default);
}
