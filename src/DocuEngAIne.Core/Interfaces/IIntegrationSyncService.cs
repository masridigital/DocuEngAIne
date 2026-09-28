using DocuEngAIne.Core.Entities;

namespace DocuEngAIne.Core.Interfaces;

public interface IIntegrationSyncService
{
    Task<(bool Ok, string Message)> TestConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default);
    Task<SyncRun> SyncAsync(Guid connectionId, CancellationToken cancellationToken = default);
    Task<SyncRun> SyncFromPayloadAsync(Guid connectionId, IReadOnlyList<ExternalCompanyDto> companies, CancellationToken cancellationToken = default);
}

/// <summary>
/// Thrown by <see cref="IIntegrationSyncService"/> when a sync is refused because another run for
/// the same connection is still live. Two concurrent runs double-spend the StackJack allowance and
/// fight over the same mappings, so the second caller is told to wait rather than queued.
/// </summary>
public sealed class SyncAlreadyRunningException : InvalidOperationException
{
    public SyncAlreadyRunningException()
        : base("A sync is already running for this integration.")
    {
    }
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
