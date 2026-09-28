using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Tenancy;

namespace DocuEngAIne.Api.Middleware;

/// <summary>
/// Refuses API requests for a tenant that a platform operator has suspended or archived, on every
/// route — with its status and reason, so the app can say why instead of failing each page. Runs
/// after authentication (the tenant comes from the token) and the IP allowlist (the tenant's own
/// network rule still applies first), before authorization. The platform console is exempt: it is
/// authorized by host configuration, not by the caller's tenant. API tokens and background sync
/// check the same status on their own paths.
/// </summary>
public sealed class TenantStatusMiddleware
{
    public const string SuspendedError = "tenant_suspended";
    public const string ArchivedError = "tenant_archived";

    private readonly RequestDelegate _next;

    public TenantStatusMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ICurrentUser user, TenantStatusService statuses)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/api")
            && !path.StartsWithSegments("/api/platform")
            && context.User.Identity?.IsAuthenticated == true
            && user.TenantId is Guid tenantId
            && await statuses.GetAsync(tenantId, context.RequestAborted) is { Status: not TenantStatus.Active } state)
        {
            var suspended = state.Status == TenantStatus.Suspended;
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(
                new
                {
                    error = suspended ? SuspendedError : ArchivedError,
                    status = state.Status.ToString(),
                    reason = state.Reason,
                    message = suspended
                        ? "This tenant is suspended. Contact your service provider to restore access."
                        : "This tenant has been archived. Contact your service provider to restore it.",
                },
                context.RequestAborted);
            return;
        }

        await _next(context);
    }
}
