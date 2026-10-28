using AssetTracker.Core.Security;
using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Lending;
using AssetTracker.Core.Notifications;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Lending;

/// <summary>
/// Checkout and return (WP2.1). The class shares one database and clock; tests only move the clock forward and
/// derive due dates from <c>Clock.UtcToday</c> (the default timezone is UTC in tests).
/// </summary>
public class LendingServiceTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public LendingServiceTests(AppFactory f) => _f = f;

    private DateOnly InDays(int days) => _f.Clock.UtcToday.AddDays(days);

    [Fact]
    public async Task Checkout_tagged_asset_creates_loan_event_audit_and_outbox()
    {
        var site = await _f.SiteAsync("LS01");
        var holder = await _f.PersonAsync("ls01.holder", site);
        var asset = await _f.AssetAsync("LS-0001", site);

        var result = await _f.CheckoutTaggedAsync(asset, holder, InDays(7));

        Assert.Equal(AssetState.CheckedOut, (await _f.ReloadAssetAsync(asset.Id)).State);
        var loan = await _f.ReloadLoanAsync(result.Loan.Id);
        Assert.Equal((asset.Id, holder.Id, 1, 0), (loan.AssetId!.Value, loan.HolderId, loan.Quantity, loan.ReturnedQuantity));
        Assert.True(loan.IsOpen);
        Assert.Equal("ls01.holder", loan.HolderNameSnapshot);
        Assert.Equal("ls01.holder@example.test", loan.HolderEmailSnapshot);
        Assert.Equal(TestUser.It.Name, loan.CreatedBy);

        var ev = Assert.Single(await _f.EventsAsync(loan.Id));
        Assert.Equal(LoanEventType.CheckedOut, ev.Type);
        Assert.Equal(InDays(7), ev.DueDate);

        var audit = Assert.Single(await _f.AuditAsync("loan.checkout", loan.Id));
        Assert.Equal(site.Id, audit.SiteId);

        var rows = await _f.OutboxAsync(loan.Id, NotificationKind.Checkout);
        Assert.Equal(["ls01.holder@example.test", LendingTestSupport.ItMailbox],
            rows.Select(r => r.RecipientEmail).OrderByDescending(e => e!.StartsWith("ls01")));
        Assert.All(rows, r => Assert.Equal(NotificationStatus.Pending, r.Status));
        Assert.All(rows, r => Assert.Equal(ev.Id, r.LoanEventId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-address")]
    public async Task Missing_or_invalid_email_blocks_checkout(string? email)
    {
        var key = "LS02" + (email?.Length ?? 9);
        var site = await _f.SiteAsync(key);
        var holder = await _f.PersonAsync(key.ToLowerInvariant() + ".holder", site, email: email);
        var asset = await _f.AssetAsync("LS-" + key, site);

        var ex = await Assert.ThrowsAsync<DomainException>(() => _f.CheckoutTaggedAsync(asset, holder));

        Assert.Equal("loan.holder_no_email", ex.Code);
        Assert.Contains("missing email blocks checkout", ex.Message);
        Assert.Equal(AssetState.Available, (await _f.ReloadAssetAsync(asset.Id)).State);
        Assert.False(await _f.WithDbAsync(db => db.Loans.AnyAsync(l => l.AssetId == asset.Id)));
    }

    [Fact]
    public async Task Inactive_person_cannot_receive_a_new_loan()
    {
        var site = await _f.SiteAsync("LS03");
        var holder = await _f.PersonAsync("ls03.holder", site, active: false);
        var pool = await _f.PoolAsync("HS-LS03", site, 5);

        var ex = await Assert.ThrowsAsync<DomainException>(() => _f.CheckoutPoolAsync(pool, holder, 1));

        Assert.Equal("loan.holder_inactive", ex.Code);
        Assert.Equal(5, (await _f.ReloadPoolAsync(pool.Id)).Available);
    }

    [Fact]
    public async Task Due_date_in_the_past_is_rejected()
    {
        var site = await _f.SiteAsync("LS04");
        var holder = await _f.PersonAsync("ls04.holder", site);
        var asset = await _f.AssetAsync("LS-0004", site);

        var ex = await Assert.ThrowsAsync<DomainException>(() => _f.CheckoutTaggedAsync(asset, holder, InDays(-1)));
        Assert.Equal("loan.due_past", ex.Code);
    }

    [Fact]
    public async Task Manager_and_admin_cannot_check_out_or_return()
    {
        var site = await _f.SiteAsync("LS05");
        var holder = await _f.PersonAsync("ls05.holder", site);
        var asset = await _f.AssetAsync("LS-0005", site);
        await _f.WithDbAsync(db => Seed.ManagerWithGrantsAsync(db,
            new TestUser(@"CORP\mgr.ls05", "S-1-5-21-1000-1000-1000-3501", AppRoles.Manager), site));

        foreach (var user in new[] { TestUser.Manager, TestUser.Admin, TestUser.Nobody })
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => _f.RunAsAsync(user, sp =>
                sp.GetRequiredService<LendingService>().CheckoutAsync(new CheckoutRequest(holder.Id, AssetId: asset.Id))));
        }

        var loan = (await _f.CheckoutTaggedAsync(asset, holder)).Loan;
        await Assert.ThrowsAsync<AccessDeniedException>(() => _f.RunAsAsync(TestUser.Manager, sp =>
            sp.GetRequiredService<LendingService>().ReturnAsync(loan.Id)));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _f.RunAsAsync(TestUser.Manager, sp =>
            sp.GetRequiredService<LendingService>().SearchPeopleAsync("ls05")));
    }

    [Fact]
    public async Task Concurrent_checkouts_of_one_tagged_asset_succeed_exactly_once()
    {
        var site = await _f.SiteAsync("LS06");
        var asset = await _f.AssetAsync("LS-0006", site);
        var holders = new List<Person>();
        for (var i = 0; i < 10; i++) holders.Add(await _f.PersonAsync($"ls06.holder{i}", site));

        using var start = new ManualResetEventSlim();
        var attempts = holders.Select(h => Task.Run(async () =>
        {
            start.Wait();
            try { await _f.CheckoutTaggedAsync(asset, h); return true; }
            catch (ConflictException) { return false; }
        })).ToList();
        start.Set();
        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(ok => ok));
        Assert.Equal(1, await _f.WithDbAsync(db => db.Loans.CountAsync(l => l.AssetId == asset.Id)));
        Assert.Equal(AssetState.CheckedOut, (await _f.ReloadAssetAsync(asset.Id)).State);
    }

    [Fact]
    public async Task Concurrent_pool_checkouts_never_exceed_available()
    {
        var site = await _f.SiteAsync("LS07");
        var holder = await _f.PersonAsync("ls07.holder", site);
        var pool = await _f.PoolAsync("HS-LS07", site, 7);

        using var start = new ManualResetEventSlim();
        var attempts = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            start.Wait();
            try { await _f.CheckoutPoolAsync(pool, holder, 2); return true; }
            catch (ConflictException) { return false; }
        })).ToList();
        start.Set();
        var results = await Task.WhenAll(attempts);

        Assert.Equal(3, results.Count(ok => ok));
        var stored = await _f.ReloadPoolAsync(pool.Id);
        Assert.Equal((1, 6), (stored.Available, stored.Outstanding));
        Assert.Equal(6, await _f.WithDbAsync(db => db.Loans.Where(l => l.PoolId == pool.Id).SumAsync(l => l.Quantity)));
    }

    [Fact]
    public async Task Pool_checkout_beyond_available_reports_how_many_are_left()
    {
        var site = await _f.SiteAsync("LS08");
        var holder = await _f.PersonAsync("ls08.holder", site);
        var pool = await _f.PoolAsync("HS-LS08", site, 3);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => _f.CheckoutPoolAsync(pool, holder, 4));
        Assert.Contains("Only 3", ex.Message);
    }

    [Theory]
    [InlineData(false, AssetState.Available)]
    [InlineData(true, AssetState.Unavailable)]
    public async Task Return_tagged_asset_sets_state_and_closes_loan(bool markUnavailable, AssetState expected)
    {
        var key = markUnavailable ? "LS09U" : "LS09A";
        var site = await _f.SiteAsync(key);
        var holder = await _f.PersonAsync(key.ToLowerInvariant() + ".holder", site);
        var asset = await _f.AssetAsync("LS-" + key, site);
        var loan = (await _f.CheckoutTaggedAsync(asset, holder)).Loan;
        _f.Clock.Advance(TimeSpan.FromHours(1));

        var r = await _f.AsItLending(s => s.ReturnAsync(loan.Id, condition: "Scratched", markUnavailable: markUnavailable));

        Assert.True(r.FullyReturned);
        Assert.Equal(expected, (await _f.ReloadAssetAsync(asset.Id)).State);
        var stored = await _f.ReloadLoanAsync(loan.Id);
        Assert.Equal(_f.Clock.UtcNow, stored.ClosedAt);
        Assert.Equal((1, 2), (stored.ReturnedQuantity, stored.EffectiveVersion));
        Assert.Equal("Scratched", stored.ReturnCondition);
        Assert.Equal(LoanEventType.Returned, (await _f.EventsAsync(loan.Id)).Last().Type);
        Assert.Single(await _f.AuditAsync("loan.return", loan.Id));
        Assert.Equal(2, (await _f.OutboxAsync(loan.Id, NotificationKind.Return)).Count);
    }

    [Fact]
    public async Task Partial_pool_return_keeps_loan_open_and_overdue_then_full_return_closes()
    {
        var site = await _f.SiteAsync("LS10");
        var holder = await _f.PersonAsync("ls10.holder", site);
        var pool = await _f.PoolAsync("HS-LS10", site, 5);
        var due = InDays(2);
        var loan = (await _f.CheckoutPoolAsync(pool, holder, 3, due)).Loan;

        var partial = await _f.AsItLending(s => s.ReturnAsync(loan.Id, quantity: 1));

        Assert.False(partial.FullyReturned);
        Assert.Equal(LoanEventType.PartiallyReturned, partial.Event.Type);
        var stored = await _f.ReloadLoanAsync(loan.Id);
        Assert.True(stored.IsOpen);
        Assert.Equal(2, stored.OutstandingQuantity);
        var p = await _f.ReloadPoolAsync(pool.Id);
        Assert.Equal((3, 2), (p.Available, p.Outstanding));

        // Past the due date the remaining units are still overdue: the daily job reminds about this loan.
        _f.Clock.UtcNow = new DateTimeOffset(due.AddDays(2).ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);
        await _f.RunAsAsync(TestUser.Nobody, sp =>
            sp.GetRequiredService<OverdueReminderService>().QueueRemindersAsync(CancellationToken.None));
        var reminders = await _f.OutboxAsync(loan.Id, NotificationKind.OverdueReminder);
        Assert.Equal(2, reminders.Count);

        var full = await _f.AsItLending(s => s.ReturnAsync(loan.Id));
        Assert.True(full.FullyReturned);
        Assert.Equal(2, full.Event.Quantity);
        Assert.False((await _f.ReloadLoanAsync(loan.Id)).IsOpen);
        p = await _f.ReloadPoolAsync(pool.Id);
        Assert.Equal((5, 0), (p.Available, p.Outstanding));
    }

    [Fact]
    public async Task Return_quantity_and_time_are_validated()
    {
        var site = await _f.SiteAsync("LS11");
        var holder = await _f.PersonAsync("ls11.holder", site);
        var pool = await _f.PoolAsync("HS-LS11", site, 5);
        var loan = (await _f.CheckoutPoolAsync(pool, holder, 2)).Loan;

        await Assert.ThrowsAsync<DomainException>(() => _f.AsItLending(s => s.ReturnAsync(loan.Id, quantity: 3)));
        await Assert.ThrowsAsync<DomainException>(() => _f.AsItLending(s => s.ReturnAsync(loan.Id, quantity: 0)));
        await Assert.ThrowsAsync<DomainException>(() =>
            _f.AsItLending(s => s.ReturnAsync(loan.Id, returnedAt: _f.Clock.UtcNow.AddHours(1))));
        await Assert.ThrowsAsync<DomainException>(() =>
            _f.AsItLending(s => s.ReturnAsync(loan.Id, returnedAt: loan.StartedAt.AddMinutes(-1))));
        await Assert.ThrowsAsync<DomainException>(() => _f.AsItLending(s => s.ReturnAsync(loan.Id, markUnavailable: true)));
        Assert.Equal(2, (await _f.ReloadLoanAsync(loan.Id)).OutstandingQuantity);
    }

    [Fact]
    public async Task Return_for_an_inactive_holder_is_allowed_and_only_it_is_mailed()
    {
        var site = await _f.SiteAsync("LS12");
        var holder = await _f.PersonAsync("ls12.holder", site);
        var asset = await _f.AssetAsync("LS-0012", site);
        var loan = (await _f.CheckoutTaggedAsync(asset, holder)).Loan;
        await _f.SetPersonActiveAsync(holder.Id, false);

        var r = await _f.AsItLending(s => s.ReturnAsync(loan.Id));

        Assert.True(r.FullyReturned);
        var rows = await _f.OutboxAsync(loan.Id, NotificationKind.Return);
        Assert.Contains(rows, x => x.Status == NotificationStatus.Skipped && x.RecipientEmail is null);
        Assert.Contains(rows, x => x.Status == NotificationStatus.Pending && x.RecipientEmail == LendingTestSupport.ItMailbox);
    }

    [Fact]
    public async Task Double_return_is_a_conflict_even_when_concurrent()
    {
        var site = await _f.SiteAsync("LS13");
        var holder = await _f.PersonAsync("ls13.holder", site);
        var asset = await _f.AssetAsync("LS-0013", site);
        var loan = (await _f.CheckoutTaggedAsync(asset, holder)).Loan;

        using var start = new ManualResetEventSlim();
        var attempts = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            start.Wait();
            try { await _f.AsItLending(s => s.ReturnAsync(loan.Id)); return true; }
            catch (ConflictException) { return false; }
        })).ToList();
        start.Set();
        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(ok => ok));
        Assert.Single(await _f.EventsAsync(loan.Id), e => e.Type == LoanEventType.Returned);
        await Assert.ThrowsAsync<ConflictException>(() => _f.AsItLending(s => s.ReturnAsync(loan.Id)));

        // A pool partial-return race never returns more than was outstanding.
        var pool = await _f.PoolAsync("HS-LS13", site, 4);
        var pl = (await _f.CheckoutPoolAsync(pool, holder, 3)).Loan;
        var poolAttempts = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            try { await _f.AsItLending(s => s.ReturnAsync(pl.Id, quantity: 1)); return true; }
            catch (DomainException) { return false; }
        })).ToList();
        var poolResults = await Task.WhenAll(poolAttempts);
        Assert.True(poolResults.Count(ok => ok) <= 3);
        var stored = await _f.ReloadLoanAsync(pl.Id);
        var p = await _f.ReloadPoolAsync(pool.Id);
        Assert.Equal(poolResults.Count(ok => ok), stored.ReturnedQuantity);
        Assert.Equal(stored.OutstandingQuantity, p.Outstanding);
        Assert.Equal(4, p.Available + p.Outstanding);
    }

    [Fact]
    public async Task People_search_for_checkout_returns_only_active_people_with_email()
    {
        var site = await _f.SiteAsync("LS14");
        await _f.PersonAsync("ls14.ok", site);
        await _f.PersonAsync("ls14.inactive", site, active: false);
        await _f.PersonAsync("ls14.nomail", site, email: null);
        await _f.PersonAsync("ls14.badmail", site, email: "bad address");

        var eligible = await _f.AsItLending(s => s.SearchPeopleAsync("ls14."));
        Assert.Equal(["ls14.ok"], eligible.Select(p => p.Username));

        var all = await _f.AsItLending(s => s.SearchPeopleAsync("ls14.", eligibleOnly: false));
        Assert.Equal(4, all.Count);
    }
}
