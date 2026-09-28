using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace DocuEngAIne.Infrastructure.Audit;

/// <summary>
/// Purges audit rows past the retention window, in chunks so one pass never holds a giant
/// transaction. Retention is platform policy, not tenant policy, so it runs across all tenants.
/// Chunked remove-range (not ExecuteDelete) so the same code path runs under the InMemory
/// provider in tests.
/// </summary>
public sealed class AuditRetentionService
{
    public const string RetentionDaysKey = "Audit:RetentionDays";
    public const string ChunkSizeKey = "Audit:PurgeChunkSize";
    public const int DefaultRetentionDays = 365;
    public const int DefaultChunkSize = 1000;
    public const int MaxChunkSize = 5000;

    private readonly DocuEngAIneDbContext _db;
    private readonly IConfiguration _configuration;

    public AuditRetentionService(DocuEngAIneDbContext db, IConfiguration configuration)
    {
        _db = db;
        _configuration = configuration;
    }

    public int RetentionDays => _configuration.GetValue(RetentionDaysKey, DefaultRetentionDays);

    public int ChunkSize => Math.Clamp(_configuration.GetValue(ChunkSizeKey, DefaultChunkSize), 1, MaxChunkSize);

    /// <summary>Deletes events older than the retention window. Returns rows removed; 0 with retention disabled (&lt;= 0 days).</summary>
    public async Task<int> PurgeAsync(DateTimeOffset? utcNow = null, CancellationToken cancellationToken = default)
    {
        var days = RetentionDays;
        if (days <= 0)
            return 0;

        var cutoff = (utcNow ?? DateTimeOffset.UtcNow).AddDays(-days);
        var chunk = ChunkSize;
        var removed = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = await _db.AuditLogs
                .Where(a => a.CreatedAt < cutoff)
                .OrderBy(a => a.CreatedAt)
                .Take(chunk)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
                break;

            _db.AuditLogs.RemoveRange(batch);
            await _db.SaveChangesAsync(cancellationToken);
            removed += batch.Count;
            if (batch.Count < chunk)
                break;
        }

        return removed;
    }
}
