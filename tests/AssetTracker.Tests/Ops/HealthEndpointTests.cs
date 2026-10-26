using System.Net;
using System.Text.Json;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Ops;
using AssetTracker.Tests.Infrastructure;
using AssetTracker.Tests.Notifications;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Ops;

/// <summary>
/// /health and /health/ready. Each test gets its own factory (fresh database), because readiness depends on
/// global state (sync runs, outbox, heartbeat).
/// </summary>
public class HealthEndpointTests
{
    private static HttpClient Anonymous(AppFactory f) =>
        f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<(HttpStatusCode Code, string Body, JsonElement Json)> ReadyAsync(AppFactory f)
    {
        var res = await Anonymous(f).GetAsync("/health/ready");
        var body = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, body, JsonDocument.Parse(body).RootElement);
    }

    private static string CheckStatus(JsonElement json, string name) =>
        json.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name)
            .GetProperty("status").GetString()!;

    private static Task SucceededSyncAsync(AppFactory f, DateTimeOffset finishedAt) => f.WithDbAsync(async db =>
    {
        db.SyncRuns.Add(new SyncRun
        {
            StartedAt = finishedAt.AddMinutes(-1), FinishedAt = finishedAt, Status = SyncRunStatus.Succeeded,
            TriggeredBy = "test",
        });
        await db.SaveChangesAsync();
    });

    private static Task HeartbeatAsync(AppFactory f) =>
        f.RunAsAsync(TestUser.Nobody, sp => sp.GetRequiredService<OpsStatusService>().RecordHeartbeatAsync("test-version"));

    private static async Task HealthyStateAsync(AppFactory f)
    {
        await SucceededSyncAsync(f, f.Clock.UtcNow);
        await HeartbeatAsync(f);
    }

    [Fact]
    public async Task Liveness_is_anonymous_and_has_no_dependencies()
    {
        using var f = new AppFactory();
        var res = await Anonymous(f).GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("Healthy", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ready_is_healthy_when_everything_is_current()
    {
        using var f = new AppFactory();
        await HealthyStateAsync(f);

        var (code, _, json) = await ReadyAsync(f);

        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("Healthy", json.GetProperty("status").GetString());
        foreach (var name in new[] { "database", "directory-sync", "mail-outbox", "worker" })
            Assert.Equal("Healthy", CheckStatus(json, name));
    }

    [Fact]
    public async Task Ready_is_unhealthy_without_any_sync_or_heartbeat_and_leaks_nothing()
    {
        using var f = new AppFactory();

        var (code, body, json) = await ReadyAsync(f);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, code);
        Assert.Equal("Unhealthy", json.GetProperty("status").GetString());
        Assert.Equal("Healthy", CheckStatus(json, "database"));
        Assert.Equal("Unhealthy", CheckStatus(json, "directory-sync"));
        Assert.Equal("Unhealthy", CheckStatus(json, "worker"));
        Assert.DoesNotContain("Data Source", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", body);
        Assert.DoesNotContain(".db", body);
    }

    [Fact]
    public async Task Ready_is_degraded_when_sync_is_stale()
    {
        using var f = new AppFactory();
        await SucceededSyncAsync(f, f.Clock.UtcNow.AddDays(-2));
        await HeartbeatAsync(f);

        var (code, _, json) = await ReadyAsync(f);

        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("Degraded", json.GetProperty("status").GetString());
        Assert.Equal("Degraded", CheckStatus(json, "directory-sync"));
    }

    [Fact]
    public async Task Ready_is_degraded_when_mail_failed_without_exposing_recipient_or_error()
    {
        using var f = new AppFactory();
        await HealthyStateAsync(f);
        await f.WithDbAsync(async db =>
        {
            var row = await NotificationSeed.OutboxRowAsync(db, "failed.health@example.test", f.Clock.UtcNow);
            row.Status = NotificationStatus.Failed;
            row.LastError = "SmtpCommandException: relay.internal refused";
            await db.SaveChangesAsync();
        });

        var (code, body, json) = await ReadyAsync(f);

        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal("Degraded", json.GetProperty("status").GetString());
        Assert.Equal("Degraded", CheckStatus(json, "mail-outbox"));
        Assert.DoesNotContain("failed.health", body);
        Assert.DoesNotContain("relay.internal", body);
    }

    [Fact]
    public async Task Ready_is_degraded_when_pending_mail_is_too_old()
    {
        using var f = new AppFactory();
        await HealthyStateAsync(f);
        await f.WithDbAsync(db => NotificationSeed.OutboxRowAsync(db, "old.pending@example.test", f.Clock.UtcNow.AddHours(-2)));

        var (_, _, json) = await ReadyAsync(f);

        Assert.Equal("Degraded", CheckStatus(json, "mail-outbox"));
    }

    [Fact]
    public async Task Worker_heartbeat_goes_stale_after_missed_intervals()
    {
        using var f = new AppFactory();
        await HealthyStateAsync(f);
        WorkerHeartbeat Status() => f.RunAsAsync(TestUser.Nobody,
            sp => sp.GetRequiredService<OpsStatusService>().GetWorkerHeartbeatAsync()).GetAwaiter().GetResult();

        var fresh = Status();
        Assert.False(fresh.IsStale);
        Assert.Equal("test-version", fresh.Version);

        f.Clock.Advance(TimeSpan.FromMinutes(2)); // default: stale after 3 x 1 min
        Assert.False(Status().IsStale);

        f.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(Status().IsStale);
        var (_, _, json) = await ReadyAsync(f);
        Assert.Equal("Degraded", CheckStatus(json, "worker"));

        // Sponsor decision (docs/decisions.md): a long worker outage stays Degraded, never Unhealthy.
        f.Clock.Advance(TimeSpan.FromDays(3));
        (_, _, json) = await ReadyAsync(f);
        Assert.Equal("Degraded", CheckStatus(json, "worker"));

        await HeartbeatAsync(f);
        Assert.False(Status().IsStale);
        var setting = await f.WithDbAsync(db => Task.FromResult(db.Settings.Single(s => s.Key == OpsStatusService.HeartbeatKey)));
        Assert.Equal("system:worker", setting.UpdatedBy);
        Assert.Equal(f.Clock.UtcNow, DateTimeOffset.Parse(setting.Value));
    }
}
