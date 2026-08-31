using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Tests;

public class ApiTokenTests
{
    private sealed class RecordingAudit : IAuditService
    {
        public List<(string Action, string EntityType, Guid? EntityId, string? Details)> Entries { get; } = [];

        public Task LogAsync(string action, string entityType, Guid? entityId = null, string? details = null, CancellationToken cancellationToken = default)
        {
            Entries.Add((action, entityType, entityId, details));
            return Task.CompletedTask;
        }
    }

    private static (DocuEngAIneDbContext Db, FakeCurrentUser User, RecordingAudit Audit) Open(
        Guid tenantId,
        string? dbName = null,
        UserRole role = UserRole.Owner)
    {
        var user = new FakeCurrentUser
        {
            TenantId = tenantId,
            ObjectId = Guid.NewGuid().ToString(),
            Role = role,
        };
        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString())
            .Options;
        var db = new DocuEngAIneDbContext(options, user);
        return (db, user, new RecordingAudit());
    }

    private static async Task SeedTenantAsync(DocuEngAIneDbContext db, Guid tenantId, string slug)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId))
            db.Tenants.Add(new Tenant { Id = tenantId, Name = slug, Slug = slug, IsActive = true });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static int StatusOf(IResult result)
        => result is IStatusCodeHttpResult s && s.StatusCode is int code ? code : 0;

    private static T ValueOf<T>(IResult result)
    {
        var value = Assert.IsAssignableFrom<IValueHttpResult>(result);
        return Assert.IsType<T>(value.Value);
    }

    [Fact]
    public void Hash_Is_Sha256_Hex_And_Does_Not_Contain_Plaintext()
    {
        var plaintext = "dea_" + new string('a', 64);
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext))).ToLowerInvariant();

        var hash = ApiTokenHasher.Hash(plaintext);

        Assert.Equal(expected, hash);
        Assert.Equal(ApiTokenHasher.HashHexLength, hash.Length);
        Assert.DoesNotContain(plaintext, hash, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(plaintext, hash);
    }

    [Fact]
    public void Hash_Is_Deterministic_And_Differs_For_Different_Tokens()
    {
        var a = ApiTokenHasher.GeneratePlaintext();
        var b = ApiTokenHasher.GeneratePlaintext();

        Assert.StartsWith(ApiTokenHasher.PlaintextPrefix, a);
        Assert.NotEqual(a, b);
        Assert.Equal(ApiTokenHasher.Hash(a), ApiTokenHasher.Hash(a));
        Assert.NotEqual(ApiTokenHasher.Hash(a), ApiTokenHasher.Hash(b));
        Assert.Equal(ApiTokenHasher.PublicPrefixLength, ApiTokenHasher.PublicPrefix(a).Length);
    }

    [Fact]
    public async Task Create_Stores_Hash_Not_Plaintext_And_Returns_Plaintext_Once()
    {
        var tenantId = Guid.NewGuid();
        var (db, user, audit) = Open(tenantId);
        await using (db)
        {
            await SeedTenantAsync(db, tenantId, "acme");

            var created = await ApiTokenEndpoints.CreateAsync(
                new CreateApiTokenRequest("Cursor"), db, user, audit);

            Assert.Equal(StatusCodes.Status201Created, StatusOf(created));
            var body = ValueOf<CreatedApiTokenResponse>(created);
            Assert.Equal("Cursor", body.Name);
            Assert.StartsWith(ApiTokenHasher.PlaintextPrefix, body.Token);
            Assert.Equal(ApiTokenHasher.PublicPrefix(body.Token), body.Prefix);

            var stored = await db.ApiTokens.AsNoTracking().SingleAsync();
            Assert.Equal(tenantId, stored.TenantId);
            Assert.Equal(ApiTokenHasher.Hash(body.Token), stored.TokenHash);
            Assert.NotEqual(body.Token, stored.TokenHash);
            Assert.DoesNotContain(body.Token, stored.TokenHash);
            Assert.Equal(body.Prefix, stored.TokenPrefix);
            Assert.Null(stored.RevokedAt);
            Assert.Contains(audit.Entries, e => e.Action == "ApiToken.Create" && e.Details != null && !e.Details.Contains(body.Token));

            var listed = await ApiTokenEndpoints.ListAsync(db, user);
            Assert.Single(listed);
            Assert.Equal(stored.Id, listed[0].Id);
            Assert.Equal(body.Prefix, listed[0].Prefix);
            var listedJson = JsonSerializer.Serialize(listed);
            Assert.DoesNotContain(body.Token, listedJson);
            Assert.DoesNotContain(stored.TokenHash, listedJson);
        }
    }

    [Fact]
    public async Task Create_Requires_Name_And_Uses_Caller_Tenant()
    {
        var tenantId = Guid.NewGuid();
        var (db, user, audit) = Open(tenantId);
        await using (db)
        {
            await SeedTenantAsync(db, tenantId, "acme");

            var missing = await ApiTokenEndpoints.CreateAsync(new CreateApiTokenRequest("  "), db, user, audit);
            Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(missing));
            Assert.Empty(await db.ApiTokens.ToListAsync());

            user.TenantId = null;
            var noTenant = await ApiTokenEndpoints.CreateAsync(new CreateApiTokenRequest("x"), db, user, audit);
            Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf(noTenant));
        }
    }

    [Fact]
    public async Task Revoke_Is_ForTenant_And_Stops_Authenticate()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        string plaintext;
        Guid tokenId;
        var (dbA, userA, auditA) = Open(tenantA, dbName);
        await using (dbA)
        {
            await SeedTenantAsync(dbA, tenantA, "a");
            var created = await ApiTokenEndpoints.CreateAsync(new CreateApiTokenRequest("A"), dbA, userA, auditA);
            var body = ValueOf<CreatedApiTokenResponse>(created);
            plaintext = body.Token;
            tokenId = body.Id;
        }

        var (dbB, userB, auditB) = Open(tenantB, dbName);
        await using (dbB)
        {
            await SeedTenantAsync(dbB, tenantB, "b");
            var foreign = await ApiTokenEndpoints.RevokeAsync(tokenId, dbB, userB, auditB);
            Assert.Equal(StatusCodes.Status404NotFound, StatusOf(foreign));
            Assert.Null((await dbB.ApiTokens.AsNoTracking().SingleAsync(t => t.Id == tokenId)).RevokedAt);
        }

        var (dbRevoke, userRevoke, auditRevoke) = Open(tenantA, dbName);
        await using (dbRevoke)
        {
            var revoked = await ApiTokenEndpoints.RevokeAsync(tokenId, dbRevoke, userRevoke, auditRevoke);
            Assert.Equal(StatusCodes.Status204NoContent, StatusOf(revoked));
            Assert.NotNull((await dbRevoke.ApiTokens.AsNoTracking().SingleAsync(t => t.Id == tokenId)).RevokedAt);
            Assert.Contains(auditRevoke.Entries, e => e.Action == "ApiToken.Revoke");
        }

        var (dbAuth, _, _) = Open(tenantA, dbName);
        await using (dbAuth)
        {
            Assert.Null(await ApiTokenAuthenticator.AuthenticateAsync(plaintext, dbAuth));
        }
    }

    [Fact]
    public async Task Authenticate_Maps_Hash_To_TokenCurrentUser_For_Tenant()
    {
        var tenantId = Guid.NewGuid();
        var (db, user, audit) = Open(tenantId);
        await using (db)
        {
            await SeedTenantAsync(db, tenantId, "acme");
            var created = await ApiTokenEndpoints.CreateAsync(new CreateApiTokenRequest("Harness"), db, user, audit);
            var body = ValueOf<CreatedApiTokenResponse>(created);

            var mapped = await ApiTokenAuthenticator.AuthenticateAsync(body.Token, db);
            Assert.NotNull(mapped);
            Assert.Equal(tenantId, mapped.TenantId);
            Assert.Equal(body.Id, mapped.TokenId);
            Assert.True(mapped.IsAuthenticated);
            Assert.StartsWith("apitoken:", mapped.ObjectId);
            Assert.True(mapped.HasRole(UserRole.Reader));
            Assert.False(mapped.HasRole(UserRole.Contributor));
            Assert.False(mapped.HasRole(UserRole.Admin));

            Assert.Null(await ApiTokenAuthenticator.AuthenticateAsync("dea_not-a-real-token", db));
            Assert.Null(await ApiTokenAuthenticator.AuthenticateAsync(null, db));
            Assert.Equal("dea_abc", ApiTokenAuthenticator.ReadPresentedToken("Bearer dea_abc", null));
            Assert.Equal("dea_xyz", ApiTokenAuthenticator.ReadPresentedToken("Bearer ignored", "dea_xyz"));
        }
    }

    [Fact]
    public async Task Create_With_Expiry_Sets_ExpiresAt_And_Expired_Token_Fails_Authentication()
    {
        var tenantId = Guid.NewGuid();
        var (db, user, audit) = Open(tenantId);
        await using (db)
        {
            await SeedTenantAsync(db, tenantId, "acme");

            var invalid = await ApiTokenEndpoints.CreateAsync(new CreateApiTokenRequest("bad", ExpiresInDays: 0), db, user, audit);
            Assert.Equal(StatusCodes.Status400BadRequest, StatusOf(invalid));

            var created = await ApiTokenEndpoints.CreateAsync(new CreateApiTokenRequest("expiring", ExpiresInDays: 30), db, user, audit);
            var body = ValueOf<CreatedApiTokenResponse>(created);
            Assert.NotNull(body.ExpiresAt);
            Assert.Equal(DateTimeOffset.UtcNow.AddDays(30), body.ExpiresAt.Value, TimeSpan.FromMinutes(5));

            var listed = await ApiTokenEndpoints.ListAsync(db, user);
            Assert.Equal(body.ExpiresAt, Assert.Single(listed).ExpiresAt);

            // Live until the expiry passes, dead after it.
            Assert.NotNull(await ApiTokenAuthenticator.AuthenticateAsync(body.Token, db));

            var stored = await db.ApiTokens.SingleAsync(t => t.Id == body.Id);
            stored.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();

            Assert.Null(await ApiTokenAuthenticator.AuthenticateAsync(body.Token, db));
        }
    }

    [Fact]
    public async Task Authenticate_Throttles_LastUsedAt_Writes()
    {
        var tenantId = Guid.NewGuid();
        var (db, user, audit) = Open(tenantId);
        await using (db)
        {
            await SeedTenantAsync(db, tenantId, "acme");
            var created = await ApiTokenEndpoints.CreateAsync(new CreateApiTokenRequest("busy"), db, user, audit);
            var body = ValueOf<CreatedApiTokenResponse>(created);

            Assert.NotNull(await ApiTokenAuthenticator.AuthenticateAsync(body.Token, db));
            var firstSeen = (await db.ApiTokens.AsNoTracking().SingleAsync(t => t.Id == body.Id)).LastUsedAt;
            Assert.NotNull(firstSeen);

            // A fresh LastUsedAt is not rewritten on every call — MCP clients authenticate many
            // times a minute and last-used is an audit hint, not a metric.
            Assert.NotNull(await ApiTokenAuthenticator.AuthenticateAsync(body.Token, db));
            Assert.Equal(firstSeen, (await db.ApiTokens.AsNoTracking().SingleAsync(t => t.Id == body.Id)).LastUsedAt);

            var stored = await db.ApiTokens.SingleAsync(t => t.Id == body.Id);
            stored.LastUsedAt = DateTimeOffset.UtcNow - ApiTokenAuthenticator.LastUsedWriteInterval - TimeSpan.FromMinutes(1);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            Assert.NotNull(await ApiTokenAuthenticator.AuthenticateAsync(body.Token, db));
            var refreshed = (await db.ApiTokens.AsNoTracking().SingleAsync(t => t.Id == body.Id)).LastUsedAt;
            Assert.True(refreshed > firstSeen);
        }
    }

    [Fact]
    public async Task AuditService_Persists_Token_And_System_Actor_Identity()
    {
        var tenantId = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();
        var (db, _, _) = Open(tenantId, dbName);
        await using (db)
        {
            await SeedTenantAsync(db, tenantId, "acme");

            // Token and scheduler identities have no Users row, so UserId stays null — before
            // ActorObjectId, their audit rows were completely anonymous.
            var tokenUser = new TokenCurrentUser(tenantId, Guid.NewGuid(), "mcp");
            var tokenDb = new DocuEngAIneDbContext(
                new DbContextOptionsBuilder<DocuEngAIneDbContext>().UseInMemoryDatabase(dbName).Options,
                tokenUser);
            await using (tokenDb)
            {
                var audit = new AuditService(tokenDb, tokenUser, new HttpContextAccessor());
                await audit.LogAsync("KeeperLink.Reveal", nameof(KeeperLink), Guid.NewGuid());
            }

            var background = BackgroundCurrentUser.ForTenant(tenantId);
            var backgroundDb = new DocuEngAIneDbContext(
                new DbContextOptionsBuilder<DocuEngAIneDbContext>().UseInMemoryDatabase(dbName).Options,
                background);
            await using (backgroundDb)
            {
                var audit = new AuditService(backgroundDb, background, new HttpContextAccessor());
                await audit.LogAsync("Integration.Sync", nameof(IntegrationConnection), Guid.NewGuid());
            }

            var rows = await db.AuditLogs.AsNoTracking().OrderBy(a => a.Action).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(BackgroundCurrentUser.SystemObjectId, rows[0].ActorObjectId);
            Assert.StartsWith("apitoken:", rows[1].ActorObjectId);
            Assert.All(rows, r => Assert.Null(r.UserId));
            Assert.All(rows, r => Assert.Equal(tenantId, r.TenantId));
        }
    }

    [Fact]
    public void CurrentUser_Ambient_Scope_Supplies_Tenant_Without_Http_Jwt()
    {
        var tokenUser = new TokenCurrentUser(Guid.NewGuid(), Guid.NewGuid(), "scope-test");
        var httpUser = new CurrentUser(new HttpContextAccessor());

        Assert.Null(httpUser.TenantId);
        Assert.False(httpUser.IsAuthenticated);

        using (CurrentUserScope.Use(tokenUser))
        {
            Assert.Equal(tokenUser.TenantId, httpUser.TenantId);
            Assert.True(httpUser.IsAuthenticated);
            Assert.Equal(tokenUser.ObjectId, httpUser.ObjectId);
            Assert.True(httpUser.HasRole(UserRole.Reader));
            Assert.False(httpUser.HasRole(UserRole.Admin));
        }

        Assert.Null(httpUser.TenantId);
        Assert.False(httpUser.IsAuthenticated);
    }
}
