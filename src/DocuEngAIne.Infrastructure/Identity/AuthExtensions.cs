using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace DocuEngAIne.Infrastructure.Identity;

public static class AuthExtensions
{
    /// <summary>
    /// Name of the policy that gates tenant-administration surfaces (MCP server registry,
    /// integration connections, outbound API tokens). Exposed as a constant so endpoint files bind
    /// to it by symbol rather than by a string literal that can silently drift out of sync with this file.
    /// </summary>
    public const string AdminPolicy = "RequireAdmin";

    /// <summary>
    /// The policy for the cross-tenant platform console: only the operators configured in
    /// <see cref="PlatformOptions"/>, never anything a tenant can grant itself.
    /// </summary>
    public const string PlatformOperatorPolicy = "RequirePlatformOperator";

    public static IServiceCollection AddDocuEngAIneAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var authority = configuration["EntraId:Authority"];
        var audience = configuration["EntraId:Audience"];

        if (string.IsNullOrWhiteSpace(authority) || string.IsNullOrWhiteSpace(audience))
            throw new InvalidOperationException("EntraId:Authority and EntraId:Audience must be configured.");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                options.Audience = audience;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromMinutes(5),
                };

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        if (context.Request.Headers.ContainsKey("X-MS-TOKEN"))
                        {
                            context.Token = context.Request.Headers["X-MS-TOKEN"];
                        }
                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorization(options =>
        {
            // RequireAuthorization() with no policy name resolves to DefaultPolicy, so every
            // authenticated route also refuses a deactivated user — not just the admin routes.
            options.DefaultPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new ActiveUserRequirement())
                .Build();
            options.AddPolicy("RequireAuthenticated", policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new ActiveUserRequirement());
            });
            options.AddPolicy(AdminPolicy, policy =>
            {
                policy.RequireAuthenticatedUser();
                // The active-user requirement is what stops an Entra Admin/Owner app-role claim from
                // outliving a deactivated row: the admin handler accepts that claim without the DB.
                policy.AddRequirements(new ActiveUserRequirement(), new TenantAdminRequirement());
            });
            options.AddPolicy(PlatformOperatorPolicy, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new ActiveUserRequirement(), new PlatformOperatorRequirement());
            });
        });

        services.Configure<PlatformOptions>(configuration.GetSection(PlatformOptions.SectionName));

        // Scoped, not singleton: the handlers read the Users table through the request-scoped DbContext.
        services.AddScoped<IAuthorizationHandler, TenantAdminAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, ActiveUserAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, PlatformOperatorAuthorizationHandler>();

        services.AddHttpContextAccessor();

        return services;
    }
}

/// <summary>
/// Requirement behind <see cref="AuthExtensions.AdminPolicy"/>.
/// </summary>
/// <remarks>
/// This exists instead of a plain <c>policy.RequireRole("Admin", "Owner")</c> because DocuEngAIne has
/// two independent sources of truth for "this person administers the tenant": Entra app-role claims
/// and the tenant-wide <see cref="Core.Entities.User.Role"/> column. Entra app roles are an optional
/// step in the setup guide, so a claims-only policy would hard-lock every user out of a tenant whose
/// app registration never defined them — including the person who onboarded it.
/// </remarks>
public sealed class TenantAdminRequirement : IAuthorizationRequirement
{
}

/// <summary>
/// Grants <see cref="TenantAdminRequirement"/> when the caller is an administrator by *either*
/// signal: an Entra app role of <c>Admin</c>/<c>Owner</c>, or a provisioned <c>User</c> row in the
/// current tenant whose <see cref="UserRole"/> is <c>Admin</c> or higher.
/// </summary>
/// <remarks>
/// The claim check runs first so tenants that do configure app roles never pay for a database
/// round-trip on every admin request. Failure is silent (no <c>context.Fail()</c>): another handler
/// for the same requirement should still be able to succeed, and <c>Fail()</c> would veto it.
/// </remarks>
public sealed class TenantAdminAuthorizationHandler : AuthorizationHandler<TenantAdminRequirement>
{
    private readonly DocuEngAIneDbContext _db;
    private readonly ICurrentUser _currentUser;

    public TenantAdminAuthorizationHandler(DocuEngAIneDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, TenantAdminRequirement requirement)
    {
        var principal = context.User;
        if (principal is null || principal.Identity?.IsAuthenticated != true)
            return;

        if (principal.HasRole("Admin") || principal.HasRole("Owner"))
        {
            context.Succeed(requirement);
            return;
        }

        if (_currentUser.TenantId is null || string.IsNullOrEmpty(_currentUser.ObjectId))
            return;

        var tenantId = _currentUser.TenantId.Value;
        var objectId = _currentUser.ObjectId;

        // Fall back to the tenant-wide role we store ourselves. Scoped by TenantId as well as
        // EntraObjectId so a row from another tenant can never satisfy this requirement.
        var user = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.TenantId == tenantId && u.EntraObjectId == objectId);

        if (user is not null && user.IsActive && user.Role >= UserRole.Admin)
            context.Succeed(requirement);
    }
}

/// <summary>
/// Refuses a caller whose <see cref="Core.Entities.User"/> row in the current tenant is deactivated.
/// </summary>
/// <remarks>
/// Before this requirement, <see cref="Core.Entities.User.IsActive"/> only gated the admin policy's
/// row fallback: every other route needs just a valid Entra token, write checks read Entra claims
/// or the stored role without looking at the flag, and an Entra Admin/Owner app role passed the
/// admin policy with no database lookup at all. Deactivating a user therefore removed almost
/// nothing. No row at all still succeeds — first sign-in provisions it through <c>GET /api/me</c>,
/// and onboarding runs before any row exists.
/// </remarks>
public sealed class ActiveUserRequirement : IAuthorizationRequirement
{
}

public sealed class ActiveUserAuthorizationHandler : AuthorizationHandler<ActiveUserRequirement>
{
    private readonly DocuEngAIneDbContext _db;
    private readonly ICurrentUser _currentUser;

    public ActiveUserAuthorizationHandler(DocuEngAIneDbContext db, ICurrentUser currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ActiveUserRequirement requirement)
    {
        if (context.User?.Identity?.IsAuthenticated != true)
            return;

        if (_currentUser.TenantId is not Guid tenantId || string.IsNullOrEmpty(_currentUser.ObjectId))
        {
            context.Succeed(requirement);
            return;
        }

        var objectId = _currentUser.ObjectId;
        var deactivated = await _db.Users
            .AsNoTracking()
            .AnyAsync(u => u.TenantId == tenantId && u.EntraObjectId == objectId && !u.IsActive);

        if (!deactivated)
            context.Succeed(requirement);
    }
}

/// <summary>Requirement behind <see cref="AuthExtensions.PlatformOperatorPolicy"/>.</summary>
public sealed class PlatformOperatorRequirement : IAuthorizationRequirement
{
}

/// <summary>Grants <see cref="PlatformOperatorRequirement"/> to the callers listed in <see cref="PlatformOptions"/>, and to no one else.</summary>
public sealed class PlatformOperatorAuthorizationHandler : AuthorizationHandler<PlatformOperatorRequirement>
{
    private readonly ICurrentUser _currentUser;
    private readonly IOptions<PlatformOptions> _options;

    public PlatformOperatorAuthorizationHandler(ICurrentUser currentUser, IOptions<PlatformOptions> options)
    {
        _currentUser = currentUser;
        _options = options;
    }

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PlatformOperatorRequirement requirement)
    {
        if (context.User?.Identity?.IsAuthenticated == true
            && _options.Value.IsOperator(_currentUser.TenantId, _currentUser.ObjectId))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
