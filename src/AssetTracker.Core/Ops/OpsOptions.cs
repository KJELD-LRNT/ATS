namespace AssetTracker.Core.Ops;

/// <summary>
/// Thresholds for the IT ops dashboard and the anonymous <c>/health/ready</c> endpoint (config section <c>Ops</c>).
/// Defaults are placeholders until the client's monitoring expectations are agreed (Stage 0).
/// Directory sync freshness uses <c>DirectorySync:StaleAfter</c>.
/// </summary>
public class OpsOptions
{
    public const string SectionName = "Ops";

    /// <summary>
    /// How often the worker writes its heartbeat. Must match the worker's <c>Worker:HeartbeatInterval</c>.
    /// </summary>
    public TimeSpan WorkerHeartbeatInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>The worker is reported stale after this many missed heartbeat intervals.</summary>
    public int WorkerStaleAfterIntervals { get; set; } = 3;

    /// <summary>Pending mail older than this marks the outbox check Degraded (relay outage or worker stopped).</summary>
    public TimeSpan OutboxPendingMaxAge { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan WorkerStaleAfter => WorkerHeartbeatInterval * Math.Max(1, WorkerStaleAfterIntervals);
}
