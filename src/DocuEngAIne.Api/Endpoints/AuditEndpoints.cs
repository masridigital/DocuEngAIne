using System.Text;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

/// <summary>
/// Tenant audit trail: filtered list, single event, per-record activity feed, and CSV export.
/// Admin-gated — audit rows carry actor identity, IPs, and change diffs. <c>AuditLog</c> is not
/// <c>ITenantScoped</c> (platform events may have no tenant), so every query here filters on the
/// caller's TenantId explicitly.
/// </summary>
public static class AuditEndpoints
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
    public const int ActivityLimit = 100;
    public const int ExportRowCap = 10_000;

    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/audit-events").RequireAuthorization(AuthExtensions.AdminPolicy);

        group.MapGet("", ListAsync);
        group.MapGet("/export", ExportAsync);
        group.MapGet("/activity/{entityType}/{entityId:guid}", ActivityAsync);
        group.MapGet("/{id:guid}", GetAsync);

        return app;
    }

    public sealed record AuditEventItem(
        Guid Id,
        string Action,
        string? Category,
        string EntityType,
        Guid? EntityId,
        string? TargetLabel,
        string? Details,
        string? ChangesJson,
        string? ActorObjectId,
        string? ActorName,
        Guid? UserId,
        string? IpAddress,
        string? RequestMethod,
        string? RequestPath,
        DateTimeOffset OccurredAt);

    public sealed record AuditEventPage(int Total, int Page, int PageSize, IReadOnlyList<AuditEventItem> Items);

    public static async Task<IResult> ListAsync(
        [FromQuery] string? action,
        [FromQuery] string? category,
        [FromQuery] string? entityType,
        [FromQuery] Guid? entityId,
        [FromQuery] string? actor,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        var query = Filter(db, user.TenantId.Value, action, category, entityType, entityId, actor, from, to);
        var total = await query.CountAsync(cancellationToken);

        var currentPage = Math.Max(page ?? 1, 1);
        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var rows = await query
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .Skip((currentPage - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken);

        return Results.Ok(new AuditEventPage(total, currentPage, size, rows.Select(Map).ToList()));
    }

    public static async Task<IResult> GetAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        var row = await db.AuditLogs.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id && a.TenantId == user.TenantId, cancellationToken);
        return row is null ? Results.NotFound() : Results.Ok(Map(row));
    }

    /// <summary>Latest activity for one record — the Hudu-style per-item timeline.</summary>
    public static async Task<IResult> ActivityAsync(
        string entityType,
        Guid entityId,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        var rows = await db.AuditLogs.AsNoTracking()
            .Where(a => a.TenantId == user.TenantId && a.EntityType == entityType && a.EntityId == entityId)
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .Take(ActivityLimit)
            .ToListAsync(cancellationToken);

        return Results.Ok(rows.Select(Map).ToList());
    }

    public static async Task<IResult> ExportAsync(
        [FromQuery] string? action,
        [FromQuery] string? category,
        [FromQuery] string? entityType,
        [FromQuery] Guid? entityId,
        [FromQuery] string? actor,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IAuditService audit,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        var rows = await Filter(db, user.TenantId.Value, action, category, entityType, entityId, actor, from, to)
            .OrderByDescending(a => a.CreatedAt)
            .Take(ExportRowCap)
            .ToListAsync(cancellationToken);

        var csv = BuildCsv(rows);

        // Exporting the trail is itself an audited act.
        await audit.LogAsync(new AuditEntry(
            "Audit.Export", nameof(AuditLog),
            Details: $"rows={rows.Count}",
            Category: AuditCategories.Export), cancellationToken);

        return Results.File(
            Encoding.UTF8.GetBytes(csv),
            "text/csv",
            $"audit-events-{DateTimeOffset.UtcNow:yyyy-MM-dd}.csv");
    }

    public static string BuildCsv(IReadOnlyList<AuditLog> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("occurred_at,action,category,entity_type,entity_id,target_label,actor_name,actor_object_id,ip_address,request_method,request_path,details");
        foreach (var a in rows)
        {
            sb.AppendJoin(',',
                CsvField(a.CreatedAt.ToString("O")),
                CsvField(a.Action),
                CsvField(a.Category),
                CsvField(a.EntityType),
                CsvField(a.EntityId?.ToString()),
                CsvField(a.TargetLabel),
                CsvField(a.ActorName),
                CsvField(a.ActorObjectId),
                CsvField(a.IpAddress),
                CsvField(a.RequestMethod),
                CsvField(a.RequestPath),
                CsvField(a.Details));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Quotes the field and defuses spreadsheet formula injection: a leading <c>= + - @</c> or tab
    /// would execute as a formula when the CSV is opened in Excel, so it is prefixed with a quote.
    /// </summary>
    public static string CsvField(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        if (value[0] is '=' or '+' or '-' or '@' or '\t')
            value = "'" + value;

        return '"' + value.Replace("\"", "\"\"") + '"';
    }

    private static IQueryable<AuditLog> Filter(
        DocuEngAIneDbContext db,
        Guid tenantId,
        string? action,
        string? category,
        string? entityType,
        Guid? entityId,
        string? actor,
        DateTimeOffset? from,
        DateTimeOffset? to)
    {
        var query = db.AuditLogs.AsNoTracking().Where(a => a.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(action))
            query = query.Where(a => a.Action == action);
        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(a => a.Category == category);
        if (!string.IsNullOrWhiteSpace(entityType))
            query = query.Where(a => a.EntityType == entityType);
        if (entityId is Guid id)
            query = query.Where(a => a.EntityId == id);
        if (!string.IsNullOrWhiteSpace(actor))
            query = query.Where(a => a.ActorObjectId == actor);
        if (from is DateTimeOffset f)
            query = query.Where(a => a.CreatedAt >= f);
        if (to is DateTimeOffset t)
            query = query.Where(a => a.CreatedAt <= t);
        return query;
    }

    private static AuditEventItem Map(AuditLog a) => new(
        a.Id, a.Action, a.Category, a.EntityType, a.EntityId, a.TargetLabel, a.Details, a.ChangesJson,
        a.ActorObjectId, a.ActorName, a.UserId, a.IpAddress, a.RequestMethod, a.RequestPath, a.CreatedAt);
}
