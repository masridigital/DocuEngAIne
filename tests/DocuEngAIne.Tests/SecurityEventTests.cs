using System.Net;
using System.Net.Http.Json;
using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Infrastructure.Audit;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using DocuEngAIne.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocuEngAIne.Tests;

/// <summary>The recorder: repeats merge into one row with a count, and only registered kinds are recorded.</summary>
public class SecurityEventRecorderTests
{
    private static DocuEngAIneDbContext Open(Guid tenantId)
        => new(
            new DbContextOptionsBuilder<DocuEngAIneDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new FakeCurrentUser { TenantId = tenantId, ObjectId = "admin" });

    [Fact]
    public async Task Repeats_Within_The_Window_Are_One_Row_With_A_Count()
    {
        var tenantId = Guid.NewGuid();
        await using var db = Open(tenantId);
        var recorder = new SecurityEventRecorder(db);
        var t0 = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

        await recorder.RecordAsync(tenantId, SecurityEventTypes.IpBlocked, "blocked", "198.51.100.7", "oid-1", "Tech One", "/api/a", t0);
        await recorder.RecordAsync(tenantId, SecurityEventTypes.IpBlocked, "blocked", "198.51.100.7", "oid-1", "Tech One", "/api/b", t0.AddMinutes(5));
        // Another address, and the same address after the window, are new rows.
        await recorder.RecordAsync(tenantId, SecurityEventTypes.IpBlocked, "blocked", "198.51.100.8", "oid-1", "Tech One", "/api/a", t0.AddMinutes(6));
        await recorder.RecordAsync(tenantId, SecurityEventTypes.IpBlocked, "blocked", "198.51.100.7", "oid-1", "Tech One", "/api/c", t0.AddMinutes(30));

        var rows = await db.SecurityEvents.AsNoTracking().OrderBy(e => e.FirstSeenAt).ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.Equal(2, rows[0].Count);
        Assert.Equal(t0, rows[0].FirstSeenAt);
        Assert.Equal(t0.AddMinutes(5), rows[0].LastSeenAt);
        Assert.Equal("/api/b", rows[0].Path);
        Assert.Equal(SecurityEventTypes.SeverityWarning, rows[0].Severity);
        Assert.Equal("198.51.100.8", rows[1].IpAddress);
        Assert.Equal(1, rows[2].Count);
    }

    [Fact]
    public async Task Only_Registered_Kinds_Can_Be_Recorded()
    {
        var tenantId = Guid.NewGuid();
        await using var db = Open(tenantId);
        await Assert.ThrowsAsync<ArgumentException>(
            () => new SecurityEventRecorder(db).RecordAsync(tenantId, "auth.login.failed", "nope"));
        Assert.Empty(await db.SecurityEvents.ToListAsync());
    }

    [Fact]
    public async Task Retention_Purges_Events_Last_Seen_Before_The_Window()
    {
        var tenantId = Guid.NewGuid();
        await using var db = Open(tenantId);
        var now = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        var recorder = new SecurityEventRecorder(db);
        await recorder.RecordAsync(tenantId, SecurityEventTypes.TokenExpired, "old", utcNow: now.AddDays(-400));
        await recorder.RecordAsync(tenantId, SecurityEventTypes.TokenExpired, "recent", "192.0.2.1", utcNow: now.AddDays(-10));

        var retention = new AuditRetentionService(db, new ConfigurationBuilder().Build());
        Assert.Equal(1, await retention.PurgeSecurityEventsAsync(now));
        Assert.Equal("recent", Assert.Single(await db.SecurityEvents.ToListAsync()).Description);
    }
}

/// <summary>Each kind of refusal lands in the tenant's log, through the real pipeline.</summary>
public class SecurityEventPipelineTests : IClassFixture<TestHost>
{
    private readonly TestHost _host;

    public SecurityEventPipelineTests(TestHost host) => _host = host;

    private List<SecurityEvent> EventsFor(Guid tenantId)
    {
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocuEngAIneDbContext>();
        return db.SecurityEvents.AsNoTracking().Where(e => e.TenantId == tenantId).ToList();
    }

    private Guid SeedTenant(Action<Tenant> configure, params string[] cidrs)
    {
        var tenantId = Guid.NewGuid();
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocuEngAIneDbContext>();
        var tenant = new Tenant { Id = tenantId, Name = "Watched", Slug = $"sec-{tenantId:N}" };
        configure(tenant);
        db.Tenants.Add(tenant);
        foreach (var cidr in cidrs)
            db.IpAllowlistEntries.Add(new IpAllowlistEntry { TenantId = tenantId, Cidr = cidr });
        db.SaveChanges();
        return tenantId;
    }

    private async Task<int> SendAsync(string method, string path, string? remoteIp, Action<HttpContext>? headers = null)
    {
        var context = await _host.Server.SendAsync(ctx =>
        {
            ctx.Request.Method = method;
            ctx.Request.Path = path;
            ctx.Connection.RemoteIpAddress = remoteIp is null ? null : IPAddress.Parse(remoteIp);
            headers?.Invoke(ctx);
        });
        return context.Response.StatusCode;
    }

    [Fact]
    public async Task An_Ip_Allowlist_Block_Is_Recorded_Once_With_A_Count()
    {
        var tenantId = SeedTenant(t => t.IpAllowlistEnabled = true, "203.0.113.0/24");
        var ticket = TestAuthHandler.EncodeTicket("sec-admin", tenantId, nameof(UserRole.Admin));

        for (var i = 0; i < 2; i++)
        {
            var status = await SendAsync(HttpMethods.Get, "/api/companies", "198.51.100.7",
                ctx => ctx.Request.Headers.Authorization = $"{TestAuthHandler.SchemeName} {ticket}");
            Assert.Equal(StatusCodes.Status403Forbidden, status);
        }

        var recorded = Assert.Single(EventsFor(tenantId));
        Assert.Equal(SecurityEventTypes.IpBlocked, recorded.EventType);
        Assert.Equal(SecurityEventTypes.SeverityWarning, recorded.Severity);
        Assert.Equal("198.51.100.7", recorded.IpAddress);
        Assert.Equal("sec-admin", recorded.ActorObjectId);
        Assert.Equal(2, recorded.Count);
    }

    [Fact]
    public async Task A_Closed_Tenant_Refusal_Is_Recorded_In_Its_Own_Log()
    {
        var tenantId = SeedTenant(t =>
        {
            t.Status = TenantStatus.Suspended;
            t.StatusReason = "Invoice overdue";
        });
        var ticket = TestAuthHandler.EncodeTicket("sec-user", tenantId, nameof(UserRole.Admin));

        var status = await SendAsync(HttpMethods.Get, "/api/companies", "192.0.2.10",
            ctx => ctx.Request.Headers.Authorization = $"{TestAuthHandler.SchemeName} {ticket}");

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        var recorded = Assert.Single(EventsFor(tenantId));
        Assert.Equal(SecurityEventTypes.TenantClosedAccess, recorded.EventType);
        Assert.Equal(SecurityEventTypes.SeverityInfo, recorded.Severity);
        Assert.Contains("suspended", recorded.Description);
    }

    [Fact]
    public async Task A_Deactivated_User_Refusal_Is_Recorded()
    {
        using var client = _host.CreateSuspendedAdminClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/documents")).StatusCode);

        var recorded = Assert.Single(EventsFor(_host.TenantAId), e => e.ActorObjectId == _host.SuspendedAdminObjectId);
        Assert.Equal(SecurityEventTypes.DeactivatedUserAccess, recorded.EventType);
        Assert.Equal(SecurityEventTypes.SeverityWarning, recorded.Severity);
    }

    [Fact]
    public async Task Revoked_And_Expired_Api_Tokens_Are_Recorded_And_Unknown_Ones_Are_Not()
    {
        var tenantId = SeedTenant(_ => { });
        var revoked = new ApiToken
        {
            TenantId = tenantId,
            Name = "old agent",
            TokenHash = ApiTokenHasher.Hash("revoked-plaintext"),
            TokenPrefix = "revo",
            RevokedAt = DateTimeOffset.UtcNow.AddDays(-1),
        };
        var expired = new ApiToken
        {
            TenantId = tenantId,
            Name = "trial agent",
            TokenHash = ApiTokenHasher.Hash("expired-plaintext"),
            TokenPrefix = "expi",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1),
        };
        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocuEngAIneDbContext>();
            db.ApiTokens.AddRange(revoked, expired);
            db.SaveChanges();
        }

        async Task<HttpStatusCode> CallAsync(string plaintext)
        {
            using var client = _host.CreateAnonymousClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", plaintext);
            var response = await client.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = "1", method = "tools/list" });
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync("revoked-plaintext"));
        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync("expired-plaintext"));
        Assert.Equal(HttpStatusCode.Unauthorized, await CallAsync("nobody-knows-this"));

        var events = EventsFor(tenantId);
        Assert.Equal(2, events.Count);
        var revokedEvent = Assert.Single(events, e => e.EventType == SecurityEventTypes.TokenRevoked);
        Assert.Equal(SecurityEventTypes.SeverityCritical, revokedEvent.Severity);
        Assert.Equal($"apitoken:{revoked.Id:D}", revokedEvent.ActorObjectId);
        Assert.Equal("old agent", revokedEvent.ActorName);
        Assert.Single(events, e => e.EventType == SecurityEventTypes.TokenExpired && e.ActorName == "trial agent");
    }

    [Fact]
    public async Task The_Log_Is_Admin_Only_And_Shows_Only_The_Callers_Tenant()
    {
        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DocuEngAIneDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.SecurityEvents.AddRange(
                new SecurityEvent { TenantId = _host.TenantAId, EventType = SecurityEventTypes.IpBlocked, Severity = SecurityEventTypes.SeverityWarning, Description = "own", IpAddress = "192.0.2.50", FirstSeenAt = now, LastSeenAt = now },
                new SecurityEvent { TenantId = _host.TenantBId, EventType = SecurityEventTypes.IpBlocked, Severity = SecurityEventTypes.SeverityWarning, Description = "poison", FirstSeenAt = now, LastSeenAt = now });
            db.SaveChanges();
        }

        using (var reader = _host.CreateReaderClient())
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/security-events")).StatusCode);

        using var owner = _host.CreateOwnerClient();
        var page = await owner.GetFromJsonAsync<SecurityEventEndpoints.SecurityEventPage>("/api/security-events?type=ip.blocked");
        Assert.NotNull(page);
        Assert.Contains(page.Items, i => i.Description == "own" && i.Name == "Refused by the IP allowlist");
        Assert.DoesNotContain(page.Items, i => i.Description == "poison");

        var types = await owner.GetFromJsonAsync<List<SecurityEventDefinition>>("/api/security-events/types");
        Assert.NotNull(types);
        Assert.Equal(SecurityEventTypes.All.Count, types.Count);
    }
}
