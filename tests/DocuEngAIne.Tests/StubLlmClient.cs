using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Tests;

/// <summary>
/// In-process stand-in for <see cref="ILlmClient"/> so HTTP pipeline tests never open a socket.
/// </summary>
public sealed class StubLlmClient : ILlmClient
{
    public LlmChatResult Result { get; set; } = new("stub-reply", "llama3.1", LlmProvider.Ollama);

    public IReadOnlyList<LlmMessage>? LastMessages { get; private set; }

    public LlmChatOptions? LastOptions { get; private set; }

    public int CallCount { get; private set; }

    public Exception? ThrowOnChat { get; set; }

    public void Reset()
    {
        CallCount = 0;
        LastMessages = null;
        LastOptions = null;
        ThrowOnChat = null;
        Result = new("stub-reply", "llama3.1", LlmProvider.Ollama);
    }

    public Task<LlmChatResult> ChatAsync(
        IReadOnlyList<LlmMessage> messages,
        LlmChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastMessages = messages;
        LastOptions = options;
        if (ThrowOnChat is not null)
            throw ThrowOnChat;
        return Task.FromResult(Result);
    }
}
