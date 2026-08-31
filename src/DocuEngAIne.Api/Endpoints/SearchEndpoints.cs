using DocuEngAIne.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace DocuEngAIne.Api.Endpoints;

public static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/search").RequireAuthorization();
        group.MapGet("", SearchAsync);
        return app;
    }

    public static async Task<IResult> SearchAsync(
        [FromQuery] string? q,
        ISearchService search,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        if (user.TenantId is null)
            return Results.Unauthorized();

        var hits = await search.SearchAsync(q ?? string.Empty, user.TenantId.Value, cancellationToken);
        return Results.Ok(hits);
    }
}
