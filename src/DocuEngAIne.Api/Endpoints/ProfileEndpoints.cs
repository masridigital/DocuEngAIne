using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

public static class ProfileEndpoints
{
    public const int RecentTake = 10;

    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").RequireAuthorization();

        group.MapGet("/me/recents", async (
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            if (user.TenantId is null)
                return Results.Unauthorized();

            var items = await ListRecentsAsync(db, user, cancellationToken);
            return Results.Ok(items);
        });

        group.MapGet("/me", async (
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            if (user.TenantId is null)
                return Results.Unauthorized();

            var dbUser = await db.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.TenantId == user.TenantId.Value && u.EntraObjectId == user.ObjectId, cancellationToken);

            if (dbUser is null)
            {
                // Auto-provision user on first login if tenant exists.
                var tenantExists = await db.Tenants.AnyAsync(t => t.Id == user.TenantId.Value, cancellationToken);
                if (!tenantExists)
                {
                    return Results.Ok(new
                    {
                        user.ObjectId,
                        user.Email,
                        user.DisplayName,
                        user.TenantId,
                        OnboardingRequired = true,
                    });
                }

                dbUser = new User
                {
                    TenantId = user.TenantId.Value,
                    EntraObjectId = user.ObjectId!,
                    Email = user.Email ?? "unknown",
                    DisplayName = user.DisplayName,
                    // Signing in never confers admin rights. The tenant's first Owner is granted
                    // deliberately by POST /api/tenant/onboard; bootstrapping here instead made the
                    // grant a race won by whichever member's browser revalidated first.
                    Role = UserRole.Reader,
                };

                db.Users.Add(dbUser);
                await db.SaveChangesAsync(cancellationToken);
            }

            dbUser.LastSeenAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            var tenant = await db.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == user.TenantId.Value, cancellationToken);

            return Results.Ok(new
            {
                dbUser.Id,
                dbUser.EntraObjectId,
                dbUser.Email,
                dbUser.DisplayName,
                dbUser.Role,
                dbUser.LastSeenAt,
                Tenant = tenant is null ? null : new { tenant.Id, tenant.Name, tenant.Slug, tenant.PrimaryDomain },
            });
        });

        group.MapGet("/tenant/settings", async (
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            if (user.TenantId is null)
                return Results.Unauthorized();

            var tenant = await db.Tenants
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == user.TenantId.Value, cancellationToken);

            return tenant is null ? Results.NotFound() : Results.Ok(new
            {
                tenant.Id,
                tenant.Name,
                tenant.Slug,
                tenant.PrimaryDomain,
                tenant.IsActive,
                tenant.CreatedAt,
            });
        });

        return app;
    }

    public static async Task<IReadOnlyList<RecentItem>> ListRecentsAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var assets = await db.Assets.ForTenant(user).AsNoTracking()
            .OrderByDescending(a => a.UpdatedAt)
            .ThenByDescending(a => a.Id)
            .Take(RecentTake)
            .Select(a => new RecentCandidate(FlagEntityType.Asset, a.Id, a.Name, a.CompanyId, a.UpdatedAt))
            .ToListAsync(cancellationToken);

        var documents = await db.Documents.ForTenant(user).AsNoTracking()
            .OrderByDescending(d => d.UpdatedAt)
            .ThenByDescending(d => d.Id)
            .Take(RecentTake)
            .Select(d => new RecentCandidate(FlagEntityType.Document, d.Id, d.Title, d.CompanyId, d.UpdatedAt))
            .ToListAsync(cancellationToken);

        var runbooks = await db.Runbooks.ForTenant(user).AsNoTracking()
            .OrderByDescending(r => r.UpdatedAt)
            .ThenByDescending(r => r.Id)
            .Take(RecentTake)
            .Select(r => new RecentCandidate(FlagEntityType.Runbook, r.Id, r.Title, r.CompanyId, r.UpdatedAt))
            .ToListAsync(cancellationToken);

        var merged = assets
            .Concat(documents)
            .Concat(runbooks)
            .OrderByDescending(i => i.UpdatedAt)
            .ThenByDescending(i => i.Id)
            .Take(RecentTake)
            .ToList();

        if (merged.Count == 0)
            return [];

        var companyIds = merged.Where(i => i.CompanyId.HasValue).Select(i => i.CompanyId!.Value).Distinct().ToList();
        var companyNames = companyIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.Companies.ForTenant(user).AsNoTracking()
                .Where(c => companyIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);

        return merged.Select(i => new RecentItem(
            i.EntityType,
            i.Id,
            i.Name,
            i.CompanyId,
            i.CompanyId is Guid cid && companyNames.TryGetValue(cid, out var name) ? name : null,
            i.UpdatedAt)).ToList();
    }

    private readonly record struct RecentCandidate(
        string EntityType,
        Guid Id,
        string Name,
        Guid? CompanyId,
        DateTimeOffset UpdatedAt);
}

public sealed record RecentItem(
    string EntityType,
    Guid Id,
    string Name,
    Guid? CompanyId,
    string? CompanyName,
    DateTimeOffset UpdatedAt);
