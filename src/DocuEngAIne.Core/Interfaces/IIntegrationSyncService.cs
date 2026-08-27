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
