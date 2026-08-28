using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace DocuEngAIne.Infrastructure.Identity;

public static class AuthExtensions
{
    /// <summary>
    /// Name of the policy that gates tenant-administration surfaces (MCP server registry,
    /// integration connections). Exposed as a constant so endpoint files bind to it by symbol
    /// rather than by a string literal that can silently drift out of sync with this file.
    /// </summary>
    public const string AdminPolicy = "RequireAdmin";

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
            options.AddPolicy("RequireAuthenticated", policy => policy.RequireAuthenticatedUser());
            options.AddPolicy(AdminPolicy, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new TenantAdminRequirement());
            });
        });

        // Scoped, not singleton: the handler reads the Users table through the request-scoped DbContext.
        services.AddScoped<IAuthorizationHandler, TenantAdminAuthorizationHandler>();

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
