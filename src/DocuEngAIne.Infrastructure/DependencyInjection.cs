using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using DocuEngAIne.Infrastructure.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocuEngAIne.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddDocuEngAIneInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddScoped<IResourceAuthorizationService, ResourceAuthorizationService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddHttpClient(nameof(HttpMcpClient));
        services.AddScoped<IMcpClient, HttpMcpClient>();
        services.AddScoped<IIntegrationSyncService, IntegrationSyncService>();

        var connectionString = configuration.GetConnectionString("DocuEngAIne");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings:DocuEngAIne is missing.");

        services.AddDbContext<DocuEngAIneDbContext>(options =>
        {
            options.UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsAssembly("DocuEngAIne.Api");
                sql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null);
            });

            if (configuration.GetValue<bool>("Logging:EnableSensitiveData"))
                options.EnableSensitiveDataLogging();
        });

        services.AddDocuEngAIneAuthentication(configuration);

        return services;
    }
}
