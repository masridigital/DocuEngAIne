using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Tests;

public class FakeCurrentUser : ICurrentUser
{
    public bool IsAuthenticated { get; set; } = true;
    public string? ObjectId { get; set; }
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public Guid? TenantId { get; set; }
    public UserRole Role { get; set; } = UserRole.Owner;

    public bool HasRole(UserRole role) => Role >= role;
}
