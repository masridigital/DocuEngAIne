namespace DocuEngAIne.Core.Interfaces;

/// <summary>
/// One remote contact (Halo end user, and later other PSA contacts).
/// <c>ClientExternalId</c> is the provider's own company id — the sync resolves it through the
/// connection's existing company <c>IntegrationMapping</c> rows. <c>SiteExternalId</c> is the
/// provider's site id when present. Rows missing an id or name are dropped by the mapper.
/// </summary>
public record ExternalContactDto(
    string ExternalId,
    string? ClientExternalId,
    string Name,
    string? Email = null,
    string? SiteExternalId = null,
    bool? IsInactive = null);
