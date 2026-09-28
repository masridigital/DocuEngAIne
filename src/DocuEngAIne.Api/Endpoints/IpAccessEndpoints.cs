using System.Net;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using DocuEngAIne.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

/// <summary>
/// Admin management of the tenant IP allowlist, with the anti-lockout rules that make it safe to
/// turn on: it cannot be enabled with no active entries, it cannot be enabled unless the
/// administrator's own current IP is covered, and while it is on no edit / deactivate / delete may
/// leave that IP uncovered (the rule checks the entry set as it would be after the change).
/// </summary>
public static class IpAccessEndpoints
{
    public const string NoActiveEntriesMessage = "Add at least one active entry before turning the allowlist on.";
    public const string InvalidCidrMessage = "Enter an IP address or CIDR range, e.g. 203.0.113.7 or 203.0.113.0/24.";
    public const string DuplicateCidrMessage = "That network is already on the allowlist.";

    public static IEndpointRouteBuilder MapIpAccessEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tenant/ip-access").RequireAuthorization(AuthExtensions.AdminPolicy);

        group.MapGet("", GetAsync);
        group.MapPut("/policy", SetPolicyAsync);
        group.MapPost("/entries", AddEntryAsync);
        group.MapPut("/entries/{id:guid}", UpdateEntryAsync);
        group.MapDelete("/entries/{id:guid}", DeleteEntryAsync);

        return app;
    }

    public sealed record SetPolicyRequest(bool Enabled);
    public sealed record AddEntryRequest(string? Cidr, string? Label = null, bool IsActive = true);
    public sealed record UpdateEntryRequest(string? Cidr = null, string? Label = null, bool? IsActive = null);

    public sealed record EntryView(Guid Id, string Cidr, string? Label, bool IsActive, DateTimeOffset CreatedAt);

    public sealed record IpAccessView(
        bool Enabled,
        bool BreakGlass,
        string? CurrentIp,
        bool CurrentIpCovered,
        IReadOnlyList<EntryView> Entries);

    public static async Task<IResult> GetAsync(
        HttpContext http,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IpAllowlistService allowlist,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is not Guid tenantId)
            return Results.Unauthorized();

        var enabled = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => t.IpAllowlistEnabled)
            .FirstOrDefaultAsync(cancellationToken);
        var entries = await LoadEntriesAsync(db, user, cancellationToken);
        var client = IpAllowlist.Normalize(http.Connection.RemoteIpAddress);

        return Results.Ok(new IpAccessView(
            enabled,
            allowlist.BreakGlass,
            client?.ToString(),
            IpAllowlist.Contains(ActiveNetworks(entries), client),
            entries.Select(Map).ToList()));
    }

    public static async Task<IResult> SetPolicyAsync(
        [FromBody] SetPolicyRequest request,
        HttpContext http,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IpAllowlistService allowlist,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is not Guid tenantId)
            return Results.Unauthorized();

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null)
            return Results.NotFound();
        if (tenant.IpAllowlistEnabled == request.Enabled)
            return Results.NoContent();

        if (request.Enabled)
        {
            var active = ActiveNetworks(await LoadEntriesAsync(db, user, cancellationToken));
            if (active.Count == 0)
                return Results.BadRequest(NoActiveEntriesMessage);

            var client = IpAllowlist.Normalize(http.Connection.RemoteIpAddress);
            if (!IpAllowlist.Contains(active, client))
                return Results.BadRequest(NotCoveredMessage(client, "turning the allowlist on"));
        }

        tenant.IpAllowlistEnabled = request.Enabled;
        await db.SaveChangesAsync(cancellationToken);
        allowlist.Invalidate(tenantId);

        await LogAsync(audit, request.Enabled ? "IpAccess.Enable" : "IpAccess.Disable", nameof(Tenant), tenantId,
            request.Enabled ? "IP allowlist enforcement turned on" : "IP allowlist enforcement turned off", cancellationToken);
        return Results.NoContent();
    }

    public static async Task<IResult> AddEntryAsync(
        [FromBody] AddEntryRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IpAllowlistService allowlist,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is not Guid tenantId)
            return Results.Unauthorized();

        if (!IpAllowlist.TryNormalize(request.Cidr, out _, out var normalized))
            return Results.BadRequest(InvalidCidrMessage);

        if (await db.IpAllowlistEntries.ForTenant(user).AnyAsync(e => e.Cidr == normalized, cancellationToken))
            return Results.Conflict(new { error = DuplicateCidrMessage });

        var entry = new IpAllowlistEntry
        {
            TenantId = tenantId,
            Cidr = normalized,
            Label = CleanLabel(request.Label),
            IsActive = request.IsActive,
            CreatedByObjectId = user.ObjectId,
        };
        db.IpAllowlistEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        allowlist.Invalidate(tenantId);

        await LogAsync(audit, "IpAccess.AddEntry", nameof(IpAllowlistEntry), entry.Id,
            $"Added {normalized}{(entry.IsActive ? "" : " (inactive)")}", cancellationToken, entry.Label ?? normalized);
        return Results.Created($"/api/tenant/ip-access/entries/{entry.Id}", Map(entry));
    }

    public static async Task<IResult> UpdateEntryAsync(
        Guid id,
        [FromBody] UpdateEntryRequest request,
        HttpContext http,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IpAllowlistService allowlist,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is not Guid tenantId)
            return Results.Unauthorized();

        var entries = await LoadEntriesAsync(db, user, cancellationToken, tracked: true);
        var entry = entries.FirstOrDefault(e => e.Id == id);
        if (entry is null)
            return Results.NotFound();

        var newCidr = entry.Cidr;
        if (request.Cidr is not null)
        {
            if (!IpAllowlist.TryNormalize(request.Cidr, out _, out newCidr))
                return Results.BadRequest(InvalidCidrMessage);
            if (entries.Any(e => e.Id != id && e.Cidr == newCidr))
                return Results.Conflict(new { error = DuplicateCidrMessage });
        }

        var newActive = request.IsActive ?? entry.IsActive;

        // Simulate the entry set after this change before touching anything.
        var after = entries
            .Where(e => e.Id != id)
            .Append(new IpAllowlistEntry { TenantId = tenantId, Cidr = newCidr, IsActive = newActive })
            .ToList();
        if (await WouldLockOutAsync(db, tenantId, http, after, cancellationToken) is { } refused)
            return refused;

        var before = $"{entry.Cidr}{(entry.IsActive ? "" : " (inactive)")}";
        entry.Cidr = newCidr;
        entry.IsActive = newActive;
        if (request.Label is not null)
            entry.Label = CleanLabel(request.Label);
        await db.SaveChangesAsync(cancellationToken);
        allowlist.Invalidate(tenantId);

        await LogAsync(audit, "IpAccess.UpdateEntry", nameof(IpAllowlistEntry), entry.Id,
            $"{before} → {entry.Cidr}{(entry.IsActive ? "" : " (inactive)")}", cancellationToken, entry.Label ?? entry.Cidr);
        return Results.Ok(Map(entry));
    }

    public static async Task<IResult> DeleteEntryAsync(
        Guid id,
        HttpContext http,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IpAllowlistService allowlist,
        IAuditService? audit = null,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is not Guid tenantId)
            return Results.Unauthorized();

        var entries = await LoadEntriesAsync(db, user, cancellationToken, tracked: true);
        var entry = entries.FirstOrDefault(e => e.Id == id);
        if (entry is null)
            return Results.NotFound();

        if (await WouldLockOutAsync(db, tenantId, http, entries.Where(e => e.Id != id).ToList(), cancellationToken) is { } refused)
            return refused;

        db.IpAllowlistEntries.Remove(entry);
        await db.SaveChangesAsync(cancellationToken);
        allowlist.Invalidate(tenantId);

        await LogAsync(audit, "IpAccess.RemoveEntry", nameof(IpAllowlistEntry), entry.Id,
            $"Removed {entry.Cidr}", cancellationToken, entry.Label ?? entry.Cidr);
        return Results.NoContent();
    }

    /// <summary>While enforcement is on, refuses any entry set that no longer covers the caller.</summary>
    private static async Task<IResult?> WouldLockOutAsync(
        DocuEngAIneDbContext db,
        Guid tenantId,
        HttpContext http,
        IReadOnlyList<IpAllowlistEntry> after,
        CancellationToken cancellationToken)
    {
        var enabled = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => t.IpAllowlistEnabled)
            .FirstOrDefaultAsync(cancellationToken);
        if (!enabled)
            return null;

        var client = IpAllowlist.Normalize(http.Connection.RemoteIpAddress);
        var active = ActiveNetworks(after);
        if (active.Count == 0)
            return Results.BadRequest("That change would leave no active entries while the allowlist is on. Turn the allowlist off first.");
        return IpAllowlist.Contains(active, client) ? null : Results.BadRequest(NotCoveredMessage(client, "this change"));
    }

    private static string NotCoveredMessage(IPAddress? client, string action)
        => $"Your current IP ({client?.ToString() ?? "unknown"}) would not be covered after {action} — you would lock yourself out. Add an entry covering it first.";

    private static async Task<List<IpAllowlistEntry>> LoadEntriesAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken,
        bool tracked = false)
    {
        var query = db.IpAllowlistEntries.ForTenant(user);
        if (!tracked)
            query = query.AsNoTracking();
        return await query.OrderBy(e => e.Cidr).ToListAsync(cancellationToken);
    }

    private static List<IPNetwork> ActiveNetworks(IEnumerable<IpAllowlistEntry> entries)
    {
        var networks = new List<IPNetwork>();
        foreach (var entry in entries.Where(e => e.IsActive))
        {
            if (IpAllowlist.TryNormalize(entry.Cidr, out var network, out _))
                networks.Add(network);
        }

        return networks;
    }

    private static string? CleanLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return null;
        var trimmed = label.Trim();
        return trimmed.Length <= 100 ? trimmed : trimmed[..100];
    }

    private static Task LogAsync(
        IAuditService? audit,
        string action,
        string entityType,
        Guid entityId,
        string details,
        CancellationToken cancellationToken,
        string? label = null)
        => audit is null
            ? Task.CompletedTask
            : audit.LogAsync(
                new AuditEntry(action, entityType, entityId, details, Category: AuditCategories.Security, TargetLabel: label),
                cancellationToken);

    private static EntryView Map(IpAllowlistEntry e) => new(e.Id, e.Cidr, e.Label, e.IsActive, e.CreatedAt);
}
