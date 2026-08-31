using DocuEngAIne.Core.Enums;

namespace DocuEngAIne.Core.Interfaces;

public interface ILlmClient
{
    Task<LlmChatResult> ChatAsync(
        IReadOnlyList<LlmMessage> messages,
        LlmChatOptions? options = null,
        CancellationToken cancellationToken = default);
}

public sealed record LlmMessage(string Role, string Content);

public sealed class LlmChatOptions
{
    public string? Model { get; set; }
    public double? Temperature { get; set; }
    public int? MaxTokens { get; set; }
}

public sealed record LlmChatResult(string Content, string Model, LlmProvider Provider);
