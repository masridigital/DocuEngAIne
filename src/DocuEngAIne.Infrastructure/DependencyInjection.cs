using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using DocuEngAIne.Infrastructure.Identity;
using DocuEngAIne.Infrastructure.Integrations;
using DocuEngAIne.Infrastructure.Integrations.Migration;
using DocuEngAIne.Infrastructure.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DocuEngAIne.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddDocuEngAIneInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<IBackgroundTenantContext, BackgroundTenantContext>();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddScoped<IResourceAuthorizationService, ResourceAuthorizationService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddHttpClient(nameof(HttpMcpClient));
        services.AddScoped<IMcpClient, HttpMcpClient>();
        services.AddLlmClients(configuration);
        services.AddScoped<IIntegrationSyncService, IntegrationSyncService>();
        services.AddScoped<IItGlueMigrationService, ItGlueMigrationService>();
        services.AddSingleton<IntegrationSyncRunner>();
        services.AddHostedService<IntegrationSyncHostedService>();

        var connectionString = SqlConnectionDefaults.Resolve(
            configuration.GetConnectionString("DocuEngAIne"),
            configuration.GetValue<bool>("Azure:Sql:UseManagedIdentity"));

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
