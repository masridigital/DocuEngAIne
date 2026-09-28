using DocuEngAIne.Core.Enums;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace DocuEngAIne.Infrastructure.Tenancy;

/// <summary>
/// Which <see cref="TenantFeatures"/> a tenant has on: its stored choice, else the feature's
/// default. Read on gated requests, so the answer is cached briefly per tenant and dropped on every
/// change made through this process.
/// </summary>
public sealed class TenantFeatureService
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly DocuEngAIneDbContext _db;
    private readonly IMemoryCache _cache;

    public TenantFeatureService(DocuEngAIneDbContext db, IMemoryCache cache)
    {
        _db = db;
        _cache = cache;
    }

    /// <exception cref="ArgumentException"><paramref name="key"/> is not a registered feature.</exception>
    public async Task<bool> IsEnabledAsync(Guid tenantId, string key, CancellationToken cancellationToken = default)
    {
        var feature = TenantFeatures.Find(key)
            ?? throw new ArgumentException($"'{key}' is not a registered tenant feature.", nameof(key));
        var states = await GetStatesAsync(tenantId, cancellationToken);
        return states.TryGetValue(feature.Key, out var enabled) ? enabled : feature.EnabledByDefault;
    }

    /// <summary>Every registered feature's state for the tenant.</summary>
    public async Task<IReadOnlyDictionary<string, bool>> GetStatesAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var cacheKey = CacheKey(tenantId);
        if (_cache.TryGetValue(cacheKey, out IReadOnlyDictionary<string, bool>? cached) && cached is not null)
            return cached;

        var stored = await _db.TenantFeatureSettings.AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .ToDictionaryAsync(s => s.Key, s => s.IsEnabled, cancellationToken);
        // A stored key that is no longer registered is ignored; an unstored one takes its default.
        IReadOnlyDictionary<string, bool> states = TenantFeatures.All.ToDictionary(
            f => f.Key,
            f => stored.TryGetValue(f.Key, out var enabled) ? enabled : f.EnabledByDefault,
            StringComparer.Ordinal);

        _cache.Set(cacheKey, states, CacheTtl);
        return states;
    }

    public void Invalidate(Guid tenantId) => _cache.Remove(CacheKey(tenantId));

    private static string CacheKey(Guid tenantId) => $"tenant-features:{tenantId:N}";
}
