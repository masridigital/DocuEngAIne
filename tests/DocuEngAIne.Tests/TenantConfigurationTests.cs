using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using DocuEngAIne.Infrastructure.Security;
using DocuEngAIne.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocuEngAIne.Tests;

/// <summary>
/// Tenant configuration: the registered feature catalog (defaults on, stored choices win, unknown
/// keys refused), terminology overrides that are validated and stored as overrides only, an accent
/// color that must stay readable, and every change audited with what it moved from.
/// </summary>
public class TenantConfigurationTests
{
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

    private static async Task<(DocuEngAIneDbContext Db, FakeCurrentUser User, Guid TenantId)> SeedAsync(string dbName, bool onboarded = true)
    {
        var tenantId = Guid.NewGuid();
        var user = new FakeCurrentUser { TenantId = tenantId, ObjectId = "admin-oid", Email = "admin@example.com", Role = UserRole.Admin };
        var db = new DocuEngAIneDbContext(new DbContextOptionsBuilder<DocuEngAIneDbContext>().UseInMemoryDatabase(dbName).Options, user);
        if (onboarded)
        {
            db.Tenants.Add(new Tenant { Id = tenantId, Name = "MSP", Slug = $"msp-{tenantId:N}" });
            await db.SaveChangesAsync();
        }

        return (db, user, tenantId);
    }

    private static TenantFeatureService Features(DocuEngAIneDbContext db) => new(db, new MemoryCache(new MemoryCacheOptions()));

    private static int StatusOf(IResult? result)
        => result is IStatusCodeHttpResult s && s.StatusCode is int code ? code : 0;

    private static T ValueOf<T>(IResult result) => Assert.IsAssignableFrom<T>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);

    [Fact]
    public async Task Features_Default_On_And_A_Stored_Choice_Wins()
    {
        var (db, user, tenantId) = await SeedAsync(nameof(Features_Default_On_And_A_Stored_Choice_Wins));
        var features = Features(db);
        var audit = new RecordingAudit();

        Assert.All(TenantFeatures.All, f => Assert.True(f.EnabledByDefault));
        Assert.True(await features.IsEnabledAsync(tenantId, TenantFeatures.AiAssistant));
        await Assert.ThrowsAsync<ArgumentException>(() => features.IsEnabledAsync(tenantId, "security_events"));

        var off = await TenantConfigurationEndpoints.SetFeatureAsync(
            TenantFeatures.AiAssistant, new TenantConfigurationEndpoints.SetFeatureRequest(false), db, user, features, audit);
        Assert.Equal(200, StatusOf(off));
        Assert.False(ValueOf<List<TenantConfigurationEndpoints.FeatureView>>(off).Single(f => f.Key == TenantFeatures.AiAssistant).Enabled);
        Assert.False(await features.IsEnabledAsync(tenantId, TenantFeatures.AiAssistant));
        Assert.True(await features.IsEnabledAsync(tenantId, TenantFeatures.ClientPortal));

        var entry = Assert.Single(audit.Entries);
        Assert.Equal("Tenant.SetFeature", entry.Action);
        Assert.Equal(AuditCategories.System, entry.Category);
        Assert.Equal("""{"features.ai_assistant":{"from":true,"to":false}}""", entry.ChangesJson);

        // Saying the same thing again changes nothing and is not audited again.
        await TenantConfigurationEndpoints.SetFeatureAsync(
            TenantFeatures.AiAssistant, new TenantConfigurationEndpoints.SetFeatureRequest(false), db, user, features, audit);
        Assert.Single(audit.Entries);
        Assert.Single(await db.TenantFeatureSettings.ToListAsync());

        Assert.Equal(404, StatusOf(await TenantConfigurationEndpoints.SetFeatureAsync(
            "security_events", new TenantConfigurationEndpoints.SetFeatureRequest(false), db, user, features, audit)));
    }

    [Fact]
    public async Task A_Tenant_Not_Yet_Onboarded_Reads_Every_Default_And_Cannot_Be_Configured()
    {
        var (db, user, _) = await SeedAsync(nameof(A_Tenant_Not_Yet_Onboarded_Reads_Every_Default_And_Cannot_Be_Configured), onboarded: false);
        var features = Features(db);

        var view = ValueOf<TenantConfigurationEndpoints.TenantConfigurationView>(
            await TenantConfigurationEndpoints.GetAsync(db, user, features));
        Assert.All(view.Features, f => Assert.True(f.Enabled));
        Assert.Equal("Companies", view.Terminology.Single(t => t.Key == TenantTerms.Company).Plural);
        Assert.Null(view.Branding.DisplayName);

        Assert.Equal(404, StatusOf(await TenantConfigurationEndpoints.SetFeatureAsync(
            TenantFeatures.McpServer, new TenantConfigurationEndpoints.SetFeatureRequest(false), db, user, features)));
        Assert.Equal(404, StatusOf(await TenantConfigurationEndpoints.SetBrandingAsync(
            new TenantConfigurationEndpoints.SetBrandingRequest("Acme IT", null), db, user)));
    }

    [Fact]
    public async Task Terminology_Is_Validated_And_Only_Overrides_Are_Stored()
    {
        var (db, user, tenantId) = await SeedAsync(nameof(Terminology_Is_Validated_And_Only_Overrides_Are_Stored));
        var audit = new RecordingAudit();

        var renamed = await TenantConfigurationEndpoints.SetTerminologyAsync(
            new TenantConfigurationEndpoints.SetTerminologyRequest(new()
            {
                [TenantTerms.Company] = new("  Client ", "Client   accounts"),
                [TenantTerms.Asset] = new("Asset", "Assets"),
            }),
            db, user, audit);
        Assert.Equal(200, StatusOf(renamed));
        var company = ValueOf<List<TenantConfigurationEndpoints.TermView>>(renamed).Single(t => t.Key == TenantTerms.Company);
        Assert.Equal(("Client", "Client accounts", "Company"), (company.Singular, company.Plural, company.DefaultSingular));

        // The asset term matched its default, so only the company override is kept.
        var tenant = await db.Tenants.SingleAsync(t => t.Id == tenantId);
        Assert.Equal("""{"company":{"singular":"Client","plural":"Client accounts"}}""", tenant.TerminologyJson);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("Tenant.SetTerminology", entry.Action);
        Assert.Contains("\"terminology.company.singular\":{\"from\":\"Company\",\"to\":\"Client\"}", entry.ChangesJson);
        Assert.DoesNotContain("terminology.asset", entry.ChangesJson);

        Assert.Equal(400, StatusOf(await TenantConfigurationEndpoints.SetTerminologyAsync(
            new TenantConfigurationEndpoints.SetTerminologyRequest(new() { ["contact"] = new("Person", "People") }), db, user, audit)));
        Assert.Equal(400, StatusOf(await TenantConfigurationEndpoints.SetTerminologyAsync(
            new TenantConfigurationEndpoints.SetTerminologyRequest(new() { [TenantTerms.Asset] = new("Device", null) }), db, user, audit)));
        Assert.Equal(400, StatusOf(await TenantConfigurationEndpoints.SetTerminologyAsync(
            new TenantConfigurationEndpoints.SetTerminologyRequest(new() { [TenantTerms.Asset] = new(new string('x', 41), "Devices") }), db, user, audit)));
        Assert.Equal(400, StatusOf(await TenantConfigurationEndpoints.SetTerminologyAsync(
            new TenantConfigurationEndpoints.SetTerminologyRequest(new() { [TenantTerms.Asset] = new("Dev\u0007ice", "Devices") }), db, user, audit)));
        Assert.Single(audit.Entries);

        // A null entry restores the default, leaving nothing stored.
        await TenantConfigurationEndpoints.SetTerminologyAsync(
            new TenantConfigurationEndpoints.SetTerminologyRequest(new() { [TenantTerms.Company] = null }), db, user, audit);
        Assert.Null((await db.Tenants.SingleAsync(t => t.Id == tenantId)).TerminologyJson);
        Assert.Equal(2, audit.Entries.Count);
    }

    [Fact]
    public void Damaged_Stored_Terminology_Falls_Back_To_The_Defaults()
    {
        Assert.Empty(TenantAppearance.ParseTerms("{not json"));
        Assert.Empty(TenantAppearance.ParseTerms("""{"contact":{"singular":"Person","plural":"People"}}"""));
        Assert.Empty(TenantAppearance.ParseTerms("""{"company":{"singular":"","plural":"Clients"}}"""));
        var parsed = TenantAppearance.ParseTerms("""{"company":{"singular":"Client","plural":"Clients"}}""");
        Assert.Equal("Clients", parsed[TenantTerms.Company].Plural);
    }

    [Theory]
    [InlineData("#38BDF8", true, "#38bdf8")]
    [InlineData("  #f59e0b ", true, "#f59e0b")]
    [InlineData("#ffffff", true, "#ffffff")]
    [InlineData("", true, null)]
    [InlineData(null, true, null)]
    [InlineData("#1e3a8a", false, null)]
    [InlineData("#000000", false, null)]
    [InlineData("red", false, null)]
    [InlineData("#38bdf", false, null)]
    [InlineData("#38bdf8; background:url(x)", false, null)]
    public void Accent_Colors_Must_Read_On_The_Dark_Background(string? input, bool accepted, string? stored)
    {
        Assert.Equal(accepted, TenantAppearance.TryNormalizeColor(input, out var color, out var error));
        Assert.Equal(stored, color);
        Assert.Equal(accepted, error is null);
    }

    [Fact]
    public void Contrast_Is_Measured_The_Wcag_Way()
    {
        Assert.Equal(21.0, TenantAppearance.ContrastRatio("#ffffff", "#000000"), 1);
        Assert.Equal(1.0, TenantAppearance.ContrastRatio("#38bdf8", "#38bdf8"), 3);
        Assert.True(TenantAppearance.ContrastRatio("#38bdf8", TenantAppearance.Background) >= TenantAppearance.MinimumContrast);
    }

    [Fact]
    public async Task Branding_Is_Replaced_Validated_And_Audited()
    {
        var (db, user, tenantId) = await SeedAsync(nameof(Branding_Is_Replaced_Validated_And_Audited));
        var audit = new RecordingAudit();

        var set = await TenantConfigurationEndpoints.SetBrandingAsync(
            new TenantConfigurationEndpoints.SetBrandingRequest("  Acme   IT ", "#F59E0B"), db, user, audit);
        Assert.Equal(200, StatusOf(set));
        Assert.Equal(new TenantConfigurationEndpoints.BrandingView("Acme IT", "#f59e0b"), ValueOf<TenantConfigurationEndpoints.BrandingView>(set));
        Assert.Contains("\"branding.accentColor\":{\"from\":null,\"to\":\"#f59e0b\"}", Assert.Single(audit.Entries).ChangesJson);

        Assert.Equal(400, StatusOf(await TenantConfigurationEndpoints.SetBrandingAsync(
            new TenantConfigurationEndpoints.SetBrandingRequest("Acme IT", "#111111"), db, user, audit)));
        Assert.Equal(400, StatusOf(await TenantConfigurationEndpoints.SetBrandingAsync(
            new TenantConfigurationEndpoints.SetBrandingRequest(new string('n', 81), null), db, user, audit)));
        Assert.Equal("#f59e0b", (await db.Tenants.SingleAsync(t => t.Id == tenantId)).AccentColor);

        // Nulls clear both, back to the defaults.
        var cleared = await TenantConfigurationEndpoints.SetBrandingAsync(
            new TenantConfigurationEndpoints.SetBrandingRequest(null, null), db, user, audit);
        Assert.Equal(new TenantConfigurationEndpoints.BrandingView(null, null), ValueOf<TenantConfigurationEndpoints.BrandingView>(cleared));
        Assert.Equal(2, audit.Entries.Count);
    }

    [Fact]
    public async Task Mcp_Tokens_Read_Nothing_While_The_Mcp_Server_Is_Off()
    {
        var (db, user, tenantId) = await SeedAsync(nameof(Mcp_Tokens_Read_Nothing_While_The_Mcp_Server_Is_Off));
        var plaintext = ApiTokenHasher.GeneratePlaintext();
        db.ApiTokens.Add(new ApiToken
        {
            TenantId = tenantId,
            Name = "agent",
            TokenHash = ApiTokenHasher.Hash(plaintext),
            TokenPrefix = ApiTokenHasher.PublicPrefix(plaintext),
        });
        await db.SaveChangesAsync();
        var features = Features(db);
        var allowlist = new IpAllowlistService(db, new MemoryCache(new MemoryCacheOptions()), new ConfigurationBuilder().Build());

        HttpContext Ping()
        {
            var http = new DefaultHttpContext();
            http.Request.Headers.Authorization = $"Bearer {plaintext}";
            http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"ping"}"""));
            return http;
        }

        await TenantConfigurationEndpoints.SetFeatureAsync(
            TenantFeatures.McpServer, new TenantConfigurationEndpoints.SetFeatureRequest(false), db, user, features);
        var blocked = await OutboundMcpEndpoints.HandlePostAsync(Ping(), db, new RecordingAudit(), allowlist, CancellationToken.None, features);
        Assert.Equal(403, StatusOf(blocked));
        Assert.Contains(TenantFeatures.DisabledError, JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(blocked).Value));

        await TenantConfigurationEndpoints.SetFeatureAsync(
            TenantFeatures.McpServer, new TenantConfigurationEndpoints.SetFeatureRequest(true), db, user, features);
        var allowed = await OutboundMcpEndpoints.HandlePostAsync(Ping(), db, new RecordingAudit(), allowlist, CancellationToken.None, features);
        Assert.NotEqual(403, StatusOf(allowed));
        Assert.NotEqual(401, StatusOf(allowed));
    }
}

/// <summary>
/// Feature switches on the real request pipeline: a switched-off feature answers 403 on every route
/// it covers, only for its own tenant, while the switch itself stays reachable (and Admin-only).
/// Each test uses its own tenant so switches never overlap.
/// </summary>
public class TenantFeaturePipelineTests : IClassFixture<TestHost>
{
    private readonly TestHost _host;

    public TenantFeaturePipelineTests(TestHost host) => _host = host;

    private Guid SeedTenant()
    {
        var tenantId = Guid.NewGuid();
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocuEngAIneDbContext>();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Configured", Slug = $"cfg-{tenantId:N}" });
        db.SaveChanges();
        return tenantId;
    }

    private HttpClient Admin(Guid tenantId)
        => _host.CreateAuthenticatedClient($"cfg-admin-{tenantId:N}", tenantId, nameof(UserRole.Admin));

    private HttpClient Member(Guid tenantId)
        => _host.CreateAuthenticatedClient($"cfg-member-{tenantId:N}", tenantId);

    private static async Task<bool> FeatureStateAsync(HttpClient client, string key)
    {
        using var document = JsonDocument.Parse(await client.GetStringAsync("/api/tenant/configuration"));
        return document.RootElement.GetProperty("features").EnumerateArray()
            .Single(f => f.GetProperty("key").GetString() == key)
            .GetProperty("enabled").GetBoolean();
    }

    [Fact]
    public async Task Turning_Off_The_Ai_Assistant_Blocks_Chat_But_Not_Its_Settings()
    {
        var tenantId = SeedTenant();
        using var admin = Admin(tenantId);
        var chat = new LlmChatRequest([new LlmChatMessageRequest("user", "hello")]);

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/tenant/features/ai_assistant", new { enabled = false })).StatusCode);
        Assert.False(await FeatureStateAsync(admin, TenantFeatures.AiAssistant));

        var blocked = await admin.PostAsJsonAsync("/api/llm/chat", chat);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        var body = await blocked.Content.ReadAsStringAsync();
        Assert.Contains(TenantFeatures.DisabledError, body);
        Assert.Contains(TenantFeatures.AiAssistant, body);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/llm/config")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/tenant/features/ai_assistant", new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/llm/chat", chat)).StatusCode);
    }

    [Fact]
    public async Task The_Portal_Switch_Is_Admin_Only_And_Covers_Every_Portal_Route_Of_Its_Tenant()
    {
        var tenantId = SeedTenant();
        using var admin = Admin(tenantId);
        using var member = Member(tenantId);

        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/portal/companies")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PutAsJsonAsync("/api/tenant/features/client_portal", new { enabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/tenant/features/client_portal", new { enabled = false })).StatusCode);

        var blocked = await member.GetAsync("/api/portal/companies");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Contains(TenantFeatures.ClientPortal, await blocked.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/portal")).StatusCode);

        // The configuration itself is never behind a feature, and other tenants are untouched.
        Assert.False(await FeatureStateAsync(member, TenantFeatures.ClientPortal));
        using var otherTenant = _host.CreateOwnerClient();
        Assert.Equal(HttpStatusCode.OK, (await otherTenant.GetAsync("/api/portal/companies")).StatusCode);
    }
}
