using AssetTracker.Core.Ops;
using Microsoft.Extensions.Options;

namespace AssetTracker.Worker.Jobs;

/// <summary>Settings for the worker host itself (config section <c>Worker</c>).</summary>
public class WorkerOptions
{
    public const string SectionName = "Worker";

    /// <summary>
    /// How often the heartbeat is written. Keep in sync with the web app's <c>Ops:WorkerHeartbeatInterval</c>.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Writes a heartbeat (AppSetting <c>Worker.Heartbeat</c> + <c>Worker.Version</c>) at start-up and every
/// <see cref="WorkerOptions.HeartbeatInterval"/>, so the web app's ops dashboard and <c>/health/ready</c> can show
/// whether the worker is alive. A database outage is logged and retried on the next tick.
/// </summary>
public class WorkerHeartbeatJob(IServiceScopeFactory scopes, IOptions<WorkerOptions> options,
    ILogger<WorkerHeartbeatJob> logger) : BackgroundService
{
    private static readonly string Version = AppVersion.Of(typeof(WorkerHeartbeatJob).Assembly);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.HeartbeatInterval;
        if (interval <= TimeSpan.Zero) interval = TimeSpan.FromMinutes(1);
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<OpsStatusService>().RecordHeartbeatAsync(Version, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Writing the worker heartbeat failed; retrying on the next tick.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
