using DocuEngAIne.Core.Enums;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Security;

namespace DocuEngAIne.Api.Middleware;

/// <summary>
/// Refuses API requests for a tenant whose IP allowlist is on and does not cover the caller.
/// Runs after authentication (the tenant comes from the token) and before authorization, so a
/// blocked request never reaches a handler. Anonymous requests (health probes, the SPA shell) carry
/// no tenant and pass. The outbound MCP endpoint authenticates inside its handler and applies the
/// same check there.
/// </summary>
public sealed class IpAllowlistMiddleware
{
    public const string BlockedError = "ip_not_allowed";

    private readonly RequestDelegate _next;

    public IpAllowlistMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context,
        ICurrentUser user,
        IpAllowlistService allowlist,
        SecurityEventRecorder events,
        ILogger<IpAllowlistMiddleware> logger)
    {
        if (context.Request.Path.StartsWithSegments("/api")
            && user.TenantId is Guid tenantId
            && !await allowlist.IsAllowedAsync(tenantId, context.Connection.RemoteIpAddress, context.RequestAborted))
        {
            var ip = IpAllowlist.Normalize(context.Connection.RemoteIpAddress)?.ToString();
            logger.LogWarning("IP allowlist blocked {Ip} for tenant {TenantId} on {Path}.", ip ?? "(unknown)", tenantId, context.Request.Path);
            await events.RecordAsync(
                tenantId,
                SecurityEventTypes.IpBlocked,
                $"{user.DisplayName ?? user.Email ?? "A signed-in user"} refused from {ip ?? "an unknown address"}: not on the IP allowlist.",
                ip,
                user.ObjectId,
                user.DisplayName ?? user.Email,
                context.Request.Path,
                cancellationToken: context.RequestAborted);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = BlockedError, ip }, context.RequestAborted);
            return;
        }

        await _next(context);
    }
}
