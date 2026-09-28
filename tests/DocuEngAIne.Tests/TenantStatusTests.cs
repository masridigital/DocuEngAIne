using System.Net;
using System.Net.Http.Json;
using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Api.Middleware;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using DocuEngAIne.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocuEngAIne.Tests;

/// <summary>
/// The tenant lifecycle: platform operators (and only they) suspend, archive and reactivate
/// tenants; a suspension needs a reason; an operator cannot close their own tenant; every change is
/// in both the operator's and the affected tenant's audit trail; and a closed tenant's API tokens
/// stop working.
/// </summary>
public class TenantStatusTests
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

    private static DocuEngAIneDbContext Context(string dbName, ICurrentUser user)
        => new(new DbContextOptionsBuilder<DocuEngAIneDbContext>().UseInMemoryDatabase(dbName).Options, user);

    private static FakeCurrentUser UserOf(Guid tenantId, string objectId)
        => new() { TenantId = tenantId, ObjectId = objectId, Email = $"{objectId}@example.com", Role = UserRole.Owner };

    /// <summary>The operator's tenant and a customer tenant with one user and one company (seeded as the customer, so they are stamped with its tenant).</summary>
    private static async Task<(DocuEngAIneDbContext Db, FakeCurrentUser Operator, Guid OperatorTenant, Guid Customer)> SeedAsync(string dbName)
    {
        var operatorTenant = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var op = UserOf(operatorTenant, "operator-oid");
        var db = Context(dbName, op);
        db.Tenants.AddRange(
            new Tenant { Id = operatorTenant, Name = "Platform", Slug = $"p-{operatorTenant:N}" },
            new Tenant { Id = customer, Name = "Customer", Slug = $"c-{customer:N}" });
        await db.SaveChangesAsync();

        await using (var customerDb = Context(dbName, UserOf(customer, "customer-oid")))
        {
            customerDb.Users.Add(new User { TenantId = customer, EntraObjectId = "customer-oid", Email = "owner@customer.example", Role = UserRole.Owner });
            customerDb.Companies.Add(new Company { TenantId = customer, Name = "Client", Slug = "client" });
            await customerDb.SaveChangesAsync();
        }

        return (db, op, operatorTenant, customer);
    }

    private static TenantStatusService Statuses(DocuEngAIneDbContext db) => new(db, new MemoryCache(new MemoryCacheOptions()));

    private static int StatusOf(IResult? result)
        => result is IStatusCodeHttpResult s && s.StatusCode is int code ? code : 0;

    private static T ValueOf<T>(IResult result) => Assert.IsAssignableFrom<T>(Assert.IsAssignableFrom<IValueHttpResult>(result).Value);

    [Fact]
    public void Operators_Are_Tenant_And_Object_Id_Pairs_From_Configuration()
    {
        var tenant = Guid.NewGuid();
        var options = new PlatformOptions { Operators = [new PlatformOperator { TenantId = tenant, ObjectId = "OPERATOR-OID" }] };

        Assert.True(options.IsOperator(tenant, "operator-oid"));
        Assert.False(options.IsOperator(Guid.NewGuid(), "operator-oid"));
        Assert.False(options.IsOperator(tenant, "someone-else"));
        Assert.False(options.IsOperator(null, "operator-oid"));
        Assert.False(new PlatformOptions().IsOperator(tenant, "operator-oid"));
    }

    [Fact]
    public async Task The_Console_Lists_Every_Tenant_With_Its_Status_And_Size()
    {
        var (db, _, operatorTenant, customer) = await SeedAsync(nameof(The_Console_Lists_Every_Tenant_With_Its_Status_And_Size));

        var tenants = ValueOf<List<PlatformEndpoints.TenantSummary>>(await PlatformEndpoints.ListTenantsAsync(db));

        Assert.Equal(2, tenants.Count);
        var row = tenants.Single(t => t.Id == customer);
        Assert.Equal((TenantStatus.Active, 1, 1), (row.Status, row.ActiveUsers, row.Companies));
        Assert.Contains(tenants, t => t.Id == operatorTenant);
    }

    [Fact]
    public async Task Suspending_Needs_A_Reason_Is_Audited_In_Both_Tenants_And_Reactivating_Clears_It()
    {
        var (db, op, operatorTenant, customer) = await SeedAsync(nameof(Suspending_Needs_A_Reason_Is_Audited_In_Both_Tenants_And_Reactivating_Clears_It));
        var statuses = Statuses(db);
        var audit = new RecordingAudit();

        Assert.Equal(400, StatusOf(await PlatformEndpoints.SuspendAsync(customer, new(null), db, op, statuses, audit)));
        Assert.Equal(409, StatusOf(await PlatformEndpoints.SuspendAsync(operatorTenant, new("Testing"), db, op, statuses, audit)));
        Assert.Equal(404, StatusOf(await PlatformEndpoints.SuspendAsync(Guid.NewGuid(), new("Testing"), db, op, statuses, audit)));

        var suspended = ValueOf<PlatformEndpoints.TenantSummary>(
            await PlatformEndpoints.SuspendAsync(customer, new("  Unpaid invoice  "), db, op, statuses, audit));
        Assert.Equal((TenantStatus.Suspended, "Unpaid invoice"), (suspended.Status, suspended.StatusReason));
        Assert.Equal(new TenantStatusService.State(TenantStatus.Suspended, "Unpaid invoice"), await statuses.GetAsync(customer));

        // The customer's own trail says who closed it and why; the operator's says what they did.
        var trail = await db.AuditLogs.SingleAsync(l => l.TenantId == customer);
        Assert.Equal(("Tenant.Suspended", AuditCategories.Security), (trail.Action, trail.Category));
        Assert.Contains("Unpaid invoice", trail.Details);
        Assert.StartsWith("Platform operator", trail.ActorName);
        Assert.Contains("\"to\":\"Suspended\"", trail.ChangesJson);
        Assert.Equal("Platform.TenantSuspended", Assert.Single(audit.Entries).Action);

        // Saying the same thing again changes nothing.
        await PlatformEndpoints.SuspendAsync(customer, new("Unpaid invoice"), db, op, statuses, audit);
        Assert.Single(audit.Entries);

        // Archived tenants cannot be suspended, only reactivated.
        Assert.Equal(TenantStatus.Archived, ValueOf<PlatformEndpoints.TenantSummary>(
            await PlatformEndpoints.ArchiveAsync(customer, new(), db, op, statuses, audit)).Status);
        Assert.Equal(409, StatusOf(await PlatformEndpoints.SuspendAsync(customer, new("Again"), db, op, statuses, audit)));

        var restored = ValueOf<PlatformEndpoints.TenantSummary>(await PlatformEndpoints.ReactivateAsync(customer, db, op, statuses, audit));
        Assert.Equal(TenantStatus.Active, restored.Status);
        Assert.Null(restored.StatusReason);
        Assert.Equal(TenantStatus.Active, (await statuses.GetAsync(customer))!.Status);
        Assert.Equal(3, audit.Entries.Count);
        Assert.Equal(3, await db.AuditLogs.CountAsync(l => l.TenantId == customer));
    }

    [Fact]
    public async Task Api_Tokens_Of_A_Closed_Tenant_Authenticate_Nobody()
    {
        var dbName = nameof(Api_Tokens_Of_A_Closed_Tenant_Authenticate_Nobody);
        var (db, op, _, customer) = await SeedAsync(dbName);
        var plaintext = ApiTokenHasher.GeneratePlaintext();
        await using (var customerDb = Context(dbName, UserOf(customer, "customer-oid")))
        {
            customerDb.ApiTokens.Add(new ApiToken
            {
                TenantId = customer,
                Name = "agent",
                TokenHash = ApiTokenHasher.Hash(plaintext),
                TokenPrefix = ApiTokenHasher.PublicPrefix(plaintext),
            });
            await customerDb.SaveChangesAsync();
        }

        Assert.NotNull(await ApiTokenAuthenticator.AuthenticateAsync(plaintext, db, CancellationToken.None));

        await PlatformEndpoints.SuspendAsync(customer, new("Unpaid invoice"), db, op, Statuses(db));
        Assert.Null(await ApiTokenAuthenticator.AuthenticateAsync(plaintext, db, CancellationToken.None));

        await PlatformEndpoints.ReactivateAsync(customer, db, op, Statuses(db));
        Assert.NotNull(await ApiTokenAuthenticator.AuthenticateAsync(plaintext, db, CancellationToken.None));
    }

    [Fact]
    public async Task A_Tenant_Not_Yet_Onboarded_Has_No_Status_To_Refuse()
    {
        var (db, _, _, _) = await SeedAsync(nameof(A_Tenant_Not_Yet_Onboarded_Has_No_Status_To_Refuse));
        Assert.Null(await Statuses(db).GetAsync(Guid.NewGuid()));
    }
}

/// <summary>
/// The lifecycle on the real request pipeline: a suspended tenant is refused on every API route
/// with its reason (and other tenants are untouched), and only configured operators reach the
/// platform console.
/// </summary>
public class TenantStatusPipelineTests : IClassFixture<TestHost>
{
    private const string OperatorObjectId = "platform-operator-oid";

    private readonly TestHost _host;

    public TenantStatusPipelineTests(TestHost host)
    {
        _host = host;
        // Operators come from host configuration; add this class's operator to the running options.
        var options = host.Services.GetRequiredService<IOptions<PlatformOptions>>().Value;
        if (!options.IsOperator(host.TenantAId, OperatorObjectId))
            options.Operators.Add(new PlatformOperator { TenantId = host.TenantAId, ObjectId = OperatorObjectId });
    }

    private HttpClient Operator() => _host.CreateAuthenticatedClient(OperatorObjectId, _host.TenantAId);

    private Guid SeedTenant()
    {
        var tenantId = Guid.NewGuid();
        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DocuEngAIneDbContext>();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Customer", Slug = $"cust-{tenantId:N}" });
        db.SaveChanges();
        return tenantId;
    }

    [Fact]
    public async Task A_Suspended_Tenant_Is_Refused_Everywhere_With_Its_Reason_Until_Reactivated()
    {
        var tenantId = SeedTenant();
        using var member = _host.CreateAuthenticatedClient($"member-{tenantId:N}", tenantId);
        using var platform = Operator();

        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/companies")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await platform.PostAsJsonAsync($"/api/platform/tenants/{tenantId}/suspend", new { reason = "Invoice 1042 is 60 days overdue." })).StatusCode);

        foreach (var path in new[] { "/api/me", "/api/companies", "/api/tenant/configuration" })
        {
            var refused = await member.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            var body = await refused.Content.ReadAsStringAsync();
            Assert.Contains(TenantStatusMiddleware.SuspendedError, body);
            Assert.Contains("Invoice 1042 is 60 days overdue.", body);
        }

        using var otherTenant = _host.CreateOwnerClient();
        Assert.Equal(HttpStatusCode.OK, (await otherTenant.GetAsync("/api/companies")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await platform.PostAsync($"/api/platform/tenants/{tenantId}/reactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/companies")).StatusCode);
    }

    [Fact]
    public async Task Only_Configured_Operators_Reach_The_Platform_Console()
    {
        using var owner = _host.CreateOwnerClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.GetAsync("/api/platform/tenants")).StatusCode);

        using var platform = Operator();
        var list = await platform.GetAsync("/api/platform/tenants");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains(_host.TenantBId.ToString(), await list.Content.ReadAsStringAsync());

        // An operator cannot close the tenant they are signed in to.
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await platform.PostAsJsonAsync($"/api/platform/tenants/{_host.TenantAId}/suspend", new { reason = "Testing" })).StatusCode);
    }
}
