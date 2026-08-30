namespace DocuEngAIne.Core.Interfaces;

/// <summary>
/// One-shot Hudu → DocuEngAIne import. Not a live company-sync system of record:
/// there is no <c>IntegrationProvider.Hudu</c> and this is not wired into
/// <see cref="IIntegrationSyncService.SyncAsync"/>.
/// </summary>
public interface IHuduMigrationService
{
    /// <summary>
    /// Imports companies and articles from a tenant-scoped StackJack Compact server and/or an
    /// explicit payload. Returns <c>null</c> when <paramref name="mcpServerId"/> is missing,
    /// belongs to another tenant, or is not Compact — the endpoint maps that to 404.
    /// Password entities are counted and discarded; they are never stored locally.
    /// </summary>
    Task<HuduImportResult?> ImportAsync(
        Guid mcpServerId,
        HuduImportPayload? payload = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional snapshot used when Compact Hudu tools are unavailable, or when an admin supplies
/// a sanitized export. A non-null <see cref="Companies"/>, <see cref="Articles"/>, or
/// <see cref="Passwords"/> list selects payload mode and skips MCP data pulls.
/// </summary>
public sealed record HuduImportPayload(
    IReadOnlyList<ExternalCompanyDto>? Companies = null,
    IReadOnlyList<HuduArticleRecord>? Articles = null,
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
