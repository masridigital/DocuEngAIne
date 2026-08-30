using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocuEngAIne.Infrastructure.Llm;

/// <summary>
/// Together AI OpenAI-compatible chat. The API key is resolved at call time so a missing
/// <c>TogetherApiKey</c> does not fail host startup.
/// </summary>
public sealed class TogetherLlmClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;
    private readonly LlmOptions _options;
    private readonly ILogger<TogetherLlmClient> _logger;

    public TogetherLlmClient(
        HttpClient http,
        IConfiguration configuration,
        IOptions<LlmOptions> options,
        ILogger<TogetherLlmClient> logger)
    {
        _http = http;
        _configuration = configuration;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<LlmChatResult> ChatAsync(
        IReadOnlyList<LlmMessage> messages,
        LlmChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var apiKey = LlmSecretResolver.Require(_configuration, LlmSecretNames.TogetherApiKey);
        var model = LlmDefaults.ResolveModel(LlmProvider.Together, _options.Model, options?.Model);
        var payload = new OpenAiChatRequest
        {
            Model = model,
            Messages = messages.Select(OpenAiChatMessage.From).ToList(),
            Temperature = options?.Temperature,
            MaxTokens = options?.MaxTokens,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, LlmDefaults.TogetherEndpoint)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        _logger.LogInformation(
            "Together chat POST {Url} {Headers}",
            LlmDefaults.TogetherEndpoint,
            LlmLogRedaction.Describe(request.Headers));

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Together chat failed ({(int)response.StatusCode}): {body}");

        var parsed = JsonSerializer.Deserialize<OpenAiChatCompletionResponse>(body)
            ?? throw new InvalidOperationException("Together returned an empty chat completion.");
        var content = parsed.Choices?.FirstOrDefault()?.Message?.Content
            ?? throw new InvalidOperationException("Together returned no assistant content.");

        return new LlmChatResult(content, parsed.Model ?? model, LlmProvider.Together);
    }
}
