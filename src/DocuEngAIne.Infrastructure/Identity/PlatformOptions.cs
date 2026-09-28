namespace DocuEngAIne.Infrastructure.Identity;

/// <summary>
/// The people who run this deployment (<c>Platform:Operators</c>). Each is an Entra tenant id and
/// object id pair, configured on the host: an Entra app role cannot carry this, because in a
/// multi-tenant app every customer's own administrators control the role assignments in their
/// directory and could grant it to themselves.
/// </summary>
public sealed class PlatformOptions
{
    public const string SectionName = "Platform";

    public List<PlatformOperator> Operators { get; set; } = [];

    public bool IsOperator(Guid? tenantId, string? objectId)
        => tenantId is Guid tenant
            && !string.IsNullOrWhiteSpace(objectId)
            && Operators.Any(o => o.TenantId == tenant && string.Equals(o.ObjectId, objectId, StringComparison.OrdinalIgnoreCase));
}

public sealed class PlatformOperator
{
    public Guid TenantId { get; set; }
    public string ObjectId { get; set; } = "";
}
