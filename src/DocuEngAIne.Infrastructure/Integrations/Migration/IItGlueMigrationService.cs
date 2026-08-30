namespace DocuEngAIne.Infrastructure.Integrations.Migration;

/// <summary>
/// One-shot IT Glue → DocuEngAIne import. IT Glue is not a live company-sync system of record
/// and is not an <c>IntegrationProvider</c>. Named distinctly from a future Hudu importer.
/// </summary>
public interface IItGlueMigrationService
{
    /// <summary>
    /// Imports one slice. Supply Compact <paramref name="mcpServerId"/> to pull
    /// <c>itg_list_organizations</c>, or <paramref name="payloadJson"/> as a JSON:API envelope
    /// (and optional documents / flexible assets). Passwords are never stored.
    /// </summary>
    Task<ItGlueMigrationResult> ImportAsync(
        Guid? mcpServerId,
        string? payloadJson,
        CancellationToken cancellationToken = default);
}

public sealed record ItGlueMigrationResult(
    string Status,
    int CompaniesCreated,
    int CompaniesUpdated,
    int DocumentsCreated,
    int DocumentsUpdated,
    int AssetsCreated,
    int AssetsUpdated,
    int ItemsSkipped,
    string? ErrorSummary = null);
