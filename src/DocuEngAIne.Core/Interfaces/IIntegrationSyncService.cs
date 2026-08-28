using DocuEngAIne.Core.Entities;

namespace DocuEngAIne.Core.Interfaces;

public interface IIntegrationSyncService
{
    Task<(bool Ok, string Message)> TestConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default);
    Task<SyncRun> SyncAsync(Guid connectionId, CancellationToken cancellationToken = default);
    Task<SyncRun> SyncFromPayloadAsync(Guid connectionId, IReadOnlyList<ExternalCompanyDto> companies, CancellationToken cancellationToken = default);
}

public record ExternalCompanyDto(
    string ExternalId,
    string Name,
    string? Slug = null,
    string? PrimaryDomain = null,
    string? City = null,
    string? State = null,
    string? Website = null,
    string? Address = null,
    bool? IsInactive = null);

/// <summary>
/// One remote device (NinjaOne device, and later other RMM endpoints) bound for an <c>Asset</c>.
/// <c>OrganizationExternalId</c> is the provider's own company id — the sync resolves it through the
/// connection's existing company <c>IntegrationMapping</c> rows and skips devices whose organization
/// was never mapped, rather than creating an orphan asset.
/// </summary>
public record ExternalDeviceDto(
    string ExternalId,
    string OrganizationExternalId,
    string Name,
    string? NodeClass = null,
    string? SystemName = null,
    string? DnsName = null);
