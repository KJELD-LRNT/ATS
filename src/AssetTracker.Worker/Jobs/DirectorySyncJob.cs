using AssetTracker.Core.Common;
using AssetTracker.Core.Directory;
using Microsoft.Extensions.Options;

namespace AssetTracker.Worker.Jobs;

/// <summary>
/// Runs a full directory sync at startup and then every <c>DirectorySync:Interval</c>.
/// Each run gets its own DI scope; failures are logged and never stop the schedule.
/// Without a configured interval the job does not schedule (no production default is guessed); the IT stale-sync
/// warning then makes the gap visible.
/// </summary>
public class DirectorySyncJob(IServiceScopeFactory scopes, IOptions<DirectorySyncOptions> options, ILogger<DirectorySyncJob> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.Interval is not { } interval || interval <= TimeSpan.Zero)
        {
            logger.LogError("DirectorySync:Interval is not configured; scheduled directory sync is disabled.");
            return;
        }

        using var timer = new PeriodicTimer(interval);
        do
        {
            await RunOnceAsync(stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var run = await scope.ServiceProvider.GetRequiredService<DirectorySyncService>().RunAsync("system:schedule", ct);
            logger.LogInformation("Scheduled directory sync run {RunId} finished with status {Status}.", run.Id, run.Status);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (ConflictException ex)
        {
            logger.LogWarning("Scheduled directory sync skipped: {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled directory sync failed.");
        }
    }
}
