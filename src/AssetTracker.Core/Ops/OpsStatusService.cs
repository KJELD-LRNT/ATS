using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Directory;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Notifications;
using AssetTracker.Core.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetTracker.Core.Ops;

/// <summary>Worker liveness as recorded in <see cref="AppSetting"/> rows by the worker's heartbeat job.</summary>
/// <param name="LastSeenAt">Time of the latest heartbeat, or null if the worker never reported.</param>
/// <param name="Version">Worker build version reported with the heartbeat.</param>
/// <param name="Age">Time since the latest heartbeat.</param>
/// <param name="IsStale">No heartbeat, or the latest is older than <see cref="OpsOptions.WorkerStaleAfter"/>.</param>
public sealed record WorkerHeartbeat(DateTimeOffset? LastSeenAt, string? Version, TimeSpan? Age, bool IsStale);

/// <summary>Outbox counts without any recipient or message content (safe for the anonymous health endpoint).</summary>
public sealed record OutboxHealth(int PendingCount, int FailedCount, TimeSpan? OldestPendingAge, bool IsDegraded);

/// <summary>Everything on the IT ops dashboard. Sections are null when the database could not be read.</summary>
public sealed record OpsDashboard(
    bool DatabaseReachable,
    string AppVersion,
    DirectorySyncStatus? Sync,
    int? LastRunIssueCount,
    OutboxStatus? Outbox,
    WorkerHeartbeat? Worker,
    TimeSpan SyncStaleAfter,
    TimeSpan WorkerStaleAfter,
    TimeSpan OutboxPendingMaxAge);

/// <summary>
/// Operational status: worker heartbeat (written by the worker, read by the web app), outbox health and the IT
/// dashboard. The heartbeat uses two <see cref="AppSetting"/> rows (<see cref="HeartbeatKey"/>,
/// <see cref="VersionKey"/>, <c>UpdatedBy = system:worker</c>), so no schema change is needed.
/// <para>
/// <see cref="RecordHeartbeatAsync"/>, <see cref="GetWorkerHeartbeatAsync"/> and <see cref="GetOutboxHealthAsync"/>
/// are not role-guarded: they are used by the worker (system actor) and the anonymous health checks, and return no
/// personal data. <see cref="GetDashboardAsync"/> requires IT.
/// </para>
/// </summary>
public class OpsStatusService(
    AppDbContext db,
    IClock clock,
    AccessScope access,
    DirectorySyncService sync,
    OutboxMonitor outbox,
    IOptions<OpsOptions> options,
    IOptions<DirectorySyncOptions> syncOptions,
    ILogger<OpsStatusService> logger)
{
    public const string HeartbeatKey = "Worker.Heartbeat";
    public const string VersionKey = "Worker.Version";
    public const string WorkerActor = "system:worker";

    /// <summary>Upserts the worker heartbeat (ISO-8601 UTC timestamp) and version. Not audited (not a business change).</summary>
    public async Task RecordHeartbeatAsync(string version, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        await UpsertAsync(HeartbeatKey, now.ToString("O"), now, ct);
        await UpsertAsync(VersionKey, version.Length <= 2000 ? version : version[..2000], now, ct);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Only possible on the very first heartbeat if two workers race on insert; the next tick updates the row.
            logger.LogWarning(ex, "Worker heartbeat could not be saved; it will be retried on the next tick.");
            db.ChangeTracker.Clear();
        }
    }

    private async Task UpsertAsync(string key, string value, DateTimeOffset now, CancellationToken ct)
    {
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            row = new AppSetting { Key = key };
            db.Settings.Add(row);
        }
        row.Value = value;
        row.UpdatedAt = now;
        row.UpdatedBy = WorkerActor;
    }

    public async Task<WorkerHeartbeat> GetWorkerHeartbeatAsync(CancellationToken ct = default)
    {
        var rows = await db.Settings.AsNoTracking()
            .Where(s => s.Key == HeartbeatKey || s.Key == VersionKey)
            .Select(s => new { s.Key, s.Value, s.UpdatedAt })
            .ToListAsync(ct);
        var beat = rows.FirstOrDefault(r => r.Key == HeartbeatKey);
        var version = rows.FirstOrDefault(r => r.Key == VersionKey)?.Value;
        if (beat is null) return new WorkerHeartbeat(null, version, null, true);

        var age = clock.UtcNow - beat.UpdatedAt;
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        return new WorkerHeartbeat(beat.UpdatedAt, version, age, age > options.Value.WorkerStaleAfter);
    }

    /// <summary>Pending/failed counts and oldest pending age; Degraded when anything failed or mail waits too long.</summary>
    public async Task<OutboxHealth> GetOutboxHealthAsync(CancellationToken ct = default)
    {
        var pending = await db.Outbox.CountAsync(x => x.Status == NotificationStatus.Pending, ct);
        var failed = await db.Outbox.CountAsync(x => x.Status == NotificationStatus.Failed, ct);
        var oldest = await db.Outbox.Where(x => x.Status == NotificationStatus.Pending)
            .OrderBy(x => x.CreatedAt).Select(x => (DateTimeOffset?)x.CreatedAt).FirstOrDefaultAsync(ct);
        var now = clock.UtcNow;
        TimeSpan? age = oldest is { } o ? (now > o ? now - o : TimeSpan.Zero) : null;
        var degraded = failed > 0 || age > options.Value.OutboxPendingMaxAge;
        return new OutboxHealth(pending, failed, age, degraded);
    }

    /// <summary>The IT ops dashboard. Requires IT. A database outage is reported on the page, not thrown.</summary>
    public async Task<OpsDashboard> GetDashboardAsync(string appVersion, CancellationToken ct = default)
    {
        access.EnsureIt();
        var o = options.Value;
        var result = new OpsDashboard(false, appVersion, null, null, null, null,
            syncOptions.Value.StaleAfter, o.WorkerStaleAfter, o.OutboxPendingMaxAge);

        bool reachable;
        try
        {
            reachable = await db.Database.CanConnectAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Database connectivity check failed.");
            reachable = false;
        }
        if (!reachable) return result;

        try
        {
            var status = await sync.GetStatusAsync(ct);
            var last = await db.SyncRuns.AsNoTracking().OrderByDescending(r => r.Id)
                .Select(r => new { r.IssuesJson }).FirstOrDefaultAsync(ct);
            int? issueCount = last is null ? null : DirectorySyncService.ParseIssues(last.IssuesJson).Count;
            var outboxStatus = await outbox.GetStatusAsync(ct);
            var worker = await GetWorkerHeartbeatAsync(ct);
            return result with
            {
                DatabaseReachable = true, Sync = status, LastRunIssueCount = issueCount, Outbox = outboxStatus,
                Worker = worker,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AccessDeniedException)
        {
            logger.LogWarning(ex, "Reading operational status failed.");
            return result with { DatabaseReachable = true };
        }
    }
}
