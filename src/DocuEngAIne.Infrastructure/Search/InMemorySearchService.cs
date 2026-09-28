using System.Collections.Concurrent;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Search;

/// <summary>
/// Process-local search index. Used until Azure AI Search is provisioned, and in every test
/// so CI never reaches a live endpoint, key, or SDK client.
/// </summary>
public sealed class InMemorySearchService : ISearchService
{
    private readonly ConcurrentDictionary<(Guid TenantId, Guid Id), SearchDocument> _documents = new();

    public Task IndexDocumentAsync(SearchDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        _documents[(document.TenantId, document.Id)] = document;
        return Task.CompletedTask;
    }

    public Task RemoveDocumentAsync(Guid documentId, Guid tenantId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _documents.TryRemove((tenantId, documentId), out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SearchHit>> SearchAsync(string query, Guid tenantId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult<IReadOnlyList<SearchHit>>([]);

        var term = query.Trim();
        IReadOnlyList<SearchHit> hits = _documents.Values
            .Where(document => document.TenantId == tenantId)
            .Where(document => Contains(document.Title, term) || Contains(document.Body, term))
            .OrderBy(document => document.Title, StringComparer.OrdinalIgnoreCase)
            .Select(document => new SearchHit(
                document.Id,
                document.Title,
                document.Body,
                document.CompanyId,
                document.TenantId))
            .ToList();

        return Task.FromResult(hits);
    }

    private static bool Contains(string? value, string term) =>
        !string.IsNullOrEmpty(value)
        && value.Contains(term, StringComparison.OrdinalIgnoreCase);
}
