using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Security;

namespace DocuEngAIne.Api.Middleware;

/// <summary>
/// Resolves the caller's company scope once per authorized API request, before the endpoint runs,
/// so the DbContext's company filters and the write guards see it. Runs after authorization, so a
/// request that is going to be refused anyway costs no scope query. <c>/mcp</c> is tenant-wide by
/// design (its tokens are minted by admins) and is not scoped.
/// </summary>
public sealed class CompanyScopeMiddleware
{
    private readonly RequestDelegate _next;

    public CompanyScopeMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context,
        ICurrentUser user,
        ICompanyScopeAccessor accessor,
        CompanyScopeResolver resolver)
    {
        if (context.Request.Path.StartsWithSegments("/api") && user.IsAuthenticated && user.TenantId is not null)
            accessor.Current = await resolver.ResolveAsync(user, context.RequestAborted);

        await _next(context);
    }
}
