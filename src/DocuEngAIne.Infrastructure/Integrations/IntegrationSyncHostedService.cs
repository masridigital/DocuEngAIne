using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>Polls due integration connections and runs them through <see cref="IntegrationSyncRunner"/>.</summary>
public sealed class IntegrationSyncHostedService : BackgroundService
{
    /// <summary>How often the host looks for due connections. Cadence per connection is separate.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);

    private readonly IntegrationSyncRunner _runner;
    private readonly ILogger<IntegrationSyncHostedService> _logger;

    public IntegrationSyncHostedService(IntegrationSyncRunner runner, ILogger<IntegrationSyncHostedService> logger)
    {
        _runner = runner;
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

        using var timer = new PeriodicTimer(PollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await _runner.RunDueAsync(stoppingToken);
                if (result.QueuedConnectionIds.Count > 0)
                {
                    _logger.LogInformation(
                        "Scheduled sync queued {Count} connection(s).",
                        result.QueuedConnectionIds.Count);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Integration sync scheduler tick failed.");
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
