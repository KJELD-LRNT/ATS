using AssetTracker.Core.Domain;
using AssetTracker.Core.Notifications;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Notifications;

/// <summary>
/// Per-site overdue reminders (WP4.2, D4). Each test sets the clock explicitly and asserts only on its own loans
/// (the class shares one database, so a run also reminds about other tests' overdue loans). The default timezone is
/// UTC and the reminder time 08:00 (defaults); tests that need another zone give their site a <c>TimeZoneId</c>.
/// </summary>
public class OverdueReminderTests : IClassFixture<AppFactory>
{
    private const string It = "it-overdue@example.test";
    private const string Amsterdam = "Europe/Amsterdam";
    private const string NewYork = "America/New_York";
    private readonly AppFactory _f;

    public OverdueReminderTests(AppFactory f) => _f = f;

    private void SetNow(int year, int month, int day, int hour = 9, int minute = 0) =>
        _f.Clock.UtcNow = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero);

    /// <summary>One worker tick, as a principal without roles (like the worker).</summary>
    private Task<OverdueRunResult> RunAsync() =>
        _f.RunAsAsync(TestUser.Nobody, sp => sp.GetRequiredService<OverdueReminderService>().QueueRemindersAsync(CancellationToken.None));

    private Task<List<NotificationOutbox>> RemindersAsync(int loanId) =>
        _f.WithDbAsync(db => db.Outbox.AsNoTracking()
            .Where(o => o.LoanId == loanId && o.Kind == NotificationKind.OverdueReminder)
            .OrderBy(o => o.Id).ToListAsync());

    private Task<int> RunAuditCountAsync() =>
        _f.WithDbAsync(db => db.AuditEvents.CountAsync(a => a.Action == OverdueReminderService.RunAuditAction));

    private Task<Loan> LoanAsync(string key, DateOnly? due, bool active = true, string? email = "default",
        string? timeZoneId = null) =>
        _f.WithDbAsync(async db =>
        {
            await NotificationSeed.SetItMailboxAsync(db, It);
            var site = await Seed.SiteAsync(db, key, timeZoneId: timeZoneId);
            var holder = await Seed.PersonAsync(db, "od." + key.ToLowerInvariant(), site, active: active, email: email);
            return await NotificationSeed.TaggedLoanAsync(db, site, holder, "OD-" + key, due);
        });

    [Fact]
    public async Task One_reminder_per_overdue_loan_to_holder_and_it()
    {
        SetNow(2026, 5, 11);
        var loan = await LoanAsync("OD1", new DateOnly(2026, 5, 8));
        var auditsBefore = await RunAuditCountAsync();

        var result = await RunAsync();

        Assert.Contains(result.DueSites, d => d.LocalDate == new DateOnly(2026, 5, 11));
        var rows = await RemindersAsync(loan.Id);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(NotificationStatus.Pending, r.Status);
            Assert.StartsWith($"overdue:{loan.Id}:2026-05-11:", r.DedupeKey);
            Assert.Contains("OD-OD1", r.Subject);
            Assert.Contains("2026-05-08", r.Body);
            Assert.Contains("3 day(s) overdue", r.Body);
            Assert.Contains("Site OD1", r.Body);
        });
        Assert.Equal(new[] { It, "od.od1@example.test" }, rows.Select(r => r.RecipientEmail).Order());
        Assert.Equal(auditsBefore + 1, await RunAuditCountAsync());
    }

    [Fact]
    public async Task Ticks_before_the_reminder_time_queue_nothing_and_reruns_never_duplicate()
    {
        SetNow(2026, 6, 10, 6); // 06:00 at a UTC site: before the 08:00 reminder time
        var loan = await LoanAsync("OD2", new DateOnly(2026, 6, 1));

        await RunAsync();
        Assert.Empty(await RemindersAsync(loan.Id));

        _f.Clock.Advance(TimeSpan.FromHours(3)); // 09:00
        await RunAsync();
        Assert.Equal(2, (await RemindersAsync(loan.Id)).Count);

        var auditsBefore = await RunAuditCountAsync();
        _f.Clock.Advance(TimeSpan.FromHours(10)); // 19:00 the same local day, e.g. a worker restart
        var rerun = await RunAsync();
        Assert.Equal(2, (await RemindersAsync(loan.Id)).Count);
        Assert.True(rerun.AlreadyQueued >= 2);
        Assert.Equal(0, rerun.Queued);
        Assert.Equal(auditsBefore, await RunAuditCountAsync()); // idle ticks are not audited

        _f.Clock.Advance(TimeSpan.FromHours(10)); // 05:00 the next day: before the reminder time
        await RunAsync();
        Assert.Equal(2, (await RemindersAsync(loan.Id)).Count);

        _f.Clock.Advance(TimeSpan.FromHours(4)); // 09:00 the next day
        await RunAsync();
        var rows = await RemindersAsync(loan.Id);
        Assert.Equal(4, rows.Count);
        Assert.Equal(2, rows.Count(r => r.DedupeKey.Contains(":2026-06-11:")));
    }

    [Fact]
    public async Task Concurrent_runs_do_not_double_queue()
    {
        SetNow(2026, 6, 20);
        var loan = await LoanAsync("OD3", new DateOnly(2026, 6, 2));

        await Task.WhenAll(RunAsync(), RunAsync());

        Assert.Equal(2, (await RemindersAsync(loan.Id)).Count);
    }

    /// <summary>D4: an item is overdue (and reminded) from its due date; "due today" wording on that day.</summary>
    [Fact]
    public async Task Due_today_is_reminded_but_undated_due_tomorrow_and_closed_loans_are_not()
    {
        SetNow(2026, 7, 15);
        var undated = await LoanAsync("OD4A", null);
        var dueToday = await LoanAsync("OD4B", new DateOnly(2026, 7, 15));
        var dueYesterday = await LoanAsync("OD4C", new DateOnly(2026, 7, 14));
        var closed = await LoanAsync("OD4D", new DateOnly(2026, 7, 1));
        var dueTomorrow = await LoanAsync("OD4E", new DateOnly(2026, 7, 16));
        await _f.WithDbAsync(async db =>
        {
            var l = await db.Loans.SingleAsync(x => x.Id == closed.Id);
            l.ReturnedQuantity = 1;
            l.ClosedAt = _f.Clock.UtcNow;
            await db.SaveChangesAsync();
        });

        await RunAsync();

        Assert.Empty(await RemindersAsync(undated.Id));
        Assert.Empty(await RemindersAsync(closed.Id));
        Assert.Empty(await RemindersAsync(dueTomorrow.Id));
        var today = await RemindersAsync(dueToday.Id);
        Assert.Equal(2, today.Count);
        Assert.All(today, r =>
        {
            Assert.StartsWith("Due today:", r.Subject);
            Assert.Contains("is due back today", r.Body);
        });
        var yesterday = await RemindersAsync(dueYesterday.Id);
        Assert.Equal(2, yesterday.Count);
        Assert.All(yesterday, r =>
        {
            Assert.StartsWith("Overdue:", r.Subject);
            Assert.Contains("1 day(s) overdue", r.Body);
        });
    }

    [Theory]
    [InlineData(false, "former@example.test", "inactive")]
    [InlineData(true, null, "no email address")]
    [InlineData(true, "broken@", "invalid")]
    public async Task Inactive_or_mailless_holder_gets_it_only_reminder(bool active, string? email, string reasonPart)
    {
        SetNow(2026, 8, 3);
        var loan = await LoanAsync("OD5" + reasonPart[..3].ToUpperInvariant(), new DateOnly(2026, 7, 31), active, email);

        await RunAsync();

        var row = Assert.Single(await RemindersAsync(loan.Id));
        Assert.Equal(It, row.RecipientEmail);
        Assert.Equal(NotificationStatus.Pending, row.Status);
        Assert.Contains("Only IT receives this reminder", row.Body);
        Assert.Contains(reasonPart, row.Body);
    }

    [Fact]
    public async Task Partially_returned_pool_loan_gets_a_single_reminder_per_recipient()
    {
        SetNow(2026, 9, 7);
        var loan = await _f.WithDbAsync(async db =>
        {
            await NotificationSeed.SetItMailboxAsync(db, It);
            var site = await Seed.SiteAsync(db, "OD6");
            var holder = await Seed.PersonAsync(db, "od.pool", site);
            return await NotificationSeed.PoolLoanAsync(db, site, holder, "OD-HS-6", quantity: 4, returned: 1,
                due: new DateOnly(2026, 9, 1));
        });

        await RunAsync();

        var rows = await RemindersAsync(loan.Id);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Contains("4 x OD-HS-6", r.Subject);
            Assert.Matches(@"Outstanding:\s+3\r?\n", r.Body);
        });
    }

    [Fact]
    public async Task Holder_equal_to_it_mailbox_gets_one_reminder()
    {
        SetNow(2026, 9, 21);
        var loan = await LoanAsync("OD7", new DateOnly(2026, 9, 1), email: It.ToUpperInvariant());

        await RunAsync();

        var row = Assert.Single(await RemindersAsync(loan.Id));
        Assert.Equal(It, row.RecipientEmail);
    }

    [Fact]
    public async Task Missing_it_mailbox_records_skipped_row_and_holder_still_reminded()
    {
        SetNow(2026, 10, 5);
        var loan = await LoanAsync("OD8", new DateOnly(2026, 10, 1));
        await _f.WithDbAsync(db => NotificationSeed.SetItMailboxAsync(db, null));
        try
        {
            await RunAsync();
        }
        finally
        {
            await _f.WithDbAsync(db => NotificationSeed.SetItMailboxAsync(db, It));
        }

        var rows = await RemindersAsync(loan.Id);
        Assert.Equal(2, rows.Count);
        Assert.Equal("od.od8@example.test", rows.Single(r => r.Status == NotificationStatus.Pending).RecipientEmail);
        var skipped = rows.Single(r => r.Status == NotificationStatus.Skipped);
        Assert.Equal(NotificationService.ItMailboxNotConfigured, skipped.SkipReason);
        Assert.Null(skipped.RecipientEmail);
    }

    [Fact]
    public async Task Local_day_and_reminder_time_use_the_sites_timezone()
    {
        // Europe/Amsterdam is UTC+1 in winter: 23:30 UTC on 9 Nov is already 00:30 on 10 Nov locally.
        SetNow(2026, 11, 9, 22, 30); // 23:30 local on 9 Nov: the item due on the 10th is not yet due
        var loan = await LoanAsync("OD9", new DateOnly(2026, 11, 10), timeZoneId: Amsterdam);
        await RunAsync();
        Assert.Empty(await RemindersAsync(loan.Id));

        SetNow(2026, 11, 9, 23, 30); // 00:30 on 10 Nov locally: overdue (D4) but before the 08:00 reminder time
        await RunAsync();
        Assert.Empty(await RemindersAsync(loan.Id));

        SetNow(2026, 11, 10, 7, 0); // 08:00 local
        await RunAsync();
        var rows = await RemindersAsync(loan.Id);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Contains(":2026-11-10:", r.DedupeKey));
        Assert.All(rows, r => Assert.Contains("is due back today", r.Body));
    }

    [Fact]
    public async Task Sites_in_different_timezones_are_reminded_at_their_own_local_time()
    {
        var due = new DateOnly(2026, 10, 20);
        var ams = await LoanAsync("OD10A", due, timeZoneId: Amsterdam); // UTC+2 (CEST) until 25 Oct 2026
        var nyc = await LoanAsync("OD10N", due, timeZoneId: NewYork); // UTC-4 (EDT) until 1 Nov 2026

        // 07:30 UTC on 21 Oct: 09:30 in Amsterdam (past 08:00), 03:30 in New York (not yet).
        SetNow(2026, 10, 21, 7, 30);
        var first = await RunAsync();
        Assert.Equal(2, (await RemindersAsync(ams.Id)).Count);
        Assert.Empty(await RemindersAsync(nyc.Id));
        var nycSiteId = await _f.WithDbAsync(db => db.Assets.Where(a => a.Id == nyc.AssetId).Select(a => a.SiteId).SingleAsync());
        Assert.DoesNotContain(first.DueSites, d => d.SiteId == nycSiteId);

        // A later tick the same day: 12:15 UTC = 08:15 in New York.
        SetNow(2026, 10, 21, 12, 15);
        await RunAsync();
        Assert.Equal(2, (await RemindersAsync(nyc.Id)).Count);
        Assert.Equal(2, (await RemindersAsync(ams.Id)).Count);

        // Reruns later that day never duplicate either site.
        SetNow(2026, 10, 21, 20, 0);
        await RunAsync();
        await RunAsync();
        var amsRows = await RemindersAsync(ams.Id);
        var nycRows = await RemindersAsync(nyc.Id);
        Assert.Equal(2, amsRows.Count);
        Assert.Equal(2, nycRows.Count);
        Assert.All(amsRows.Concat(nycRows), r => Assert.Contains(":2026-10-21:", r.DedupeKey));
        Assert.All(amsRows.Concat(nycRows), r => Assert.Contains("1 day(s) overdue", r.Body));
    }

    [Fact]
    public async Task Dst_transition_day_produces_exactly_one_reminder_per_recipient()
    {
        // Summer time ends 25 Oct 2026 in Amsterdam: 03:00 CEST becomes 02:00 CET, so the local day has 25 hours.
        var loan = await LoanAsync("OD11", new DateOnly(2026, 10, 24), timeZoneId: Amsterdam);

        // Tick every 30 minutes from 00:00 local on the 25th (22:00 UTC on the 24th) to 23:30 local (22:30 UTC).
        for (var t = new DateTimeOffset(2026, 10, 24, 22, 0, 0, TimeSpan.Zero);
             t <= new DateTimeOffset(2026, 10, 25, 22, 30, 0, TimeSpan.Zero);
             t = t.AddMinutes(30))
        {
            _f.Clock.UtcNow = t;
            await RunAsync();
        }

        var rows = await RemindersAsync(loan.Id);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Contains(":2026-10-25:", r.DedupeKey));
        Assert.All(rows, r => Assert.True(r.CreatedAt == new DateTimeOffset(2026, 10, 25, 7, 0, 0, TimeSpan.Zero),
            $"queued at {r.CreatedAt:O}, expected the first tick at 08:00 CET (07:00 UTC)"));
    }

    [Fact]
    public async Task Tagged_loan_follows_its_assets_current_site()
    {
        // Loan due 3 Mar, checked out at a UTC site; the asset then moves to a UTC+14 site.
        var loan = await LoanAsync("OD12", new DateOnly(2026, 3, 3));
        var kiritimati = await _f.WithDbAsync(db => Seed.SiteAsync(db, "OD12K", timeZoneId: "Pacific/Kiritimati"));
        await _f.WithDbAsync(async db =>
        {
            var asset = await db.Assets.SingleAsync(a => a.Id == loan.AssetId);
            asset.SiteId = kiritimati.Id;
            await db.SaveChangesAsync();
        });

        // 19:00 UTC on 2 Mar: still the 2nd at the old (UTC) site, but 09:00 on 3 Mar at the asset's current site.
        SetNow(2026, 3, 2, 19);
        await RunAsync();

        var rows = await RemindersAsync(loan.Id);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Contains(":2026-03-03:", r.DedupeKey));
    }
}
