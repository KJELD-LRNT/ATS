using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Lending;
using AssetTracker.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Lending;

/// <summary>Loan corrections via superseding events (WP2.2). The clock only moves forward in this class.</summary>
public class CorrectionServiceTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public CorrectionServiceTests(AppFactory f) => _f = f;

    [Fact]
    public async Task Holder_correction_appends_superseding_event_and_notifies_old_new_and_it()
    {
        var site = await _f.SiteAsync("CS01");
        var wrong = await _f.PersonAsync("cs01.wrong", site);
        var right = await _f.PersonAsync("cs01.right", site);
        var asset = await _f.AssetAsync("CS-0001", site);
        var checkout = await _f.CheckoutTaggedAsync(asset, wrong);
        var loanId = checkout.Loan.Id;

        var result = await _f.AsItCorrection(s => s.CommitAsync(loanId,
            new CorrectionRequest { HolderId = right.Id, Reason = "Handed to the wrong colleague in the system" }));

        var loan = await _f.ReloadLoanAsync(loanId);
        Assert.Equal(right.Id, loan.HolderId);
        Assert.Equal("cs01.right", loan.HolderNameSnapshot);
        Assert.Equal(2, loan.EffectiveVersion);

        var events = await _f.EventsAsync(loanId);
        Assert.Equal(2, events.Count);
        var original = events[0];
        Assert.Equal(LoanEventType.CheckedOut, original.Type);
        Assert.Equal(wrong.Id, original.HolderId);
        var correction = events[1];
        Assert.Equal(LoanEventType.Corrected, correction.Type);
        Assert.Equal(original.Id, correction.SupersedesEventId);
        Assert.Equal(right.Id, correction.HolderId);
        Assert.Equal("Handed to the wrong colleague in the system", correction.Reason);
        var change = Assert.Single(CorrectionService.ParseChanges(correction.ChangeJson));
        Assert.Equal(new FieldChange("holderId", wrong.Id.ToString(), right.Id.ToString()), change);

        var audit = Assert.Single(await _f.AuditAsync("loan.correct", loanId));
        Assert.Equal("Handed to the wrong colleague in the system", audit.Reason);

        var rows = await _f.OutboxAsync(loanId, NotificationKind.Correction);
        Assert.Equal(
            new[] { "cs01.right@example.test", "cs01.wrong@example.test", LendingTestSupport.ItMailbox }.Order(),
            rows.Select(r => r.RecipientEmail!).Order());
        Assert.All(rows, r => Assert.Equal(correction.Id, r.LoanEventId));

        // The read model exposes the original and the link to its correction.
        var history = await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<LoanHistoryService>().GetAsync(loanId));
        Assert.Equal(correction.Id, history.Events[0].SupersededByEventId);
        Assert.Equal("cs01.wrong", history.PersonName(history.Events[0].Event.HolderId));
        Assert.False(history.Events[1].IsSuperseded);
    }

    /// <summary>Sponsor decision (docs/decisions.md): due-date-only corrections also notify holder and IT.</summary>
    [Fact]
    public async Task Correction_for_the_same_holder_notifies_holder_once_plus_it()
    {
        var site = await _f.SiteAsync("CS02");
        var holder = await _f.PersonAsync("cs02.holder", site);
        var asset = await _f.AssetAsync("CS-0002", site);
        var loanId = (await _f.CheckoutTaggedAsync(asset, holder)).Loan.Id;

        await _f.AsItCorrection(s => s.CommitAsync(loanId,
            new CorrectionRequest { DueDate = _f.Clock.LocalToday.AddDays(30), Reason = "Agreed longer loan" }));

        var rows = await _f.OutboxAsync(loanId, NotificationKind.Correction);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task Correction_to_an_inactive_holder_without_email_is_allowed_and_recorded_as_skipped()
    {
        var site = await _f.SiteAsync("CS03");
        var holder = await _f.PersonAsync("cs03.holder", site);
        var leaver = await _f.PersonAsync("cs03.leaver", site, active: false, email: null);
        var pool = await _f.PoolAsync("HS-CS03", site, 3);
        var loanId = (await _f.CheckoutPoolAsync(pool, holder, 1)).Loan.Id;

        await _f.AsItCorrection(s => s.CommitAsync(loanId, new CorrectionRequest { HolderId = leaver.Id, Reason = "Actual borrower" }));

        Assert.Equal(leaver.Id, (await _f.ReloadLoanAsync(loanId)).HolderId);
        var rows = await _f.OutboxAsync(loanId, NotificationKind.Correction);
        Assert.Contains(rows, r => r.Status == NotificationStatus.Skipped);
        Assert.Contains(rows, r => r.RecipientEmail == LendingTestSupport.ItMailbox);
        Assert.Contains(rows, r => r.RecipientEmail == "cs03.holder@example.test");
    }

    [Fact]
    public async Task Correction_requires_a_reason_and_a_real_change()
    {
        var site = await _f.SiteAsync("CS04");
        var holder = await _f.PersonAsync("cs04.holder", site);
        var asset = await _f.AssetAsync("CS-0004", site);
        var loanId = (await _f.CheckoutTaggedAsync(asset, holder)).Loan.Id;

        var noReason = await Assert.ThrowsAsync<DomainException>(() => _f.AsItCorrection(s =>
            s.CommitAsync(loanId, new CorrectionRequest { DueDate = _f.Clock.LocalToday.AddDays(3), Reason = "  " })));
        Assert.Equal("correction.reason_required", noReason.Code);

        var noChange = await Assert.ThrowsAsync<DomainException>(() => _f.AsItCorrection(s =>
            s.CommitAsync(loanId, new CorrectionRequest { HolderId = holder.Id, Reason = "Nothing" })));
        Assert.Equal("correction.no_change", noChange.Code);

        Assert.Single(await _f.EventsAsync(loanId));
        Assert.Equal(1, (await _f.ReloadLoanAsync(loanId)).EffectiveVersion);
    }

    [Fact]
    public async Task Correction_that_overlaps_a_later_loan_is_rejected()
    {
        var site = await _f.SiteAsync("CS05");
        var first = await _f.PersonAsync("cs05.first", site);
        var second = await _f.PersonAsync("cs05.second", site);
        var asset = await _f.AssetAsync("CS-0005", site);

        var loan1 = (await _f.CheckoutTaggedAsync(asset, first)).Loan;
        _f.Clock.Advance(TimeSpan.FromDays(1));
        await _f.AsItLending(s => s.ReturnAsync(loan1.Id));
        _f.Clock.Advance(TimeSpan.FromDays(1));
        var loan2 = (await _f.CheckoutTaggedAsync(asset, second)).Loan;
        _f.Clock.Advance(TimeSpan.FromDays(1));

        // Moving loan 1's return after loan 2 started would overlap.
        var lateReturn = new CorrectionRequest { ReturnedAt = _f.Clock.UtcNow.AddHours(-1), Reason = "Returned later" };
        var preview = await _f.AsItCorrection(s => s.PreviewAsync(loan1.Id, lateReturn));
        Assert.True(preview.HasConflicts);
        Assert.Contains(preview.Conflicts, c => c.Contains($"#{loan2.Id}"));
        var ex = await Assert.ThrowsAsync<ConflictException>(() => _f.AsItCorrection(s => s.CommitAsync(loan1.Id, lateReturn)));
        Assert.Contains($"#{loan2.Id}", ex.Message);

        // Reopening loan 1 while loan 2 is open is an overlap too.
        var reopen = await _f.AsItCorrection(s => s.PreviewAsync(loan1.Id, new CorrectionRequest { Reopen = true }));
        Assert.True(reopen.HasConflicts);
        await Assert.ThrowsAsync<ConflictException>(() =>
            _f.AsItCorrection(s => s.CommitAsync(loan1.Id, new CorrectionRequest { Reopen = true, Reason = "Not returned" })));

        // Moving loan 2's start back into loan 1 overlaps as well.
        await Assert.ThrowsAsync<ConflictException>(() => _f.AsItCorrection(s => s.CommitAsync(loan2.Id,
            new CorrectionRequest { StartedAt = loan1.StartedAt.AddHours(1), Reason = "Earlier" })));

        // Nothing changed.
        Assert.Single(await _f.EventsAsync(loan2.Id));
        Assert.Equal(2, (await _f.EventsAsync(loan1.Id)).Count);
        Assert.Equal(AssetState.CheckedOut, (await _f.ReloadAssetAsync(asset.Id)).State);

        // A non-overlapping correction of the same loan is accepted.
        var earlier = new CorrectionRequest { ReturnedAt = loan1.StartedAt.AddHours(2), Reason = "Returned same day" };
        var ok = await _f.AsItCorrection(s => s.CommitAsync(loan1.Id, earlier));
        Assert.Equal(loan1.StartedAt.AddHours(2), (await _f.ReloadLoanAsync(loan1.Id)).ClosedAt);
        Assert.Equal(LoanEventType.Returned, (await _f.EventsAsync(loan1.Id)).Single(e => e.Id == ok.Event.SupersedesEventId).Type);
    }

    [Fact]
    public async Task Correcting_an_open_tagged_loan_to_returned_frees_the_asset_and_reopen_takes_it_back()
    {
        var site = await _f.SiteAsync("CS06");
        var holder = await _f.PersonAsync("cs06.holder", site);
        var asset = await _f.AssetAsync("CS-0006", site);
        var loan = (await _f.CheckoutTaggedAsync(asset, holder)).Loan;
        _f.Clock.Advance(TimeSpan.FromHours(3));

        var close = await _f.AsItCorrection(s => s.CommitAsync(loan.Id,
            new CorrectionRequest { ReturnedAt = loan.StartedAt.AddHours(1), Reason = "Return was not recorded" }));
        Assert.Equal((AssetState.CheckedOut, AssetState.Available), (close.Effects.AssetStateBefore, close.Effects.AssetStateAfter));
        Assert.Equal(AssetState.Available, (await _f.ReloadAssetAsync(asset.Id)).State);
        Assert.False((await _f.ReloadLoanAsync(loan.Id)).IsOpen);

        await _f.AsItCorrection(s => s.CommitAsync(loan.Id, new CorrectionRequest { Reopen = true, Reason = "Wrong item returned" }));
        Assert.Equal(AssetState.CheckedOut, (await _f.ReloadAssetAsync(asset.Id)).State);
        var reloaded = await _f.ReloadLoanAsync(loan.Id);
        Assert.True(reloaded.IsOpen);
        Assert.Equal(0, reloaded.ReturnedQuantity);
        Assert.Equal(3, reloaded.EffectiveVersion);
    }

    [Fact]
    public async Task Return_time_cannot_be_before_start_or_in_the_future()
    {
        var site = await _f.SiteAsync("CS07");
        var holder = await _f.PersonAsync("cs07.holder", site);
        var asset = await _f.AssetAsync("CS-0007", site);
        var loan = (await _f.CheckoutTaggedAsync(asset, holder)).Loan;

        var before = await _f.AsItCorrection(s => s.PreviewAsync(loan.Id,
            new CorrectionRequest { ReturnedAt = loan.StartedAt.AddMinutes(-5) }));
        Assert.True(before.HasConflicts);
        var future = await _f.AsItCorrection(s => s.PreviewAsync(loan.Id,
            new CorrectionRequest { StartedAt = _f.Clock.UtcNow.AddDays(1) }));
        Assert.True(future.HasConflicts);
    }

    [Fact]
    public async Task Pool_quantity_correction_adjusts_balances_and_rejects_negative_available()
    {
        var site = await _f.SiteAsync("CS08");
        var holder = await _f.PersonAsync("cs08.holder", site);
        var pool = await _f.PoolAsync("HS-CS08", site, 6);
        var loanId = (await _f.CheckoutPoolAsync(pool, holder, 4)).Loan.Id;   // available 2, outstanding 4

        var preview = await _f.AsItCorrection(s => s.PreviewAsync(loanId, new CorrectionRequest { Quantity = 5 }));
        Assert.False(preview.HasConflicts);
        Assert.Equal((2, 1, 4, 5), (preview.PoolAvailableBefore!.Value, preview.PoolAvailableAfter!.Value,
            preview.PoolOutstandingBefore!.Value, preview.PoolOutstandingAfter!.Value));
        Assert.Equal(2, (await _f.ReloadPoolAsync(pool.Id)).Available); // preview saves nothing

        await _f.AsItCorrection(s => s.CommitAsync(loanId, new CorrectionRequest { Quantity = 5, Reason = "Took five" }));
        var p = await _f.ReloadPoolAsync(pool.Id);
        Assert.Equal((1, 5), (p.Available, p.Outstanding));

        var tooMany = await Assert.ThrowsAsync<ConflictException>(() =>
            _f.AsItCorrection(s => s.CommitAsync(loanId, new CorrectionRequest { Quantity = 8, Reason = "Took eight" })));
        Assert.Contains("Only 1", tooMany.Message);
        await Assert.ThrowsAsync<ConflictException>(() =>
            _f.AsItCorrection(s => s.CommitAsync(loanId, new CorrectionRequest { ReturnedQuantity = 6, Reason = "Bad" })));
        await Assert.ThrowsAsync<ConflictException>(() =>
            _f.AsItCorrection(s => s.CommitAsync(loanId, new CorrectionRequest { ReturnedQuantity = -1, Reason = "Bad" })));
        p = await _f.ReloadPoolAsync(pool.Id);
        Assert.Equal((1, 5), (p.Available, p.Outstanding));

        // Correcting the returned quantity to everything closes the loan and returns the stock.
        _f.Clock.Advance(TimeSpan.FromHours(1));
        await _f.AsItCorrection(s => s.CommitAsync(loanId, new CorrectionRequest { ReturnedQuantity = 5, Reason = "All back" }));
        var loan = await _f.ReloadLoanAsync(loanId);
        Assert.False(loan.IsOpen);
        p = await _f.ReloadPoolAsync(pool.Id);
        Assert.Equal((6, 0), (p.Available, p.Outstanding));

        // And reducing it reopens the loan.
        await _f.AsItCorrection(s => s.CommitAsync(loanId, new CorrectionRequest { ReturnedQuantity = 3, Reason = "Two still out" }));
        loan = await _f.ReloadLoanAsync(loanId);
        Assert.True(loan.IsOpen);
        Assert.Null(loan.ClosedAt);
        p = await _f.ReloadPoolAsync(pool.Id);
        Assert.Equal((4, 2), (p.Available, p.Outstanding));

        // Each correction superseded the previous latest event: a linear chain.
        var events = await _f.EventsAsync(loanId);
        var corrections = events.Where(e => e.Type == LoanEventType.Corrected).ToList();
        Assert.Equal(3, corrections.Count);
        for (var i = 1; i < corrections.Count; i++) Assert.Equal(corrections[i - 1].Id, corrections[i].SupersedesEventId);
    }

    [Fact]
    public async Task Stale_effective_version_is_a_conflict_for_non_it_users_access_is_denied()
    {
        var site = await _f.SiteAsync("CS09");
        var holder = await _f.PersonAsync("cs09.holder", site);
        var asset = await _f.AssetAsync("CS-0009", site);
        var loanId = (await _f.CheckoutTaggedAsync(asset, holder)).Loan.Id;

        await Assert.ThrowsAsync<AccessDeniedException>(() => _f.RunAsAsync(TestUser.Manager, sp =>
            sp.GetRequiredService<CorrectionService>().PreviewAsync(loanId, new CorrectionRequest { DueDate = _f.Clock.LocalToday })));
        await Assert.ThrowsAsync<AccessDeniedException>(() => _f.RunAsAsync(TestUser.Admin, sp =>
            sp.GetRequiredService<CorrectionService>().CommitAsync(loanId,
                new CorrectionRequest { DueDate = _f.Clock.LocalToday, Reason = "x" })));

        // Two concurrent corrections: at most one commits; the other sees a changed version.
        var attempts = Enumerable.Range(1, 4).Select(i => Task.Run(async () =>
        {
            try
            {
                await _f.AsItCorrection(s => s.CommitAsync(loanId,
                    new CorrectionRequest { DueDate = _f.Clock.LocalToday.AddDays(i), Reason = $"Attempt {i}" }));
                return true;
            }
            catch (ConflictException) { return false; }
        })).ToList();
        var results = await Task.WhenAll(attempts);
        var committed = results.Count(ok => ok);
        Assert.InRange(committed, 1, 4);
        var loan = await _f.ReloadLoanAsync(loanId);
        Assert.Equal(1 + committed, loan.EffectiveVersion);
        Assert.Equal(committed, (await _f.EventsAsync(loanId)).Count(e => e.Type == LoanEventType.Corrected));
    }
}
