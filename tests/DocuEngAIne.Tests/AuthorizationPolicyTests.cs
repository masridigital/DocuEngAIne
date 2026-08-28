using System.Security.Claims;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocuEngAIne.Tests;

/// <summary>
/// Covers the admin gate behind <see cref="AuthExtensions.AdminPolicy"/>. The policy is deliberately
/// satisfiable by two independent signals — an Entra app role claim, or the tenant-wide
/// <c>User.Role</c> column — because Entra app roles are an optional setup step and a claims-only
/// policy would lock an entire tenant out of its own integration settings.
/// </summary>
public class AuthorizationPolicyTests
{
    private static (DocuEngAIneDbContext Db, FakeCurrentUser CurrentUser) CreateContext()
    {
        var currentUser = new FakeCurrentUser
        {
            TenantId = Guid.NewGuid(),
            ObjectId = Guid.NewGuid().ToString(),
        };

        var options = new DbContextOptionsBuilder<DocuEngAIneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var db = new DocuEngAIneDbContext(options, currentUser);
        db.Tenants.Add(new Tenant { Id = currentUser.TenantId!.Value, Name = "Test", Slug = "test" });
        db.SaveChanges();
        db.ChangeTracker.Clear();

        return (db, currentUser);
    }

    private static void SeedUser(
        DocuEngAIneDbContext db,
        Guid tenantId,
        string entraObjectId,
        UserRole role,
        bool isActive = true)
    {
        db.Users.Add(new User
        {
            TenantId = tenantId,
            EntraObjectId = entraObjectId,
            Email = "user@example.com",
            Role = role,
            IsActive = isActive,
        });
        db.SaveChanges();
        db.ChangeTracker.Clear();
    }

    /// <summary>Builds a signed-in principal carrying zero or more Entra app roles.</summary>
    private static ClaimsPrincipal SignedIn(params string[] appRoles)
    {
        var claims = appRoles.Select(role => new Claim("roles", role)).ToList();
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestJwt"));
    }

    private static async Task<bool> EvaluateAsync(
        DocuEngAIneDbContext db,
        FakeCurrentUser currentUser,
        ClaimsPrincipal principal)
    {
        var handler = new TenantAdminAuthorizationHandler(db, currentUser);
        var requirement = new TenantAdminRequirement();
        var context = new AuthorizationHandlerContext(
            new IAuthorizationRequirement[] { requirement },
            principal,
            resource: null);

        await handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    [Theory]
    [InlineData("Admin")]
    [InlineData("Owner")]
    public async Task Entra_App_Role_Satisfies_The_Policy(string appRole)
    {
        // No User row at all: the claim alone must be enough, otherwise a tenant that relies on
        // Entra app roles would need a database round-trip it never provisioned.
        var (db, currentUser) = CreateContext();

        Assert.True(await EvaluateAsync(db, currentUser, SignedIn(appRole)));
    }

    [Theory]
    [InlineData(UserRole.Admin)]
    [InlineData(UserRole.Owner)]
    public async Task Database_Role_Satisfies_The_Policy_Without_Any_App_Roles(UserRole role)
    {
        // This is the case the claims-only policy got wrong: an app registration with no app roles
        // defined emits no "roles" claim, so the database role is the only signal available.
        var (db, currentUser) = CreateContext();
        SeedUser(db, currentUser.TenantId!.Value, currentUser.ObjectId!, role);

        Assert.True(await EvaluateAsync(db, currentUser, SignedIn()));
    }

    [Theory]
    [InlineData(UserRole.None)]
    [InlineData(UserRole.Reader)]
    [InlineData(UserRole.Contributor)]
    public async Task Non_Admin_Database_Role_Is_Denied(UserRole role)
    {
        var (db, currentUser) = CreateContext();
        SeedUser(db, currentUser.TenantId!.Value, currentUser.ObjectId!, role);

        Assert.False(await EvaluateAsync(db, currentUser, SignedIn("Contributor")));
    }

    [Fact]
    public async Task Unprovisioned_User_Is_Denied()
    {
        var (db, currentUser) = CreateContext();

        Assert.False(await EvaluateAsync(db, currentUser, SignedIn()));
    }

    [Fact]
    public async Task Deactivated_Admin_Is_Denied()
    {
        var (db, currentUser) = CreateContext();
        SeedUser(db, currentUser.TenantId!.Value, currentUser.ObjectId!, UserRole.Owner, isActive: false);

        Assert.False(await EvaluateAsync(db, currentUser, SignedIn()));
    }

    [Fact]
    public async Task Admin_Row_From_Another_Tenant_Is_Denied()
    {
        // Guards the fallback lookup against matching on EntraObjectId alone: a guest who is Owner
        // in tenant A must not administer tenant B.
        var (db, currentUser) = CreateContext();
        SeedUser(db, Guid.NewGuid(), currentUser.ObjectId!, UserRole.Owner);

        Assert.False(await EvaluateAsync(db, currentUser, SignedIn()));
    }

    [Fact]
    public async Task Unauthenticated_Principal_Is_Denied()
    {
        // A ClaimsIdentity with no authentication type is not authenticated, even if it carries
        // a "roles" claim — the claim check must never run ahead of that test.
        var (db, currentUser) = CreateContext();
        SeedUser(db, currentUser.TenantId!.Value, currentUser.ObjectId!, UserRole.Owner);
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("roles", "Owner") }));

        Assert.False(await EvaluateAsync(db, currentUser, anonymous));
    }

    [Fact]
    public async Task Admin_Policy_Is_Registered_With_The_Hybrid_Requirement_And_Handler()
    {
        // Route-level enforcement cannot be exercised here (the test project has no
        // Microsoft.AspNetCore.Mvc.Testing host), so at least assert the policy the endpoint groups
        // name actually exists and is backed by the hybrid requirement plus a registered handler.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EntraId:Authority"] = "https://login.microsoftonline.com/common/v2.0",
                ["EntraId:Audience"] = "api://docuengaine-tests",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddDocuEngAIneAuthentication(configuration);

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IAuthorizationHandler)
            && descriptor.ImplementationType == typeof(TenantAdminAuthorizationHandler)
            && descriptor.Lifetime == ServiceLifetime.Scoped);

        await using var provider = services.BuildServiceProvider();
        var policyProvider = provider.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = await policyProvider.GetPolicyAsync(AuthExtensions.AdminPolicy);

        Assert.NotNull(policy);
        Assert.Contains(policy!.Requirements, requirement => requirement is TenantAdminRequirement);
    }
}
