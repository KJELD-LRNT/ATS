using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Lending;
using AssetTracker.Core.Notifications;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Tests.Lending;

/// <summary>
/// Pins the lending behaviour behind sponsor decisions D4, D10 and D11 (<c>docs/decisions.md</c>).
/// D8 and D9 are pinned in <c>Directory/SponsorDecisionSyncTests</c>.
/// </summary>
public class SponsorDecisionTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public SponsorDecisionTests(AppFactory f) => _f = f;

    /// <summary>D4 / per-site calendar: the due date must not be before today in the loan's site's timezone.</summary>
    [Fact]
    public async Task Checkout_due_date_is_validated_in_the_sites_timezone()
    {
        var previous = _f.Clock.UtcNow;
        _f.Clock.UtcNow = new DateTimeOffset(2026, 3, 2, 12, 0, 0, TimeSpan.Zero);
        try
        {
            var utcSite = await _f.SiteAsync("SD01U");
            var plus14 = await _f.SiteAsync("SD01K", "Pacific/Kiritimati"); // already 3 Mar locally
            var holder = await _f.PersonAsync("sd01.holder", utcSite);
            var today = new DateOnly(2026, 3, 2);

            // Due today at a UTC site is allowed (it is then due today).
            var ok = await _f.CheckoutTaggedAsync(await _f.AssetAsync("SD-01-U", utcSite), holder, today);
            Assert.Equal(today, ok.Loan.DueDate);

            // The same date is already in the past at the UTC+14 site.
            var plus14Asset = await _f.AssetAsync("SD-01-K", plus14);
            var ex = await Assert.ThrowsAsync<DomainException>(() => _f.CheckoutTaggedAsync(plus14Asset, holder, today));
            Assert.Equal("loan.due_past", ex.Code);
            var pool = await _f.PoolAsync("HS-SD01K", plus14, 3);
            ex = await Assert.ThrowsAsync<DomainException>(() => _f.CheckoutPoolAsync(pool, holder, 1, today));
            Assert.Equal("loan.due_past", ex.Code);
            Assert.NotNull(await _f.CheckoutPoolAsync(pool, holder, 1, today.AddDays(1)));
        }
        finally
        {
            _f.Clock.UtcNow = previous;
        }
    }

    /// <summary>D10: return mail to an inactive holder is skipped with a reason and IT is notified.</summary>
    [Fact]
    public async Task D10_return_and_correction_mail_to_an_inactive_holder_is_skipped_and_it_is_notified()
    {
        var site = await _f.SiteAsync("SD02");
        var holder = await _f.PersonAsync("sd02.holder", site);
        var pool = await _f.PoolAsync("HS-SD02", site, 5);
        var loanId = (await _f.CheckoutPoolAsync(pool, holder, 2)).Loan.Id;
        await _f.SetPersonActiveAsync(holder.Id, false);

        await _f.AsItLending(s => s.ReturnAsync(loanId, quantity: 1));
        await _f.AsItCorrection(s => s.CommitAsync(loanId,
            new CorrectionRequest { DueDate = _f.Clock.UtcToday.AddDays(10), Reason = "Extended" }));

        foreach (var kind in new[] { NotificationKind.Return, NotificationKind.Correction })
        {
            var rows = await _f.OutboxAsync(loanId, kind);
            Assert.Equal(2, rows.Count);
            var skipped = Assert.Single(rows, r => r.Status == NotificationStatus.Skipped);
            Assert.Null(skipped.RecipientEmail);
            Assert.Equal("holder is inactive in the directory", skipped.SkipReason);
            var it = Assert.Single(rows, r => r.Status == NotificationStatus.Pending);
            Assert.Equal(LendingTestSupport.ItMailbox, it.RecipientEmail);
            Assert.Contains("sd02.holder was not emailed: holder is inactive in the directory", it.Body);
        }
    }

    /// <summary>D11: a correction supersedes the loan's latest event (a return, or an earlier correction).</summary>
    [Fact]
    public async Task D11_correction_supersedes_the_loans_latest_event()
    {
        var site = await _f.SiteAsync("SD03");
        var holder = await _f.PersonAsync("sd03.holder", site);
        var asset = await _f.AssetAsync("SD-0003", site);
        var checkout = await _f.CheckoutTaggedAsync(asset, holder);
        var returned = await _f.AsItLending(s => s.ReturnAsync(checkout.Loan.Id, returnedAt: _f.Clock.UtcNow));

        var first = await _f.AsItCorrection(s => s.CommitAsync(checkout.Loan.Id,
            new CorrectionRequest { DueDate = _f.Clock.UtcToday.AddDays(5), Reason = "Due date was agreed" }));
        var second = await _f.AsItCorrection(s => s.CommitAsync(checkout.Loan.Id,
            new CorrectionRequest { DueDate = _f.Clock.UtcToday.AddDays(6), Reason = "Typo in the first correction" }));

        Assert.Equal(returned.Event.Id, first.Event.SupersedesEventId);
        Assert.Equal(first.Event.Id, second.Event.SupersedesEventId);
        var events = await _f.EventsAsync(checkout.Loan.Id);
        Assert.Equal([LoanEventType.CheckedOut, LoanEventType.Returned, LoanEventType.Corrected, LoanEventType.Corrected],
            events.Select(e => e.Type));
        Assert.Null(events[0].SupersedesEventId);
        Assert.Equal(_f.Clock.UtcToday.AddDays(6), (await _f.ReloadLoanAsync(checkout.Loan.Id)).DueDate);
        Assert.Equal(4, await _f.WithDbAsync(db => db.LoanEvents.CountAsync(e => e.LoanId == checkout.Loan.Id)));
    }
}
