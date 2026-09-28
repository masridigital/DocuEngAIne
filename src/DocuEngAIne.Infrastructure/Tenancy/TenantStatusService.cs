using DocuEngAIne.Core.Enums;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace DocuEngAIne.Infrastructure.Tenancy;

/// <summary>
/// A tenant's lifecycle status, read on every API request, so it is cached briefly per tenant and
/// dropped when a platform operator changes it through this process. Another instance picks the
/// change up within <see cref="CacheTtl"/>.
/// </summary>
public sealed class TenantStatusService
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly DocuEngAIneDbContext _db;
    private readonly IMemoryCache _cache;

    public TenantStatusService(DocuEngAIneDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public sealed record State(TenantStatus Status, string? Reason);

    // Wraps the answer so "no such tenant" (null) is cached too.
    private sealed record Entry(State? Value);

    /// <summary>The tenant's status, or null when it has not been onboarded (there is nothing to refuse).</summary>
    public async Task<State?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var key = CacheKey(tenantId);
        if (_cache.TryGetValue(key, out Entry? cached) && cached is not null)
            return cached.Value;

        var state = await _db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new State(t.Status, t.StatusReason))
            .FirstOrDefaultAsync(cancellationToken);

        _cache.Set(key, new Entry(state), CacheTtl);
        return state;
    }

    public void Invalidate(Guid tenantId) => _cache.Remove(CacheKey(tenantId));

    private static string CacheKey(Guid tenantId) => $"tenant-status:{tenantId:N}";
}
