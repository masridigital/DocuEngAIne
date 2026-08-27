using DocuEngAIne.Core.Enums;

namespace DocuEngAIne.Core.Interfaces;

public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    string? ObjectId { get; }
    string? Email { get; }
    string? DisplayName { get; }
    Guid? TenantId { get; }
    bool HasRole(UserRole role);
}
