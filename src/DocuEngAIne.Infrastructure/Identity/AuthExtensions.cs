using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace DocuEngAIne.Infrastructure.Identity;

public static class AuthExtensions
{
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
            options.AddPolicy("RequireAdmin", policy => policy.RequireRole("Admin", "Owner"));
        });

        services.AddHttpContextAccessor();

        return services;
    }
}
