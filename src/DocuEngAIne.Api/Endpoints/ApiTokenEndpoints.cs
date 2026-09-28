using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Endpoints;

/// <summary>
/// Admin create / list / revoke for per-tenant API tokens. These are the credentials the outbound
/// MCP server accepts — not Entra JWTs. The plaintext is returned once on create and is never
/// persisted; every later lookup is by SHA-256 hash.
/// </summary>
public static class ApiTokenEndpoints
{
    public const string NameRequiredMessage = "Name is required.";
    public const string ExpiryInPastMessage = "ExpiresInDays must be at least 1.";

    public static IEndpointRouteBuilder MapApiTokenEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tokens").RequireAuthorization(AuthExtensions.AdminPolicy);

        group.MapGet("", async (
            DocuEngAIneDbContext db,
            ICurrentUser user,
            CancellationToken cancellationToken) =>
        {
            if (user.TenantId is null)
                return Results.Unauthorized();

            return Results.Ok(await ListAsync(db, user, cancellationToken));
        });

        group.MapPost("", async (
            [FromBody] CreateApiTokenRequest? request,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            IAuditService audit,
            CancellationToken cancellationToken) =>
            await CreateAsync(request, db, user, audit, cancellationToken));

        group.MapDelete("/{id:guid}", async (
            Guid id,
            DocuEngAIneDbContext db,
            ICurrentUser user,
            IAuditService audit,
            CancellationToken cancellationToken) =>
            await RevokeAsync(id, db, user, audit, cancellationToken));

        return app;
    }

    public static async Task<IReadOnlyList<ApiTokenListItem>> ListAsync(
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        var tokens = await db.ApiTokens.ForTenant(user).AsNoTracking()
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(cancellationToken);
        return tokens.Select(MapList).ToList();
    }

    public static async Task<IResult> CreateAsync(
        CreateApiTokenRequest? request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IAuditService audit,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        var name = request?.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return Results.BadRequest(NameRequiredMessage);

        if (request?.ExpiresInDays is int days && days < 1)
            return Results.BadRequest(ExpiryInPastMessage);

        var plaintext = ApiTokenHasher.GeneratePlaintext();
        var token = new ApiToken
        {
            TenantId = user.TenantId.Value,
            Name = name,
            TokenHash = ApiTokenHasher.Hash(plaintext),
            TokenPrefix = ApiTokenHasher.PublicPrefix(plaintext),
            CreatedByObjectId = user.ObjectId,
            ExpiresAt = request?.ExpiresInDays is int d ? DateTimeOffset.UtcNow.AddDays(d) : null,
        };

        db.ApiTokens.Add(token);
        await db.SaveChangesAsync(cancellationToken);

        await audit.LogAsync(
            "ApiToken.Create",
            nameof(ApiToken),
            token.Id,
            $"Created token '{token.Name}' prefix={token.TokenPrefix}",
            cancellationToken);

        return Results.Created($"/api/tokens/{token.Id}", new CreatedApiTokenResponse(
            token.Id,
            token.Name,
            token.TokenPrefix,
            plaintext,
            token.CreatedAt,
            token.ExpiresAt));
    }

    public static async Task<IResult> RevokeAsync(
        Guid id,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        IAuditService audit,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        var token = await db.ApiTokens.ForTenant(user).FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (token is null)
            return Results.NotFound();

        if (token.RevokedAt is null)
        {
            token.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            await audit.LogAsync(
                "ApiToken.Revoke",
                nameof(ApiToken),
                token.Id,
                $"Revoked token '{token.Name}' prefix={token.TokenPrefix}",
                cancellationToken);
        }

        return Results.NoContent();
    }

    private static ApiTokenListItem MapList(ApiToken t) =>
        new(t.Id, t.Name, t.TokenPrefix, t.CreatedAt, t.LastUsedAt, t.RevokedAt, t.ExpiresAt);
}

/// <summary>Null <paramref name="ExpiresInDays"/> mints a non-expiring token — visible as such in the list.</summary>
public record CreateApiTokenRequest(string? Name = null, int? ExpiresInDays = null);

public sealed record CreatedApiTokenResponse(
    Guid Id,
    string Name,
    string Prefix,
    string Token,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt = null);

public sealed record ApiTokenListItem(
    Guid Id,
    string Name,
    string Prefix,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? ExpiresAt = null);
