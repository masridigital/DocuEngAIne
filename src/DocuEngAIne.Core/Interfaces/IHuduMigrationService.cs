namespace DocuEngAIne.Core.Interfaces;

/// <summary>
/// One-shot Hudu → DocuEngAIne import. Not a live company-sync system of record:
/// there is no <c>IntegrationProvider.Hudu</c> and this is not wired into
/// <see cref="IIntegrationSyncService.SyncAsync"/>.
/// </summary>
public interface IHuduMigrationService
{
    /// <summary>
    /// Maps Compact-shaped Hudu JSON into DocuEngAIne. <paramref name="mcpServerId"/> is a
    /// tenant Compact registration used only for the 404 gate — this service does not call
    /// Compact Hudu tools. Returns <c>null</c> when the server is missing, belongs to another
    /// tenant, or is not Compact. Password entities are counted and discarded.
    /// </summary>
    Task<HuduImportResult?> ImportAsync(
        Guid mcpServerId,
        HuduImportPayload? payload = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Compact-shaped Hudu snapshot. Typed lists and/or raw <c>hudu_list_companies</c> /
/// <c>hudu_list_articles</c> / <c>hudu_list_folders</c> JSON (catalog schema or sanitized
/// fixtures). Password rows are counted only — never stored.
/// </summary>
public sealed record HuduImportPayload(
    IReadOnlyList<ExternalCompanyDto>? Companies = null,
    IReadOnlyList<HuduArticleRecord>? Articles = null,
    IReadOnlyList<HuduFolderRecord>? Folders = null,
    string? CompactCompaniesJson = null,
    string? CompactArticlesJson = null,
    string? CompactFoldersJson = null,
    int PasswordCount = 0);

/// <summary>One Hudu knowledge-base article (never a password vault entry).</summary>
public sealed record HuduArticleRecord(
    string ExternalId,
    string Title,
    string? Content = null,
    string? Slug = null,
    string? CompanyExternalId = null,
    string? FolderExternalId = null,
    string? FolderName = null,
    bool Draft = false);

public sealed record HuduFolderRecord(
    string ExternalId,
    string Name,
    string? CompanyExternalId = null);

public sealed record HuduImportResult(
    int CompaniesCreated,
    int CompaniesUpdated,
    int CompaniesSkipped,
    int ArticlesCreated,
    int ArticlesUpdated,
    int ArticlesSkipped,
    int PasswordsSkipped,
    string Source,
    IReadOnlyList<string> ToolsUsed,
    string? Message = null);
