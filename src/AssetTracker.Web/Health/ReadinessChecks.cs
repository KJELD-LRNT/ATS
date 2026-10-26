using System.Text.Json;
using AssetTracker.Core.Data;
using AssetTracker.Core.Directory;
using AssetTracker.Core.Ops;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AssetTracker.Web.Health;

/// <summary>
/// Readiness checks behind the anonymous <c>/health/ready</c> endpoint. Every check returns a fixed, non-sensitive
/// description: no personal data, exception messages, hostnames or connection strings ever reach the response.
/// Failures are logged server-side.
/// </summary>
public static class ReadinessChecks
{
    public const string Tag = "ready";

    public static IHealthChecksBuilder AddAssetTrackerReadiness(this IHealthChecksBuilder builder) => builder
        .AddCheck<DatabaseHealthCheck>("database", tags: [Tag])
        .AddCheck<DirectorySyncHealthCheck>("directory-sync", tags: [Tag])
        .AddCheck<OutboxHealthCheck>("mail-outbox", tags: [Tag])
        .AddCheck<WorkerHealthCheck>("worker", tags: [Tag]);

    /// <summary>Minimal JSON: overall status and each check's status and fixed description.</summary>
    public static Task WriteResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        var body = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                // Only descriptions written by our own checks; an exception's message is never echoed.
                description = e.Value.Exception is null ? e.Value.Description : "Check failed.",
            }),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(body));
    }

    internal static HealthCheckResult Failed(ILogger logger, Exception ex, string check)
    {
        logger.LogWarning(ex, "Health check {Check} failed.", check);
        return HealthCheckResult.Unhealthy("Check failed.");
    }
}

public class DatabaseHealthCheck(AppDbContext db, ILogger<DatabaseHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(ct)
                ? HealthCheckResult.Healthy("Database reachable.")
                : HealthCheckResult.Unhealthy("Database not reachable.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ReadinessChecks.Failed(logger, ex, "database");
        }
    }
}

/// <summary>Unhealthy when no sync ever succeeded; Degraded when stale or the latest run failed/was rejected.</summary>
public class DirectorySyncHealthCheck(DirectorySyncService sync, ILogger<DirectorySyncHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var s = await sync.GetStatusAsync(ct);
            if (s.LastSuccessAt is null) return HealthCheckResult.Unhealthy("No directory sync has succeeded yet.");
            if (s.IsStale) return HealthCheckResult.Degraded("Last successful directory sync is older than the stale threshold.");
            if (s.LastRunUnsuccessful) return HealthCheckResult.Degraded("The latest directory sync run failed or was rejected.");
            return HealthCheckResult.Healthy("Directory snapshot is fresh.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ReadinessChecks.Failed(logger, ex, "directory-sync");
        }
    }
}

/// <summary>Degraded when any mail has failed (retries exhausted) or the oldest pending mail exceeds <c>Ops:OutboxPendingMaxAge</c>.</summary>
public class OutboxHealthCheck(OpsStatusService ops, ILogger<OutboxHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var h = await ops.GetOutboxHealthAsync(ct);
            if (h.FailedCount > 0) return HealthCheckResult.Degraded("Failed notifications need attention.");
            if (h.IsDegraded) return HealthCheckResult.Degraded("Pending notifications are older than the threshold.");
            return HealthCheckResult.Healthy("Mail outbox is draining.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ReadinessChecks.Failed(logger, ex, "mail-outbox");
        }
    }
}

/// <summary>Unhealthy when the worker never reported; Degraded when its heartbeat is stale.</summary>
public class WorkerHealthCheck(OpsStatusService ops, ILogger<WorkerHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var w = await ops.GetWorkerHeartbeatAsync(ct);
            if (w.LastSeenAt is null) return HealthCheckResult.Unhealthy("The worker has never reported a heartbeat.");
            if (w.IsStale) return HealthCheckResult.Degraded("The worker heartbeat is stale.");
            return HealthCheckResult.Healthy("Worker heartbeat is current.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ReadinessChecks.Failed(logger, ex, "worker");
        }
    }
}
