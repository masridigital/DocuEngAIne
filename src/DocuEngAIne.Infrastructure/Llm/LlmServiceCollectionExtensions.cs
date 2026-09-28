using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocuEngAIne.Infrastructure.Llm;

public static class LlmServiceCollectionExtensions
{
    public static IServiceCollection AddLlmClients(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LlmOptions>(configuration.GetSection(LlmOptions.SectionName));
        services.AddTransient<LlmHttpLoggingHandler>();
        services.AddHttpClient<OllamaLlmClient>()
            .AddHttpMessageHandler<LlmHttpLoggingHandler>();
        services.AddHttpClient<TogetherLlmClient>()
            .AddHttpMessageHandler<LlmHttpLoggingHandler>();
        services.AddHttpClient<AnthropicLlmClient>()
            .AddHttpMessageHandler<LlmHttpLoggingHandler>();

        // Factory selects the configured provider. Together/Anthropic clients are always
        // registered so a missing API key throws from ChatAsync, not host startup.
        services.AddScoped<ILlmClient>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
            return options.Provider switch
            {
                LlmProvider.Together => sp.GetRequiredService<TogetherLlmClient>(),
                LlmProvider.Anthropic => sp.GetRequiredService<AnthropicLlmClient>(),
                _ => sp.GetRequiredService<OllamaLlmClient>(),
            };
        });

        return services;
    }
}
