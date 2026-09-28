namespace DocuEngAIne.Core.Interfaces;

/// <summary>
/// Tenant-scoped document search. Production will talk to Azure AI Search; tests and local
/// scaffolding use an in-memory stub. Never call a live search service from tests.
/// </summary>
public interface ISearchService
{
    Task IndexDocumentAsync(SearchDocument document, CancellationToken cancellationToken = default);

    Task RemoveDocumentAsync(Guid documentId, Guid tenantId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchHit>> SearchAsync(string query, Guid tenantId, CancellationToken cancellationToken = default);
}

/// <summary>
/// One KB article in the search index. <see cref="Body"/> is <c>Document.Content</c>.
/// </summary>
public sealed record SearchDocument(
    Guid Id,
    string Title,
    string? Body,
    Guid? CompanyId,
    Guid TenantId);

public sealed record SearchHit(
    Guid Id,
    string Title,
    string? Body,
    Guid? CompanyId,
    Guid TenantId);
