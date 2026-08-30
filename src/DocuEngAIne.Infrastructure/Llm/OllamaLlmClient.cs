using System.Net.Http.Json;
using System.Text.Json;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocuEngAIne.Infrastructure.Llm;

/// <summary>
/// OpenAI-compatible chat against a self-hosted Ollama instance. No API key.
/// </summary>
public sealed class OllamaLlmClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly LlmOptions _options;
    private readonly ILogger<OllamaLlmClient> _logger;

    public OllamaLlmClient(HttpClient http, IOptions<LlmOptions> options, ILogger<OllamaLlmClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<LlmChatResult> ChatAsync(
        IReadOnlyList<LlmMessage> messages,
        LlmChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var model = LlmDefaults.ResolveModel(LlmProvider.Ollama, _options.Model, options?.Model);
        var url = ChatCompletionsUrl(_options.Ollama.BaseUrl);
        var payload = new OpenAiChatRequest
        {
            Model = model,
            Messages = messages.Select(OpenAiChatMessage.From).ToList(),
            Temperature = options?.Temperature,
            MaxTokens = options?.MaxTokens,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload),
        };

        _logger.LogInformation("Ollama chat POST {Url} {Headers}", url, LlmLogRedaction.Describe(request.Headers));

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Ollama chat failed ({(int)response.StatusCode}): {body}");

        var parsed = JsonSerializer.Deserialize<OpenAiChatCompletionResponse>(body)
            ?? throw new InvalidOperationException("Ollama returned an empty chat completion.");
        var content = parsed.Choices?.FirstOrDefault()?.Message?.Content
            ?? throw new InvalidOperationException("Ollama returned no assistant content.");

        return new LlmChatResult(content, parsed.Model ?? model, LlmProvider.Ollama);
    }

    internal static Uri ChatCompletionsUrl(string? baseUrl)
    {
        var root = string.IsNullOrWhiteSpace(baseUrl) ? LlmDefaults.OllamaBaseUrl : baseUrl.Trim().TrimEnd('/');
        return new Uri($"{root}/v1/chat/completions");
    }
}
