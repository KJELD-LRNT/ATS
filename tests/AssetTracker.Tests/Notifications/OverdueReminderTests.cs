using AssetTracker.Core.Domain;
using AssetTracker.Core.Notifications;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Notifications;

/// <summary>
/// Each test sets the clock explicitly and asserts only on its own loans (the class shares one database,
/// so a run also reminds about other tests' overdue loans).
/// </summary>
public class OverdueReminderTests : IClassFixture<AppFactory>
{
    private const string It = "it-overdue@example.test";
    private readonly AppFactory _f;

    public OverdueReminderTests(AppFactory f)
    {
        _f = f;
        _f.Clock.TimeZone = TimeZoneInfo.Utc;
    }

    private void SetNow(int year, int month, int day, int hour = 9, int minute = 0) =>
        _f.Clock.UtcNow = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero);

    /// <summary>Runs as a principal without roles, like the worker.</summary>
    private Task<OverdueRunResult> RunAsync() =>
        _f.RunAsAsync(TestUser.Nobody, sp => sp.GetRequiredService<OverdueReminderService>().QueueRemindersAsync(CancellationToken.None));

    private Task<List<NotificationOutbox>> RemindersAsync(int loanId) =>
        _f.WithDbAsync(db => db.Outbox.AsNoTracking()
            .Where(o => o.LoanId == loanId && o.Kind == NotificationKind.OverdueReminder)
            .OrderBy(o => o.Id).ToListAsync());

    private Task<Loan> LoanAsync(string key, DateOnly? due, bool active = true, string? email = "default") =>
        _f.WithDbAsync(async db =>
        {
            await NotificationSeed.SetItMailboxAsync(db, It);
            var site = await Seed.SiteAsync(db, key);
            var holder = await Seed.PersonAsync(db, "od." + key.ToLowerInvariant(), site, active: active, email: email);
            return await NotificationSeed.TaggedLoanAsync(db, site, holder, "OD-" + key, due);
        });

    [Fact]
    public async Task One_reminder_per_overdue_loan_to_holder_and_it()
    {
        SetNow(2026, 5, 11);
        var loan = await LoanAsync("OD1", new DateOnly(2026, 5, 8));

        var result = await RunAsync();

        Assert.Equal(new DateOnly(2026, 5, 11), result.LocalDate);
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
        Assert.True(await _f.RunAsAsync(TestUser.Nobody, sp =>
            sp.GetRequiredService<OverdueReminderService>().HasRunForAsync(new DateOnly(2026, 5, 11))));
    }

    [Fact]
    public async Task Rerun_on_the_same_day_does_not_duplicate_and_next_day_queues_again()
    {
        SetNow(2026, 6, 10, 6);
        var loan = await LoanAsync("OD2", new DateOnly(2026, 6, 1));

        await RunAsync();
        _f.Clock.Advance(TimeSpan.FromHours(10)); // same local day, e.g. a worker restart
        var rerun = await RunAsync();

        Assert.Equal(2, (await RemindersAsync(loan.Id)).Count);
        Assert.True(rerun.AlreadyQueued >= 2);

        _f.Clock.Advance(TimeSpan.FromHours(10)); // next day
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

    [Fact]
    public async Task Undated_due_today_and_closed_loans_get_no_reminder()
    {
        SetNow(2026, 7, 15);
        var undated = await LoanAsync("OD4A", null);
        var dueToday = await LoanAsync("OD4B", new DateOnly(2026, 7, 15));
        var dueYesterday = await LoanAsync("OD4C", new DateOnly(2026, 7, 14));
        var closed = await LoanAsync("OD4D", new DateOnly(2026, 7, 1));
        await _f.WithDbAsync(async db =>
        {
            var l = await db.Loans.SingleAsync(x => x.Id == closed.Id);
            l.ReturnedQuantity = 1;
            l.ClosedAt = _f.Clock.UtcNow;
            await db.SaveChangesAsync();
        });

        await RunAsync();

        Assert.Empty(await RemindersAsync(undated.Id));
        Assert.Empty(await RemindersAsync(dueToday.Id));
        Assert.Empty(await RemindersAsync(closed.Id));
        Assert.Equal(2, (await RemindersAsync(dueYesterday.Id)).Count);
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
    public async Task Local_day_boundary_uses_client_timezone()
    {
        // Europe/Amsterdam is UTC+1 in winter: 23:30 UTC on 9 Nov is already 00:30 on 10 Nov locally.
        _f.Clock.TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
        try
        {
            SetNow(2026, 11, 9, 22, 30); // 23:30 local on the due date: not yet overdue
            var loan = await LoanAsync("OD9", new DateOnly(2026, 11, 9));
            Assert.Equal(new DateOnly(2026, 11, 9), _f.Clock.LocalToday);
            await RunAsync();
            Assert.Empty(await RemindersAsync(loan.Id));

            SetNow(2026, 11, 9, 23, 30); // still 9 Nov in UTC, 10 Nov locally
            Assert.Equal(new DateOnly(2026, 11, 10), _f.Clock.LocalToday);
            var result = await RunAsync();

            Assert.Equal(new DateOnly(2026, 11, 10), result.LocalDate);
            var rows = await RemindersAsync(loan.Id);
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Contains(":2026-11-10:", r.DedupeKey));
        }
        finally
        {
            _f.Clock.TimeZone = TimeZoneInfo.Utc;
        }
    }
}
