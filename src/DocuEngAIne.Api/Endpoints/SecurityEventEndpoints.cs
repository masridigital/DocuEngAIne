using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

/// <summary>
/// The tenant's security events (<see cref="SecurityEventTypes"/>): refusals that say who is trying
/// to get in and from where. Admin only, newest first, one row per burst of repeats with its count.
/// </summary>
public static class SecurityEventEndpoints
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    public static IEndpointRouteBuilder MapSecurityEventEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/security-events").RequireAuthorization(AuthExtensions.AdminPolicy);
        group.MapGet("", ListAsync);
        group.MapGet("/types", () => Results.Ok(SecurityEventTypes.All));
        return app;
    }

    public sealed record SecurityEventItem(
        Guid Id,
        string EventType,
        string Name,
        string Severity,
        string Description,
        string? IpAddress,
        string? ActorObjectId,
        string? ActorName,
        string? Path,
        int Count,
        DateTimeOffset FirstSeenAt,
        DateTimeOffset LastSeenAt);

    public sealed record SecurityEventPage(int Total, int Page, int PageSize, IReadOnlyList<SecurityEventItem> Items);

    public static async Task<IResult> ListAsync(
        [FromQuery] string? type,
        [FromQuery] string? severity,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is not Guid tenantId)
            return Results.Unauthorized();

        var query = db.SecurityEvents.AsNoTracking().Where(e => e.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(type))
            query = query.Where(e => e.EventType == type);
        if (!string.IsNullOrWhiteSpace(severity))
            query = query.Where(e => e.Severity == severity);
        // A burst that overlaps the window counts: it was still going on inside it.
        if (from is DateTimeOffset start)
            query = query.Where(e => e.LastSeenAt >= start);
        if (to is DateTimeOffset end)
            query = query.Where(e => e.FirstSeenAt <= end);

        var total = await query.CountAsync(cancellationToken);
        var currentPage = Math.Max(page ?? 1, 1);
        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var rows = await query
            .OrderByDescending(e => e.LastSeenAt)
            .ThenByDescending(e => e.Id)
            .Skip((currentPage - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken);

        return Results.Ok(new SecurityEventPage(total, currentPage, size, rows.Select(Map).ToList()));
    }

    private static SecurityEventItem Map(SecurityEvent e) => new(
        e.Id,
        e.EventType,
        SecurityEventTypes.Find(e.EventType)?.Name ?? e.EventType,
        e.Severity,
        e.Description,
        e.IpAddress,
        e.ActorObjectId,
        e.ActorName,
        e.Path,
        e.Count,
        e.FirstSeenAt,
        e.LastSeenAt);
}
