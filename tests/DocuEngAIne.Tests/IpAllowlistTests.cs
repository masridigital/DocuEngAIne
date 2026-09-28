using System.Net;
using System.Text;
using System.Text.Json;
using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Api.Middleware;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using DocuEngAIne.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocuEngAIne.Tests;

/// <summary>
/// The tenant IP allowlist: CIDR parsing that means exactly what the admin typed, a policy that
/// fails closed, and the anti-lockout rules — it cannot be turned on without an active entry
/// covering the caller, and while it is on no edit, deactivation or removal may uncover the caller.
/// </summary>
public class IpAllowlistTests
{
    private const string Covered = "203.0.113.9";
    private const string Outside = "198.51.100.7";

    private sealed class RecordingAudit : IAuditService
    {
        public List<AuditEntry> Entries { get; } = [];

        public Task LogAsync(string action, string entityType, Guid? entityId = null, string? details = null, CancellationToken cancellationToken = default)
        {
            Entries.Add(new AuditEntry(action, entityType, entityId, details));
            return Task.CompletedTask;
        }

        public Task LogAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private static (DocuEngAIneDbContext Db, FakeCurrentUser User) Open(string dbName, Guid tenantId)
    {
        var user = new FakeCurrentUser
        {
            TenantId = tenantId,
            ObjectId = $"admin-{tenantId:N}",
            Email = "admin@example.com",
            Role = UserRole.Admin,
        };
        var db = new DocuEngAIneDbContext(
            new DbContextOptionsBuilder<DocuEngAIneDbContext>().UseInMemoryDatabase(dbName).Options, user);
        return (db, user);
    }

    private static async Task<(DocuEngAIneDbContext Db, FakeCurrentUser User, Guid TenantId)> SeedAsync(
        string dbName, bool enabled, params (string Cidr, bool Active)[] entries)
    {
        var tenantId = Guid.NewGuid();
        var (db, user) = Open(dbName, tenantId);
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", Slug = $"t-{tenantId:N}", IpAllowlistEnabled = enabled });
        foreach (var (cidr, active) in entries)
            db.IpAllowlistEntries.Add(new IpAllowlistEntry { TenantId = tenantId, Cidr = cidr, IsActive = active });
        await db.SaveChangesAsync();
        return (db, user, tenantId);
    }

    private static IpAllowlistService Service(DocuEngAIneDbContext db, bool breakGlass = false)
    {
        var settings = new Dictionary<string, string?>();
        if (breakGlass)
            settings[IpAllowlistService.DisableKey] = "true";
        return new IpAllowlistService(
            db,
            new MemoryCache(new MemoryCacheOptions()),
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    private static HttpContext From(string? ip)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = ip is null ? null : IPAddress.Parse(ip);
        return http;
    }

    private static int StatusOf(IResult result)
        => result is IStatusCodeHttpResult s && s.StatusCode is int code ? code : 0;

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7/32")]
    [InlineData("  203.0.113.7  ", "203.0.113.7/32")]
    [InlineData("10.0.0.5/24", "10.0.0.0/24")]
    [InlineData("10.0.0.0/8", "10.0.0.0/8")]
    [InlineData("0.0.0.0/0", "0.0.0.0/0")]
    [InlineData("2001:db8::1/32", "2001:db8::/32")]
    [InlineData("2001:DB8::1", "2001:db8::1/128")]
    [InlineData("::ffff:198.51.100.7", "198.51.100.7/32")]
    public void Normalize_Masks_Host_Bits_To_One_Canonical_Form(string input, string expected)
    {
        Assert.True(IpAllowlist.TryNormalize(input, out var network, out var normalized));
        Assert.Equal(expected, normalized);
        Assert.Equal(expected, network.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-ip")]
    [InlineData("10")]
    [InlineData("10.1")]
    [InlineData("010.0.0.1")]
    [InlineData("0x0a.0.0.1")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("10.0.0.0/+8")]
    [InlineData("10.0.0.0/")]
    [InlineData("10.0.0.0/ 8")]
    [InlineData("10.0.0.0/8/8")]
    [InlineData("/24")]
    [InlineData("2001:db8::/129")]
    public void Normalize_Rejects_Anything_But_An_Exact_Address_Or_Range(string? input)
    {
        Assert.False(IpAllowlist.TryNormalize(input, out _, out _));
    }

    [Fact]
    public void Contains_Matches_Both_Families_And_Fails_Closed()
    {
        Assert.True(IpAllowlist.TryNormalize("203.0.113.0/24", out var v4, out _));
        Assert.True(IpAllowlist.TryNormalize("2001:db8::/32", out var v6, out _));
        var networks = new[] { v4, v6 };

        Assert.True(IpAllowlist.Contains(networks, IPAddress.Parse("203.0.113.200")));
        Assert.True(IpAllowlist.Contains(networks, IPAddress.Parse("::ffff:203.0.113.200")));
        Assert.True(IpAllowlist.Contains(networks, IPAddress.Parse("2001:db8:1::5")));
        Assert.False(IpAllowlist.Contains(networks, IPAddress.Parse("203.0.114.1")));
        Assert.False(IpAllowlist.Contains(networks, IPAddress.Parse("2001:db9::1")));
        Assert.False(IpAllowlist.Contains(networks, null));
        Assert.False(IpAllowlist.Contains(Array.Empty<IPNetwork>(), IPAddress.Parse("203.0.113.200")));
    }

    [Fact]
    public async Task Policy_Is_Open_When_Off_And_Only_Admits_Active_Entries_When_On()
    {
        var (db, _, tenantId) = await SeedAsync(
            nameof(Policy_Is_Open_When_Off_And_Only_Admits_Active_Entries_When_On),
            enabled: false,
            ("203.0.113.0/24", true),
            ("192.0.2.0/24", false));
        var service = Service(db);

        Assert.True(await service.IsAllowedAsync(tenantId, IPAddress.Parse(Outside)));

        var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId);
        tenant.IpAllowlistEnabled = true;
        await db.SaveChangesAsync();
        service.Invalidate(tenantId);

        Assert.True(await service.IsAllowedAsync(tenantId, IPAddress.Parse(Covered)));
        Assert.False(await service.IsAllowedAsync(tenantId, IPAddress.Parse(Outside)));
        Assert.False(await service.IsAllowedAsync(tenantId, IPAddress.Parse("192.0.2.1")));
        Assert.False(await service.IsAllowedAsync(tenantId, null));
    }

    [Fact]
    public async Task Policy_Ignores_Other_Tenants_Entries()
    {
        var dbName = nameof(Policy_Ignores_Other_Tenants_Entries);
        var (dbA, _, tenantA) = await SeedAsync(dbName, enabled: true, ("192.0.2.0/24", true));
        await SeedAsync(dbName, enabled: true, ("203.0.113.0/24", true));

        Assert.False(await Service(dbA).IsAllowedAsync(tenantA, IPAddress.Parse(Covered)));
    }

    [Fact]
    public async Task Break_Glass_Setting_Turns_Enforcement_Off()
    {
        var (db, _, tenantId) = await SeedAsync(
            nameof(Break_Glass_Setting_Turns_Enforcement_Off), enabled: true, ("203.0.113.0/24", true));

        Assert.False(await Service(db).IsAllowedAsync(tenantId, IPAddress.Parse(Outside)));
        Assert.True(await Service(db, breakGlass: true).IsAllowedAsync(tenantId, IPAddress.Parse(Outside)));
    }

    [Fact]
    public async Task Cannot_Turn_On_Without_An_Active_Entry()
    {
        var (db, user, tenantId) = await SeedAsync(
            nameof(Cannot_Turn_On_Without_An_Active_Entry), enabled: false, ("203.0.113.0/24", false));

        var result = await IpAccessEndpoints.SetPolicyAsync(new(true), From(Covered), db, user, Service(db));

        Assert.Equal(IpAccessEndpoints.NoActiveEntriesMessage, Assert.IsType<BadRequest<string>>(result).Value);
        Assert.False((await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId)).IpAllowlistEnabled);
    }

    [Theory]
    [InlineData(Outside)]
    [InlineData(null)]
    public async Task Cannot_Turn_On_Unless_The_Callers_Address_Is_Covered(string? callerIp)
    {
        var (db, user, tenantId) = await SeedAsync(
            $"{nameof(Cannot_Turn_On_Unless_The_Callers_Address_Is_Covered)}-{callerIp}",
            enabled: false,
            ("203.0.113.0/24", true));

        var result = await IpAccessEndpoints.SetPolicyAsync(new(true), From(callerIp), db, user, Service(db));

        Assert.Contains(callerIp ?? "unknown", Assert.IsType<BadRequest<string>>(result).Value);
        Assert.False((await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenantId)).IpAllowlistEnabled);
    }

    [Fact]
    public async Task Turning_On_And_Off_Is_Audited_And_Applies_At_Once()
    {
        var (db, user, tenantId) = await SeedAsync(
            nameof(Turning_On_And_Off_Is_Audited_And_Applies_At_Once), enabled: false, ("203.0.113.0/24", true));
        var service = Service(db);
        var audit = new RecordingAudit();

        // Prime the cache while off; the toggle must not wait out the cache.
        Assert.True(await service.IsAllowedAsync(tenantId, IPAddress.Parse(Outside)));

        Assert.Equal(204, StatusOf(await IpAccessEndpoints.SetPolicyAsync(new(true), From(Covered), db, user, service, audit)));
        Assert.False(await service.IsAllowedAsync(tenantId, IPAddress.Parse(Outside)));

        Assert.Equal(204, StatusOf(await IpAccessEndpoints.SetPolicyAsync(new(false), From(Covered), db, user, service, audit)));
        Assert.True(await service.IsAllowedAsync(tenantId, IPAddress.Parse(Outside)));

        Assert.Equal(new[] { "IpAccess.Enable", "IpAccess.Disable" }, audit.Entries.Select(e => e.Action).ToArray());
        Assert.All(audit.Entries, e => Assert.Equal(AuditCategories.Security, e.Category));
    }

    [Fact]
    public async Task Add_Stores_The_Normalized_Network_And_Refuses_Invalid_Or_Duplicate_Input()
    {
        var (db, user, tenantId) = await SeedAsync(
            nameof(Add_Stores_The_Normalized_Network_And_Refuses_Invalid_Or_Duplicate_Input), enabled: false);
        var service = Service(db);
        var audit = new RecordingAudit();

        var created = Assert.IsType<Created<IpAccessEndpoints.EntryView>>(
            await IpAccessEndpoints.AddEntryAsync(new("10.0.0.5/24", "  Office  "), db, user, service, audit));
        Assert.Equal("10.0.0.0/24", created.Value!.Cidr);
        Assert.Equal("Office", created.Value.Label);

        var invalid = await IpAccessEndpoints.AddEntryAsync(new("10.0.0.0/33"), db, user, service, audit);
        Assert.Equal(IpAccessEndpoints.InvalidCidrMessage, Assert.IsType<BadRequest<string>>(invalid).Value);
        Assert.Equal(409, StatusOf(await IpAccessEndpoints.AddEntryAsync(new("10.0.0.0/24"), db, user, service, audit)));
        Assert.Equal(409, StatusOf(await IpAccessEndpoints.AddEntryAsync(new("10.0.0.77/24"), db, user, service, audit)));

        var stored = await db.IpAllowlistEntries.AsNoTracking().SingleAsync();
        Assert.Equal(tenantId, stored.TenantId);
        Assert.Equal(user.ObjectId, stored.CreatedByObjectId);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("IpAccess.AddEntry", entry.Action);
        Assert.Equal(AuditCategories.Security, entry.Category);
    }

    [Fact]
    public async Task While_On_No_Change_May_Leave_The_Caller_Uncovered()
    {
        var (db, user, _) = await SeedAsync(
            nameof(While_On_No_Change_May_Leave_The_Caller_Uncovered),
            enabled: true,
            ("203.0.113.0/24", true),
            ("198.51.100.0/24", true));
        var service = Service(db);
        var mine = await db.IpAllowlistEntries.SingleAsync(e => e.Cidr == "203.0.113.0/24");
        var other = await db.IpAllowlistEntries.SingleAsync(e => e.Cidr == "198.51.100.0/24");

        Assert.IsType<BadRequest<string>>(
            await IpAccessEndpoints.UpdateEntryAsync(mine.Id, new(IsActive: false), From(Covered), db, user, service));
        Assert.IsType<BadRequest<string>>(
            await IpAccessEndpoints.UpdateEntryAsync(mine.Id, new(Cidr: "192.0.2.0/24"), From(Covered), db, user, service));
        Assert.IsType<BadRequest<string>>(
            await IpAccessEndpoints.DeleteEntryAsync(mine.Id, From(Covered), db, user, service));

        var unchanged = await db.IpAllowlistEntries.AsNoTracking().SingleAsync(e => e.Id == mine.Id);
        Assert.Equal("203.0.113.0/24", unchanged.Cidr);
        Assert.True(unchanged.IsActive);

        // Narrowing that still covers the caller, and removing an entry the caller does not need, go through.
        Assert.Equal(200, StatusOf(
            await IpAccessEndpoints.UpdateEntryAsync(mine.Id, new(Cidr: "203.0.113.0/25", Label: "HQ"), From(Covered), db, user, service)));
        Assert.Equal(204, StatusOf(await IpAccessEndpoints.DeleteEntryAsync(other.Id, From(Covered), db, user, service)));

        // The last active entry cannot go while enforcement is on, even from an address it covers.
        Assert.IsType<BadRequest<string>>(
            await IpAccessEndpoints.DeleteEntryAsync(mine.Id, From(Covered), db, user, service));
    }

    [Fact]
    public async Task While_Off_Entries_Can_Be_Changed_Freely()
    {
        var (db, user, _) = await SeedAsync(
            nameof(While_Off_Entries_Can_Be_Changed_Freely), enabled: false, ("203.0.113.0/24", true));
        var service = Service(db);
        var entry = await db.IpAllowlistEntries.SingleAsync();

        Assert.Equal(200, StatusOf(
            await IpAccessEndpoints.UpdateEntryAsync(entry.Id, new(IsActive: false), From(Outside), db, user, service)));
        Assert.Equal(204, StatusOf(await IpAccessEndpoints.DeleteEntryAsync(entry.Id, From(Outside), db, user, service)));
    }

    [Fact]
    public async Task Update_Refuses_Invalid_And_Duplicate_Networks()
    {
        var (db, user, _) = await SeedAsync(
            nameof(Update_Refuses_Invalid_And_Duplicate_Networks),
            enabled: false,
            ("203.0.113.0/24", true),
            ("198.51.100.0/24", true));
        var service = Service(db);
        var entry = await db.IpAllowlistEntries.SingleAsync(e => e.Cidr == "203.0.113.0/24");

        var invalid = await IpAccessEndpoints.UpdateEntryAsync(entry.Id, new(Cidr: "203.0.113"), From(Covered), db, user, service);
        Assert.Equal(IpAccessEndpoints.InvalidCidrMessage, Assert.IsType<BadRequest<string>>(invalid).Value);
        Assert.Equal(409, StatusOf(
            await IpAccessEndpoints.UpdateEntryAsync(entry.Id, new(Cidr: "198.51.100.9/24"), From(Covered), db, user, service)));
    }

    [Fact]
    public async Task Entries_Are_Tenant_Scoped()
    {
        var dbName = nameof(Entries_Are_Tenant_Scoped);
        var (dbA, userA, _) = await SeedAsync(dbName, enabled: false, ("203.0.113.0/24", true));
        var (dbB, _, tenantB) = await SeedAsync(dbName, enabled: false, ("198.51.100.0/24", true));
        var foreign = await dbB.IpAllowlistEntries.AsNoTracking().SingleAsync(e => e.TenantId == tenantB);
        var service = Service(dbA);

        var view = Assert.IsType<Ok<IpAccessEndpoints.IpAccessView>>(
            await IpAccessEndpoints.GetAsync(From(Covered), dbA, userA, service)).Value!;
        Assert.Equal(new[] { "203.0.113.0/24" }, view.Entries.Select(e => e.Cidr).ToArray());

        Assert.Equal(404, StatusOf(
            await IpAccessEndpoints.UpdateEntryAsync(foreign.Id, new(IsActive: false), From(Covered), dbA, userA, service)));
        Assert.Equal(404, StatusOf(await IpAccessEndpoints.DeleteEntryAsync(foreign.Id, From(Covered), dbA, userA, service)));

        // Uniqueness is per tenant: A may list the network B already has.
        Assert.Equal(201, StatusOf(await IpAccessEndpoints.AddEntryAsync(new("198.51.100.0/24"), dbA, userA, service)));
    }

    [Fact]
    public async Task View_Reports_The_Callers_Address_As_The_Allowlist_Sees_It()
    {
        var (db, user, _) = await SeedAsync(
            nameof(View_Reports_The_Callers_Address_As_The_Allowlist_Sees_It),
            enabled: true,
            ("203.0.113.0/24", true),
            ("198.51.100.0/24", false));
        var service = Service(db);

        var mapped = Assert.IsType<Ok<IpAccessEndpoints.IpAccessView>>(
            await IpAccessEndpoints.GetAsync(From($"::ffff:{Covered}"), db, user, service)).Value!;
        Assert.True(mapped.Enabled);
        Assert.False(mapped.BreakGlass);
        Assert.Equal(Covered, mapped.CurrentIp);
        Assert.True(mapped.CurrentIpCovered);
        Assert.Equal(2, mapped.Entries.Count);

        var inactiveOnly = Assert.IsType<Ok<IpAccessEndpoints.IpAccessView>>(
            await IpAccessEndpoints.GetAsync(From(Outside), db, user, service)).Value!;
        Assert.False(inactiveOnly.CurrentIpCovered);
    }

    [Fact]
    public async Task Mcp_Token_Is_Refused_From_Outside_The_Allowlist()
    {
        var (db, _, tenantId) = await SeedAsync(
            nameof(Mcp_Token_Is_Refused_From_Outside_The_Allowlist), enabled: true, ("203.0.113.0/24", true));
        var plaintext = ApiTokenHasher.GeneratePlaintext();
        db.ApiTokens.Add(new ApiToken
        {
            TenantId = tenantId,
            Name = "harness",
            TokenHash = ApiTokenHasher.Hash(plaintext),
            TokenPrefix = ApiTokenHasher.PublicPrefix(plaintext),
        });
        await db.SaveChangesAsync();
        var service = Service(db);

        HttpContext Ping(string ip)
        {
            var http = From(ip);
            http.Request.Headers.Authorization = $"Bearer {plaintext}";
            http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"ping"}"""));
            return http;
        }

        var blocked = await OutboundMcpEndpoints.HandlePostAsync(Ping(Outside), db, new RecordingAudit(), service, CancellationToken.None);
        Assert.Equal(403, StatusOf(blocked));
        var body = JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(blocked).Value);
        Assert.Contains(IpAllowlistMiddleware.BlockedError, body);

        var allowed = await OutboundMcpEndpoints.HandlePostAsync(Ping(Covered), db, new RecordingAudit(), service, CancellationToken.None);
        Assert.NotEqual(403, StatusOf(allowed));
        Assert.NotEqual(401, StatusOf(allowed));
    }
}

/// <summary>
/// The allowlist on the real request pipeline: the middleware refuses a blocked address on every
/// tenant route before any handler runs, and leaves other tenants and anonymous routes alone.
/// Each test uses its own tenant so policies (and their cache entries) never overlap.
/// </summary>
public class IpAllowlistPipelineTests : IClassFixture<TestHost>
{
    private readonly TestHost _host;

    public IpAllowlistPipelineTests(TestHost host) => _host = host;

    private Guid SeedTenant(bool enabled, params string[] cidrs)
    {
        var tenantId = Guid.NewGuid();
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocuEngAIneDbContext>();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Allowlisted", Slug = $"ip-{tenantId:N}", IpAllowlistEnabled = enabled });
        foreach (var cidr in cidrs)
            db.IpAllowlistEntries.Add(new IpAllowlistEntry { TenantId = tenantId, Cidr = cidr });
        db.SaveChanges();
        return tenantId;
    }

    private async Task<(int Status, string Body)> GetAsync(string path, string? remoteIp, string? ticket)
    {
        var context = await _host.Server.SendAsync(ctx =>
        {
            ctx.Request.Method = HttpMethods.Get;
            ctx.Request.Path = path;
            if (ticket is not null)
                ctx.Request.Headers.Authorization = $"{TestAuthHandler.SchemeName} {ticket}";
            ctx.Connection.RemoteIpAddress = remoteIp is null ? null : IPAddress.Parse(remoteIp);
        });

        using var reader = new StreamReader(context.Response.Body);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }

    private static string AdminTicket(Guid tenantId)
        => TestAuthHandler.EncodeTicket($"ip-admin-{tenantId:N}", tenantId, nameof(UserRole.Admin));

    [Fact]
    public async Task Blocked_Address_Is_Refused_Before_Any_Handler()
    {
        var tenantId = SeedTenant(enabled: true, "203.0.113.0/24");

        var (status, body) = await GetAsync("/api/tenant/ip-access", "198.51.100.7", AdminTicket(tenantId));

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Contains(IpAllowlistMiddleware.BlockedError, body);
        Assert.Contains("198.51.100.7", body);
    }

    [Fact]
    public async Task Unknown_Address_Is_Refused()
    {
        var tenantId = SeedTenant(enabled: true, "203.0.113.0/24");

        var (status, body) = await GetAsync("/api/tenant/ip-access", null, AdminTicket(tenantId));

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Contains(IpAllowlistMiddleware.BlockedError, body);
    }

    [Fact]
    public async Task Covered_Address_Reaches_The_Handler()
    {
        var tenantId = SeedTenant(enabled: true, "203.0.113.0/24");

        var (status, body) = await GetAsync("/api/tenant/ip-access", "203.0.113.9", AdminTicket(tenantId));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Contains("\"currentIpCovered\":true", body);
        Assert.Contains("\"currentIp\":\"203.0.113.9\"", body);
    }

    [Fact]
    public async Task Other_Tenants_And_Anonymous_Routes_Are_Unaffected()
    {
        SeedTenant(enabled: true, "203.0.113.0/24");

        var (tenantA, _) = await GetAsync(
            "/api/tenant/ip-access",
            "198.51.100.7",
            TestAuthHandler.EncodeTicket(_host.OwnerObjectId, _host.TenantAId, nameof(UserRole.Owner)));
        Assert.Equal(StatusCodes.Status200OK, tenantA);

        var (health, _) = await GetAsync("/api/health/live", "198.51.100.7", ticket: null);
        Assert.Equal(StatusCodes.Status200OK, health);
    }
}
