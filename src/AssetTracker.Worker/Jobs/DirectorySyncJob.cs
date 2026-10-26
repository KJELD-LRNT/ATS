using AssetTracker.Core.Common;
using AssetTracker.Core.Directory;
using Microsoft.Extensions.Options;

namespace AssetTracker.Worker.Jobs;

/// <summary>
/// Runs a full directory sync at startup and then every <c>DirectorySync:Interval</c>. The interval is re-read
/// (<see cref="IOptionsMonitor{TOptions}.CurrentValue"/>) at least every <see cref="RecheckEvery"/>, so a change on
/// Admin → Settings applies without a restart. Each run gets its own DI scope; failures are logged and never stop
/// the schedule. Without a configured interval the job does not sync (no production default is guessed) and keeps
/// checking whether one has been set; the IT stale-sync warning makes the gap visible.
/// </summary>
public class DirectorySyncJob(IServiceScopeFactory scopes, IOptionsMonitor<DirectorySyncOptions> options,
    ILogger<DirectorySyncJob> logger) : BackgroundService
{
    /// <summary>Longest single sleep, so interval changes are noticed.</summary>
    public static readonly TimeSpan RecheckEvery = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset? lastRun = null;
        var warned = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = RecheckEvery;
            if (options.CurrentValue.Interval is not { } interval || interval <= TimeSpan.Zero)
            {
                if (!warned)
                    logger.LogError("DirectorySync:Interval is not configured; scheduled directory sync is disabled until it is set.");
                warned = true;
            }
            else
            {
                if (warned) logger.LogInformation("DirectorySync:Interval is now {Interval}; scheduled directory sync enabled.", interval);
                warned = false;
                var due = lastRun is null ? DateTimeOffset.UtcNow : lastRun.Value + interval;
                if (DateTimeOffset.UtcNow >= due)
                {
                    lastRun = DateTimeOffset.UtcNow;
                    await RunOnceAsync(stoppingToken);
                    due = lastRun.Value + interval;
                }
                var remaining = due - DateTimeOffset.UtcNow;
                if (remaining < wait) wait = remaining < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remaining;
            }

            try { await Task.Delay(wait, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
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
