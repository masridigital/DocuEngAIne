using DocuEngAIne.Core.Enums;

namespace DocuEngAIne.Core.Interfaces;

public interface IResourceAuthorizationService
{
    Task<UserRole> GetEffectiveRoleAsync(Guid resourceId, string resourceType, CancellationToken cancellationToken = default);
    Task<bool> CanReadAsync(Guid resourceId, string resourceType, CancellationToken cancellationToken = default);
    Task<bool> CanWriteAsync(Guid resourceId, string resourceType, CancellationToken cancellationToken = default);
    Task<bool> CanAdminAsync(Guid resourceId, string resourceType, CancellationToken cancellationToken = default);
    Task EnforceAsync(Guid resourceId, string resourceType, UserRole minimumRole, CancellationToken cancellationToken = default);
}
