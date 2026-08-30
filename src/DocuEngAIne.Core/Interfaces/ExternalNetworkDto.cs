namespace DocuEngAIne.Core.Interfaces;

/// <summary>
/// One remote Meraki network (Compact <c>meraki_get_organization_networks</c>) bound for later
/// company-scoped ingest. <c>OrganizationExternalId</c> is the provider org id from
/// <c>meraki_get_organizations</c> — the same value the company mapper stores as <c>ExternalId</c>.
/// </summary>
public record ExternalNetworkDto(
    string ExternalId,
    string OrganizationExternalId,
    string Name,
    IReadOnlyList<string>? ProductTypes = null,
    IReadOnlyList<string>? Tags = null);
