using System.Text.Json.Serialization;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Llm;

internal sealed class OpenAiChatRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("messages")]
    public required IReadOnlyList<OpenAiChatMessage> Messages { get; init; }

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; init; }

    [JsonPropertyName("max_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxTokens { get; init; }
}

internal sealed class OpenAiChatMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    public required string Content { get; init; }

    public static OpenAiChatMessage From(LlmMessage message) => new()
    {
        Role = message.Role,
        Content = message.Content,
    };
}

internal sealed class OpenAiChatCompletionResponse
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("choices")]
    public List<OpenAiChatChoice>? Choices { get; set; }
}

internal sealed class OpenAiChatChoice
{
    [JsonPropertyName("message")]
    public OpenAiChatMessage? Message { get; set; }
}
