using DocuEngAIne.Core.Enums;

namespace DocuEngAIne.Infrastructure.Llm;

public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public LlmProvider Provider { get; set; } = LlmProvider.Ollama;

    /// <summary>Optional override. When empty, the default model for <see cref="Provider"/> is used.</summary>
    public string? Model { get; set; }

    public OllamaLlmOptions Ollama { get; set; } = new();
}

public sealed class OllamaLlmOptions
{
    public string BaseUrl { get; set; } = LlmDefaults.OllamaBaseUrl;
}

public static class LlmDefaults
{
    public const string OllamaModel = "llama3.1";
    public const string TogetherModel = "meta-llama/Meta-Llama-3.1-8B-Instruct-Turbo";
    public const string AnthropicModel = "claude-sonnet-4-20250514";
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
