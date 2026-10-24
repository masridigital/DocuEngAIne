using DocuEngAIne.Core.Enums;

namespace DocuEngAIne.Infrastructure.Llm;

public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public LlmProvider Provider { get; set; } = LlmProvider.Ollama;

    /// <summary>Optional override. When empty, the default model for <see cref="Provider"/> is used.</summary>
    public string? Model { get; set; }

    public OllamaLlmOptions Ollama { get; set; } = new();

    public AnthropicLlmOptions Anthropic { get; set; } = new();
}

public sealed class AnthropicLlmOptions
{
    public AnthropicPromptCachingOptions PromptCaching { get; set; } = new();
}

/// <summary>
/// Anthropic prompt caching (<c>cache_control</c> on the document turn) plus capture of the reported
/// token counts. <b>Off by default</b>: production keeps sending exactly the request it always did.
/// Turn it on per environment with <c>Llm__Anthropic__PromptCaching__Enabled=true</c>.
/// </summary>
public sealed class AnthropicPromptCachingOptions
{
    public bool Enabled { get; set; }
}

public sealed class OllamaLlmOptions
{
    public string BaseUrl { get; set; } = LlmDefaults.OllamaBaseUrl;
}

public static class LlmDefaults
{
    public const string OllamaModel = "llama3.1";
    public const string TogetherModel = "meta-llama/Meta-Llama-3.1-8B-Instruct-Turbo";
    // Current-generation ids are undated; the dated Sonnet-4 snapshot this shipped with is retired
    // and every default-config Anthropic chat failed with model-not-found.
    public const string AnthropicModel = "claude-opus-5";
    public const string OllamaBaseUrl = "http://127.0.0.1:11434";
    public const string TogetherEndpoint = "https://api.together.xyz/v1/chat/completions";
    public const string AnthropicEndpoint = "https://api.anthropic.com/v1/messages";
    public const string AnthropicVersion = "2023-06-01";
    public const int DefaultMaxTokens = 1024;

    public static string ModelFor(LlmProvider provider) => provider switch
    {
        LlmProvider.Together => TogetherModel,
        LlmProvider.Anthropic => AnthropicModel,
        _ => OllamaModel,
    };

    public static string ResolveModel(LlmProvider provider, string? configured, string? request)
    {
        if (!string.IsNullOrWhiteSpace(request))
            return request.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();
        return ModelFor(provider);
    }
}

public static class LlmSecretNames
{
    public const string TogetherApiKey = "TogetherApiKey";
    public const string AnthropicApiKey = "AnthropicApiKey";
}
