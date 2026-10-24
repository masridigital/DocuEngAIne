using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocuEngAIne.Infrastructure.Llm;

/// <summary>
/// Anthropic Messages API. System prompts are sent as the top-level <c>system</c> field;
/// user/assistant turns go in <c>messages</c>. The API key is resolved at call time so a
/// missing <c>AnthropicApiKey</c> does not fail host startup.
/// </summary>
public sealed class AnthropicLlmClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly IConfiguration _configuration;
    private readonly LlmOptions _options;
    private readonly ILogger<AnthropicLlmClient> _logger;

    public AnthropicLlmClient(
        HttpClient http,
        IConfiguration configuration,
        IOptions<LlmOptions> options,
        ILogger<AnthropicLlmClient> logger)
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
        var apiKey = LlmSecretResolver.Require(_configuration, LlmSecretNames.AnthropicApiKey);
        var model = LlmDefaults.ResolveModel(LlmProvider.Anthropic, _options.Model, options?.Model);
        var cache = _options.Anthropic.PromptCaching.Enabled;
        var (system, turns) = SplitSystem(messages, cache);
        if (turns.Count == 0)
            throw new InvalidOperationException("Anthropic chat requires at least one user or assistant message.");

        var payload = new AnthropicMessageRequest
        {
            Model = model,
            MaxTokens = options?.MaxTokens ?? LlmDefaults.DefaultMaxTokens,
            Temperature = options?.Temperature,
            System = system,
            Messages = turns,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, LlmDefaults.AnthropicEndpoint)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        request.Headers.TryAddWithoutValidation("anthropic-version", LlmDefaults.AnthropicVersion);

        _logger.LogInformation(
            "Anthropic chat POST {Url} {Headers}",
            LlmDefaults.AnthropicEndpoint,
            LlmLogRedaction.Describe(request.Headers));

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Anthropic chat failed ({(int)response.StatusCode}): {body}");

        var parsed = JsonSerializer.Deserialize<AnthropicMessageResponse>(body)
            ?? throw new InvalidOperationException("Anthropic returned an empty message.");
        var content = ReadText(parsed)
            ?? throw new InvalidOperationException("Anthropic returned no text content.");

        // Usage is captured only with caching switched on, so the off state changes nothing in prod.
        var usage = cache ? ReadUsage(parsed.Usage) : null;
        if (usage is not null)
        {
            _logger.LogInformation("Anthropic chat usage {Usage}", usage.ToAuditDetails());
        }

        return new LlmChatResult(content, parsed.Model ?? model, LlmProvider.Anthropic, usage);
    }

    private static LlmUsage? ReadUsage(AnthropicUsage? usage) => usage is null
        ? null
        : new LlmUsage(
            NonNegative(usage.InputTokens),
            NonNegative(usage.OutputTokens),
            NonNegative(usage.CacheCreationInputTokens),
            NonNegative(usage.CacheReadInputTokens));

    private static int NonNegative(int? value) => value is > 0 ? value.Value : 0;

    internal static (string? System, List<AnthropicTurn> Turns) SplitSystem(
        IReadOnlyList<LlmMessage> messages,
        bool cache = false)
    {
        var systemParts = new List<string>();
        var turns = new List<AnthropicTurn>();

        foreach (var message in messages)
        {
            if (string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(message.Content))
                    systemParts.Add(message.Content);
                continue;
            }

            // Plain string unless this turn ends a cacheable prefix. The system field stays a string, and
            // it sits before the marked turn, so the cached prefix covers system + everything up to here.
            turns.Add(new AnthropicTurn
            {
                Role = message.Role,
                Content = cache && message.CacheBreakpoint && !string.IsNullOrEmpty(message.Content)
                    ? new List<AnthropicTextBlock>
                    {
                        new() { Text = message.Content, CacheControl = new AnthropicCacheControl() },
                    }
                    : message.Content,
            });
        }

        var system = systemParts.Count == 0 ? null : string.Join("\n\n", systemParts);
        return (system, turns);
    }

    private static string? ReadText(AnthropicMessageResponse parsed)
    {
        if (parsed.Content is null)
            return null;

        var parts = parsed.Content
            .Where(block => string.Equals(block.Type, "text", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(block.Text))
            .Select(block => block.Text!);

        var text = string.Concat(parts);
        return string.IsNullOrEmpty(text) ? null : text;
    }
}

internal sealed class AnthropicMessageRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("max_tokens")]
    public required int MaxTokens { get; init; }

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; init; }

    [JsonPropertyName("system")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? System { get; init; }

    [JsonPropertyName("messages")]
    public required IReadOnlyList<AnthropicTurn> Messages { get; init; }
}

internal sealed class AnthropicTurn
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    // string, or List<AnthropicTextBlock> for a turn that carries a cache breakpoint.
    [JsonPropertyName("content")]
    public required object Content { get; init; }
}

internal sealed class AnthropicTextBlock
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "text";

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("cache_control")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnthropicCacheControl? CacheControl { get; init; }
}

/// <summary>Default 5-minute ephemeral cache. No <c>ttl</c> is sent, and no beta header is needed.</summary>
internal sealed class AnthropicCacheControl
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "ephemeral";
}

internal sealed class AnthropicMessageResponse
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("usage")]
    public AnthropicUsage? Usage { get; set; }

    [JsonPropertyName("content")]
    public List<AnthropicContentBlock>? Content { get; set; }
}

internal sealed class AnthropicUsage
{
    [JsonPropertyName("input_tokens")]
    public int? InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public int? OutputTokens { get; set; }

    [JsonPropertyName("cache_creation_input_tokens")]
    public int? CacheCreationInputTokens { get; set; }

    [JsonPropertyName("cache_read_input_tokens")]
    public int? CacheReadInputTokens { get; set; }
}

internal sealed class AnthropicContentBlock
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}
