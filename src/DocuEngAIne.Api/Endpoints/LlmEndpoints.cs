using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Llm;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DocuEngAIne.Api.Endpoints;

public static class LlmEndpoints
{
    public static IEndpointRouteBuilder MapLlmEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/llm").RequireAuthorization();

        group.MapGet("/config", GetConfig);
        group.MapPost("/chat", ChatAsync);

        return app;
    }

    public static IResult GetConfig(IOptions<LlmOptions> options)
    {
        var configured = options.Value;
        var model = LlmDefaults.ResolveModel(configured.Provider, configured.Model, request: null);
        return Results.Ok(new LlmConfigResponse(configured.Provider.ToString(), model));
    }

    public static async Task<IResult> ChatAsync(
        [FromBody] LlmChatRequest request,
        ILlmClient llm,
        ICurrentUser user,
        IAuditService audit,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        if (request.Messages is null || request.Messages.Count == 0)
            return Results.BadRequest("messages is required.");

        var messages = request.Messages
            .Select(m => new LlmMessage(m.Role, m.Content))
            .ToList();

        try
        {
            var result = await llm.ChatAsync(
                messages,
                new LlmChatOptions { Model = request.Model },
                cancellationToken);

            await audit.LogAsync(
                "Llm.Chat",
                "Llm",
                entityId: null,
                details: $"provider={result.Provider} model={result.Model}",
                cancellationToken);

            return Results.Ok(new LlmChatResponse(result.Content, result.Model, result.Provider.ToString()));
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(ex.Message);
        }
    }
}

public sealed record LlmChatRequest(IReadOnlyList<LlmChatMessageRequest>? Messages, string? Model = null);

public sealed record LlmChatMessageRequest(string Role, string Content);

public sealed record LlmChatResponse(string Content, string Model, string Provider);

public sealed record LlmConfigResponse(string Provider, string Model);
