using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocuEngAIne.Infrastructure.Audit;

/// <summary>Runs the audit retention purge once at startup (after a delay) and then daily.</summary>
public sealed class AuditRetentionHostedService : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AuditRetentionHostedService> _logger;

    public AuditRetentionHostedService(IServiceScopeFactory scopes, ILogger<AuditRetentionHostedService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        using var timer = new PeriodicTimer(Interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var retention = scope.ServiceProvider.GetRequiredService<AuditRetentionService>();
                var removed = await retention.PurgeAsync(cancellationToken: stoppingToken);
                if (removed > 0)
                    _logger.LogInformation("Audit retention purged {Count} event(s) older than {Days} days.", removed, retention.RetentionDays);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Audit retention purge failed.");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
