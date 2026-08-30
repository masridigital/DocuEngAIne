namespace DocuEngAIne.Core.Interfaces;

/// <summary>
/// One remote Halo site bound for a location. <c>ClientExternalId</c> is the provider's
/// own client id — later sync resolves it through company <c>IntegrationMapping</c> rows
/// and skips sites whose client was never mapped, rather than creating an orphan location.
/// </summary>
public record ExternalLocationDto(
    string ExternalId,
    string ClientExternalId,
    string Name,
    string? Address = null,
    string? City = null,
    bool? IsInactive = null);
