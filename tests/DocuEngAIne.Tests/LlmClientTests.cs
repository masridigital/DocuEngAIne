using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocuEngAIne.Tests;

public class LlmClientTests
{
    private const string OpenAiReply = """{"id":"cmpl","model":"reply-model","choices":[{"message":{"role":"assistant","content":"hello from provider"}}]}""";
    private const string AnthropicReply = """{"id":"msg","model":"claude-reply","content":[{"type":"text","text":"hello from anthropic"}]}""";
    private const string TogetherSecret = "together-secret-do-not-log";
    private const string AnthropicSecret = "anthropic-secret-do-not-log";

    [Fact]
    public async Task Ollama_Posts_OpenAi_Compat_Url_And_Json()
    {
        var handler = new RecordingHandler().EnqueueJson(OpenAiReply);
        var client = new OllamaLlmClient(
            new HttpClient(handler, disposeHandler: false),
            Options.Create(new LlmOptions
            {
                Provider = LlmProvider.Ollama,
                Model = "llama3.1",
                Ollama = new OllamaLlmOptions { BaseUrl = "http://127.0.0.1:11434" },
            }),
            NullLogger<OllamaLlmClient>.Instance);

        var result = await client.ChatAsync(
            [new LlmMessage("user", "ping")],
            new LlmChatOptions { Temperature = 0.2, MaxTokens = 64 });

        Assert.Equal("hello from provider", result.Content);
        Assert.Equal("reply-model", result.Model);
        Assert.Equal(LlmProvider.Ollama, result.Provider);

        var recorded = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, recorded.Method);
        Assert.Equal("http://127.0.0.1:11434/v1/chat/completions", recorded.Uri?.ToString());
        Assert.Null(recorded.Headers.Authorization);

        using var doc = JsonDocument.Parse(recorded.Body);
        var root = doc.RootElement;
        Assert.Equal("llama3.1", root.GetProperty("model").GetString());
        Assert.Equal(0.2, root.GetProperty("temperature").GetDouble());
        Assert.Equal(64, root.GetProperty("max_tokens").GetInt32());
        var message = Assert.Single(root.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("ping", message.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Together_Sends_Bearer_And_Redacts_It_In_Logs()
    {
        var handler = new RecordingHandler().EnqueueJson(OpenAiReply);
        var logger = new CapturingLogger<TogetherLlmClient>();
        var client = new TogetherLlmClient(
            new HttpClient(handler, disposeHandler: false),
            Config(("TogetherApiKey", TogetherSecret)),
            Options.Create(new LlmOptions { Provider = LlmProvider.Together }),
            logger);

        var result = await client.ChatAsync([new LlmMessage("user", "hi")]);

        Assert.Equal(LlmProvider.Together, result.Provider);
        Assert.Equal("hello from provider", result.Content);

        var recorded = Assert.Single(handler.Requests);
        Assert.Equal(LlmDefaults.TogetherEndpoint, recorded.Uri?.ToString());
        Assert.Equal("Bearer", recorded.Headers.Authorization?.Scheme);
        Assert.Equal(TogetherSecret, recorded.Headers.Authorization?.Parameter);

        var logs = string.Join('\n', logger.Messages);
        Assert.Contains("Bearer [redacted]", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(TogetherSecret, logs);
        Assert.Contains(LlmDefaults.TogetherEndpoint, logs);
    }

    [Fact]
    public async Task Together_Logging_Handler_Redacts_Authorization()
    {
        var inner = new RecordingHandler().EnqueueJson(OpenAiReply);
        var logger = new CapturingLogger<LlmHttpLoggingHandler>();
        var pipeline = new LlmHttpLoggingHandler(logger) { InnerHandler = inner };
        var client = new TogetherLlmClient(
            new HttpClient(pipeline, disposeHandler: false),
            Config(("TogetherApiKey", TogetherSecret)),
            Options.Create(new LlmOptions()),
            NullLogger<TogetherLlmClient>.Instance);

        await client.ChatAsync([new LlmMessage("user", "hi")]);

        var logs = string.Join('\n', logger.Messages);
        Assert.Contains("Bearer [redacted]", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(TogetherSecret, logs);
    }

    [Fact]
    public async Task Anthropic_Splits_System_From_User_Messages()
    {
        var handler = new RecordingHandler().EnqueueJson(AnthropicReply);
        var client = new AnthropicLlmClient(
            new HttpClient(handler, disposeHandler: false),
            Config(("AnthropicApiKey", AnthropicSecret)),
            Options.Create(new LlmOptions { Provider = LlmProvider.Anthropic }),
            NullLogger<AnthropicLlmClient>.Instance);

        var result = await client.ChatAsync(
        [
            new LlmMessage("system", "You are a doc assistant."),
            new LlmMessage("user", "Summarize the runbook."),
        ]);

        Assert.Equal("hello from anthropic", result.Content);
        Assert.Equal(LlmProvider.Anthropic, result.Provider);

        var recorded = Assert.Single(handler.Requests);
        Assert.Equal(LlmDefaults.AnthropicEndpoint, recorded.Uri?.ToString());
        Assert.Equal(AnthropicSecret, recorded.Headers.GetValues("x-api-key").Single());
        Assert.Equal(LlmDefaults.AnthropicVersion, recorded.Headers.GetValues("anthropic-version").Single());

        using var doc = JsonDocument.Parse(recorded.Body);
        var root = doc.RootElement;
        Assert.Equal("You are a doc assistant.", root.GetProperty("system").GetString());
        var turns = root.GetProperty("messages").EnumerateArray().ToList();
        Assert.Single(turns);
        Assert.Equal("user", turns[0].GetProperty("role").GetString());
        Assert.Equal("Summarize the runbook.", turns[0].GetProperty("content").GetString());
        Assert.False(turns[0].TryGetProperty("system", out _));
    }

    [Fact]
    public async Task Together_Missing_Key_Throws_Without_Sending()
    {
        var handler = new RecordingHandler();
        var client = new TogetherLlmClient(
            new HttpClient(handler, disposeHandler: false),
            Config(),
            Options.Create(new LlmOptions { Provider = LlmProvider.Together }),
            NullLogger<TogetherLlmClient>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ChatAsync([new LlmMessage("user", "hi")]));

        Assert.Contains("TogetherApiKey", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Anthropic_Missing_Key_Throws_Without_Sending()
    {
        var handler = new RecordingHandler();
        var client = new AnthropicLlmClient(
            new HttpClient(handler, disposeHandler: false),
            Config(),
            Options.Create(new LlmOptions { Provider = LlmProvider.Anthropic }),
            NullLogger<AnthropicLlmClient>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ChatAsync([new LlmMessage("user", "hi")]));

        Assert.Contains("AnthropicApiKey", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Factory_Registers_Together_Without_Key_And_Chat_Throws()
    {
        var services = new ServiceCollection();
        var configuration = Config(("Llm:Provider", "Together"));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddLlmClients(configuration);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<ILlmClient>();
        Assert.IsType<TogetherLlmClient>(client);

        var ex = Assert.Throws<InvalidOperationException>(
            () => client.ChatAsync([new LlmMessage("user", "hi")]).GetAwaiter().GetResult());
        Assert.Contains("TogetherApiKey", ex.Message);
    }

    [Fact]
    public void Factory_Registers_Anthropic_Without_Key_And_Chat_Throws()
    {
        var services = new ServiceCollection();
        var configuration = Config(("Llm:Provider", "Anthropic"));
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddLlmClients(configuration);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<ILlmClient>();
        Assert.IsType<AnthropicLlmClient>(client);

        var ex = Assert.Throws<InvalidOperationException>(
            () => client.ChatAsync([new LlmMessage("user", "hi")]).GetAwaiter().GetResult());
        Assert.Contains("AnthropicApiKey", ex.Message);
    }

    [Fact]
    public void Factory_Selects_Ollama_By_Default()
    {
        var services = new ServiceCollection();
        var configuration = Config();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddLlmClients(configuration);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.IsType<OllamaLlmClient>(scope.ServiceProvider.GetRequiredService<ILlmClient>());
    }

    [Fact]
    public void Log_Redaction_Formats_Bearer_Without_Token()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, LlmDefaults.TogetherEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TogetherSecret);

        var described = LlmLogRedaction.Describe(request.Headers);

        Assert.Contains("Bearer [redacted]", described, StringComparison.Ordinal);
        Assert.DoesNotContain(TogetherSecret, described);
    }

    private static IConfiguration Config(params (string Key, string? Value)[] pairs)
    {
        var data = pairs.ToDictionary(p => p.Key, p => p.Value);
        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri? Uri, HttpRequestHeaders Headers, string Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();

        public List<RecordedRequest> Requests { get; } = [];

        public RecordingHandler EnqueueJson(string body)
        {
            _responses.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri, request.Headers, body));

            if (_responses.Count == 0)
                throw new InvalidOperationException("RecordingHandler received more requests than were queued.");

            return _responses.Dequeue();
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
