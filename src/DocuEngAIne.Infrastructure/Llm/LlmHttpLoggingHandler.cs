using Microsoft.Extensions.Logging;

namespace DocuEngAIne.Infrastructure.Llm;

/// <summary>
/// Logs outbound LLM HTTP calls without writing API keys or bearer tokens.
/// </summary>
public sealed class LlmHttpLoggingHandler : DelegatingHandler
{
    private readonly ILogger<LlmHttpLoggingHandler> _logger;

    public LlmHttpLoggingHandler(ILogger<LlmHttpLoggingHandler> logger)
    {
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "LLM HTTP {Method} {Url} {Headers}",
                request.Method,
                request.RequestUri,
                LlmLogRedaction.Describe(request.Headers));
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
