using DocuEngAIne.Core.Enums;

namespace DocuEngAIne.Core.Interfaces;

public interface ILlmClient
{
    Task<LlmChatResult> ChatAsync(
        IReadOnlyList<LlmMessage> messages,
        LlmChatOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One chat message. <paramref name="CacheBreakpoint"/> marks the end of a prefix that is stable across
/// calls (for example a document body) so a provider that supports prompt caching may cache everything up
/// to and including this message. Providers that do not support it, or have it switched off, ignore it.
/// </summary>
public sealed record LlmMessage(string Role, string Content, bool CacheBreakpoint = false);

public sealed class LlmChatOptions
{
    public string? Model { get; set; }
    public double? Temperature { get; set; }
    public int? MaxTokens { get; set; }
}

/// <summary>
/// Token counts the provider reported for one call. Counts only: never prompt or completion text.
/// <see cref="InputTokens"/> excludes cache reads and writes, as the Anthropic API reports it.
/// </summary>
public sealed record LlmUsage(int InputTokens, int OutputTokens, int CacheCreationInputTokens, int CacheReadInputTokens)
{
    /// <summary>
    /// Space-separated <c>key=value</c> integers for an audit <c>details</c> string. Integers only, so it
    /// can never carry content.
    /// </summary>
    public string ToAuditDetails() => string.Create(
        System.Globalization.CultureInfo.InvariantCulture,
        $"in={InputTokens} out={OutputTokens} cacheRead={CacheReadInputTokens} cacheWrite={CacheCreationInputTokens}");
}

/// <param name="Usage">Null when the provider reported none or usage capture is off.</param>
public sealed record LlmChatResult(string Content, string Model, LlmProvider Provider, LlmUsage? Usage = null);
