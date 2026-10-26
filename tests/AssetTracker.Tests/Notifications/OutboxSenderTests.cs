using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Notifications;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Notifications;

/// <summary>
/// Tests in one class share a database, so assertions target this test's rows/recipients only:
/// a pass may legitimately also process rows left by earlier tests.
/// </summary>
public class OutboxSenderTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public OutboxSenderTests(AppFactory f)
    {
        _f = f;
        _f.Mail.FailWith = null;
    }

    private Task<OutboxBatchResult> ProcessAsync(int batch = 100) =>
        _f.RunAsAsync(TestUser.Nobody, sp => sp.GetRequiredService<OutboxSender>().ProcessDueAsync(batch, CancellationToken.None));

    private Task<NotificationOutbox> RowAsync(string recipient, DateTimeOffset? due = null) =>
        _f.WithDbAsync(db => NotificationSeed.OutboxRowAsync(db, recipient, due ?? _f.Clock.UtcNow));

    private Task<NotificationOutbox> ReloadAsync(long id) =>
        _f.WithDbAsync(db => db.Outbox.AsNoTracking().SingleAsync(x => x.Id == id));

    private int SentTo(string recipient) => _f.Mail.Sent.Count(m => m.To == recipient);

    [Fact]
    public async Task Due_rows_are_sent_and_marked_sent_by_a_roleless_worker()
    {
        var row = await RowAsync("sender.ok@example.test");
        var future = await RowAsync("sender.later@example.test", _f.Clock.UtcNow.AddMinutes(10));

        await ProcessAsync();

        var sent = await ReloadAsync(row.Id);
        Assert.Equal(NotificationStatus.Sent, sent.Status);
        Assert.Equal(_f.Clock.UtcNow, sent.SentAt);
        Assert.Equal(1, sent.Attempts);
        Assert.Equal(1, SentTo("sender.ok@example.test"));
        var m = _f.Mail.Sent.Single(x => x.To == "sender.ok@example.test");
        Assert.Equal(row.Subject, m.Subject);
        Assert.Equal(row.Body, m.Body);

        Assert.Equal(NotificationStatus.Pending, (await ReloadAsync(future.Id)).Status);
        Assert.Equal(0, SentTo("sender.later@example.test"));
    }

    [Fact]
    public async Task Relay_outage_keeps_rows_pending_with_backoff_and_they_are_sent_later()
    {
        const string to = "sender.outage@example.test";
        var row = await RowAsync(to);
        _f.Mail.FailWith = new IOException("relay unreachable");

        var t0 = _f.Clock.UtcNow;
        await ProcessAsync();
        var r1 = await ReloadAsync(row.Id);
        Assert.Equal(NotificationStatus.Pending, r1.Status);
        Assert.Equal(1, r1.Attempts);
        Assert.Equal(t0.AddMinutes(1), r1.NextAttemptAt);
        Assert.Equal(t0, r1.LastAttemptAt);
        Assert.Contains("relay unreachable", r1.LastError);

        // Not due yet: no attempt.
        await ProcessAsync();
        Assert.Equal(1, (await ReloadAsync(row.Id)).Attempts);

        _f.Clock.Advance(TimeSpan.FromMinutes(1));
        await ProcessAsync();
        var r2 = await ReloadAsync(row.Id);
        Assert.Equal(2, r2.Attempts);
        Assert.Equal(_f.Clock.UtcNow.AddMinutes(2), r2.NextAttemptAt);

        // Relay recovers.
        _f.Mail.FailWith = null;
        _f.Clock.Advance(TimeSpan.FromMinutes(2));
        await ProcessAsync();
        var r3 = await ReloadAsync(row.Id);
        Assert.Equal(NotificationStatus.Sent, r3.Status);
        Assert.Equal(3, r3.Attempts);
        Assert.Equal(1, SentTo(to));
    }

    [Fact]
    public void Backoff_doubles_and_is_capped()
    {
        var o = new NotificationOptions();
        Assert.Equal(TimeSpan.FromMinutes(1), OutboxSender.Backoff(1, o));
        Assert.Equal(TimeSpan.FromMinutes(2), OutboxSender.Backoff(2, o));
        Assert.Equal(TimeSpan.FromMinutes(32), OutboxSender.Backoff(6, o));
        Assert.Equal(TimeSpan.FromHours(1), OutboxSender.Backoff(7, o));
        Assert.Equal(TimeSpan.FromHours(1), OutboxSender.Backoff(50, o));
    }

    [Fact]
    public async Task Exhausted_retries_mark_row_failed_and_it_can_requeue_it()
    {
        const string to = "sender.exhaust@example.test";
        var row = await RowAsync(to);
        _f.Mail.FailWith = new InvalidOperationException("550 relay denied");

        for (var i = 0; i < 8; i++)
        {
            await ProcessAsync();
            _f.Clock.Advance(TimeSpan.FromHours(2));
        }

        var failed = await ReloadAsync(row.Id);
        Assert.Equal(NotificationStatus.Failed, failed.Status);
        Assert.Equal(8, failed.Attempts);
        Assert.Contains("550 relay denied", failed.LastError);

        // Failed rows are not retried any more.
        await ProcessAsync();
        Assert.Equal(8, (await ReloadAsync(row.Id)).Attempts);

        var status = await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<OutboxMonitor>().GetStatusAsync());
        Assert.True(status.FailedCount >= 1);
        Assert.NotNull(status.LastError);

        var page = await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<OutboxMonitor>().ListAsync());
        Assert.Contains(page.Rows, r => r.Id == row.Id && r.Status == NotificationStatus.Failed);

        await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<OutboxMonitor>().RequeueAsync(row.Id));
        var requeued = await ReloadAsync(row.Id);
        Assert.Equal(NotificationStatus.Pending, requeued.Status);
        Assert.Equal(0, requeued.Attempts);
        Assert.True(await _f.WithDbAsync(db => db.AuditEvents.AnyAsync(a =>
            a.Action == "notification.requeue" && a.EntityId == row.Id.ToString() && a.Actor == TestUser.It.Name)));

        _f.Mail.FailWith = null;
        await ProcessAsync();
        Assert.Equal(NotificationStatus.Sent, (await ReloadAsync(row.Id)).Status);
        Assert.Equal(1, SentTo(to));
    }

    [Fact]
    public async Task Requeue_all_failed_resets_every_failed_row_and_is_audited()
    {
        var ids = new List<long>();
        await _f.WithDbAsync(async db =>
        {
            for (var i = 0; i < 3; i++)
            {
                var r = await NotificationSeed.OutboxRowAsync(db, $"sender.all{i}@example.test", _f.Clock.UtcNow);
                r.Status = NotificationStatus.Failed;
                r.Attempts = 8;
                ids.Add(r.Id);
            }
            await db.SaveChangesAsync();
        });

        var count = await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<OutboxMonitor>().RequeueAllFailedAsync());

        Assert.True(count >= 3);
        var rows = await _f.WithDbAsync(db => db.Outbox.AsNoTracking().Where(x => ids.Contains(x.Id)).ToListAsync());
        Assert.All(rows, r => { Assert.Equal(NotificationStatus.Pending, r.Status); Assert.Equal(0, r.Attempts); });
        Assert.True(await _f.WithDbAsync(db => db.AuditEvents.AnyAsync(a => a.Action == "notification.requeue-all-failed")));
    }

    [Fact]
    public async Task Requeue_rejects_rows_that_are_not_failed()
    {
        var row = await RowAsync("sender.notfailed@example.test", _f.Clock.UtcNow.AddDays(30));
        await Assert.ThrowsAsync<DomainException>(() =>
            _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<OutboxMonitor>().RequeueAsync(row.Id)));
    }

    [Theory]
    [InlineData("Manager")]
    [InlineData("Admin")]
    [InlineData("Nobody")]
    public async Task Ops_api_requires_it(string userKey)
    {
        var user = userKey switch { "Manager" => TestUser.Manager, "Admin" => TestUser.Admin, _ => TestUser.Nobody };
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            _f.RunAsAsync(user, sp => sp.GetRequiredService<OutboxMonitor>().RequeueAllFailedAsync()));
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            _f.RunAsAsync(user, sp => sp.GetRequiredService<OutboxMonitor>().RequeueAsync(1)));
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            _f.RunAsAsync(user, sp => sp.GetRequiredService<OutboxMonitor>().GetStatusAsync()));
    }

    [Fact]
    public async Task Concurrent_senders_send_each_row_once()
    {
        var recipients = Enumerable.Range(0, 12).Select(i => $"sender.race{i}@example.test").ToList();
        foreach (var r in recipients) await RowAsync(r);

        await Task.WhenAll(ProcessAsync(), ProcessAsync(), ProcessAsync());

        Assert.All(recipients, r => Assert.Equal(1, SentTo(r)));
    }

    [Fact]
    public async Task Status_reports_pending_count_and_oldest_age()
    {
        var created = _f.Clock.UtcNow;
        await RowAsync("sender.status@example.test", created.AddDays(365)); // pending, not yet due
        _f.Clock.Advance(TimeSpan.FromMinutes(5));

        var status = await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<OutboxMonitor>().GetStatusAsync());

        Assert.True(status.PendingCount >= 1);
        Assert.NotNull(status.OldestPendingAge);
        Assert.True(status.OldestPendingAge >= TimeSpan.Zero);
    }
}
