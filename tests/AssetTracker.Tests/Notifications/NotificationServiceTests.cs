using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Notifications;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Notifications;

public class NotificationServiceTests : IClassFixture<AppFactory>
{
    private const string It = "it-desk@example.test";
    private readonly AppFactory _f;
    public NotificationServiceTests(AppFactory f) => _f = f;

    private Task<T> InScope<T>(Func<AppDbContext, NotificationService, Task<T>> action) =>
        _f.RunAsAsync(TestUser.It, sp =>
            action(sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<NotificationService>()));

    [Fact]
    public async Task Checkout_stages_holder_and_it_rows_linked_to_unsaved_loan_and_event()
    {
        var (loanId, rows) = await InScope(async (db, svc) =>
        {
            await NotificationSeed.SetItMailboxAsync(db, It);
            var site = await Seed.SiteAsync(db, "NS1");
            var holder = await Seed.PersonAsync(db, "ns.alice", site);
            var asset = await Seed.AssetAsync(db, "NS-LT-001", site);
            var (loan, evt) = NotificationSeed.NewTaggedLoan(asset, holder, new DateOnly(2026, 4, 1));
            db.Loans.Add(loan);

            var staged = await svc.QueueLoanNotificationAsync(loan, evt, NotificationKind.Checkout, [holder]);
            Assert.Equal(2, staged.Count);
            await db.SaveChangesAsync();
            return (loan.Id, await db.Outbox.AsNoTracking().Where(o => o.LoanId == loan.Id).ToListAsync());
        });

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(NotificationStatus.Pending, r.Status);
            Assert.Equal(loanId, r.LoanId);
            Assert.NotNull(r.LoanEventId);
            Assert.StartsWith("checkout:", r.DedupeKey);
            Assert.Contains("NS-LT-001", r.Subject);
            Assert.Contains("Site NS1", r.Body);
            Assert.Contains("2026-04-01", r.Body);
            Assert.Contains("ns.alice", r.Body);
        });
        Assert.Equal(new[] { It, "ns.alice@example.test" }, rows.Select(r => r.RecipientEmail).Order());
        Assert.Contains("checked out to you", rows.Single(r => r.RecipientEmail != It).Body);
    }

    [Fact]
    public async Task Holder_whose_address_is_the_it_mailbox_gets_one_row()
    {
        var rows = await InScope(async (db, svc) =>
        {
            await NotificationSeed.SetItMailboxAsync(db, "Helpdesk@Example.test");
            var site = await Seed.SiteAsync(db, "NS2");
            var holder = await Seed.PersonAsync(db, "ns.helpdesk", site, email: "helpdesk@EXAMPLE.TEST");
            var asset = await Seed.AssetAsync(db, "NS-LT-002", site);
            var (loan, evt) = NotificationSeed.NewTaggedLoan(asset, holder, null);
            db.Loans.Add(loan);
            var staged = await svc.QueueLoanNotificationAsync(loan, evt, NotificationKind.Checkout, [holder, holder]);
            await db.SaveChangesAsync();
            return staged;
        });

        var row = Assert.Single(rows);
        Assert.Equal("Helpdesk@Example.test", row.RecipientEmail);
        Assert.Contains("Due date:", row.Body);
        Assert.Contains("none", row.Body);
    }

    [Theory]
    [InlineData(null, true, "no email address")]
    [InlineData("not-an-address", true, "invalid")]
    [InlineData("former@example.test", false, "inactive")]
    public async Task Unmailable_holder_is_recorded_as_skipped_and_it_is_still_notified(string? email, bool active, string reasonPart)
    {
        var rows = await InScope(async (db, svc) =>
        {
            await NotificationSeed.SetItMailboxAsync(db, It);
            var site = await Seed.SiteAsync(db, "NS3" + reasonPart[..3]);
            var holder = await Seed.PersonAsync(db, "ns.skip." + reasonPart[..3], site, active: active, email: email);
            var loan = await NotificationSeed.TaggedLoanAsync(db, site, holder, "NS-SK-" + reasonPart[..3], null);
            var evt = new LoanEvent
            {
                LoanId = loan.Id, Type = LoanEventType.Returned, OccurredAt = loan.StartedAt, RecordedAt = loan.StartedAt,
                Actor = "test", HolderId = holder.Id, Quantity = 1,
            };
            loan.ReturnedQuantity = 1;
            loan.ClosedAt = loan.StartedAt;
            db.LoanEvents.Add(evt);
            var staged = await svc.QueueLoanNotificationAsync(loan, evt, NotificationKind.Return, [holder]);
            await db.SaveChangesAsync();
            return staged;
        });

        Assert.Equal(2, rows.Count);
        var skipped = rows.Single(r => r.Status == NotificationStatus.Skipped);
        Assert.Null(skipped.RecipientEmail);
        Assert.Contains(reasonPart, skipped.SkipReason);
        Assert.NotNull(skipped.LoanEventId);
        var it = rows.Single(r => r.Status == NotificationStatus.Pending);
        Assert.Equal(It, it.RecipientEmail);
        Assert.Contains("Note for IT:", it.Body);
        Assert.Contains(reasonPart, it.Body);
        Assert.StartsWith("Returned:", it.Subject);
    }

    [Fact]
    public async Task Missing_it_mailbox_is_recorded_as_skipped_row()
    {
        var rows = await InScope(async (db, svc) =>
        {
            await NotificationSeed.SetItMailboxAsync(db, null);
            var site = await Seed.SiteAsync(db, "NS4");
            var holder = await Seed.PersonAsync(db, "ns.bob", site);
            var asset = await Seed.AssetAsync(db, "NS-LT-004", site);
            var (loan, evt) = NotificationSeed.NewTaggedLoan(asset, holder, null);
            db.Loans.Add(loan);
            var staged = await svc.QueueLoanNotificationAsync(loan, evt, NotificationKind.Checkout, [holder]);
            await db.SaveChangesAsync();
            return staged;
        });

        Assert.Equal(2, rows.Count);
        Assert.Equal("ns.bob@example.test", rows.Single(r => r.Status == NotificationStatus.Pending).RecipientEmail);
        var it = rows.Single(r => r.Status == NotificationStatus.Skipped);
        Assert.Equal(NotificationService.ItMailboxNotConfigured, it.SkipReason);
        Assert.EndsWith(":it", it.DedupeKey);
    }

    [Fact]
    public async Task Correction_notifies_previous_and_corrected_holder_and_it()
    {
        var rows = await InScope(async (db, svc) =>
        {
            await NotificationSeed.SetItMailboxAsync(db, It);
            var site = await Seed.SiteAsync(db, "NS5");
            var oldHolder = await Seed.PersonAsync(db, "ns.old", site);
            var newHolder = await Seed.PersonAsync(db, "ns.new", site);
            var loan = await NotificationSeed.TaggedLoanAsync(db, site, oldHolder, "NS-LT-005", null);
            var original = loan.Events.Single();

            loan.HolderId = newHolder.Id;
            loan.HolderNameSnapshot = newHolder.DisplayName;
            loan.EffectiveVersion++;
            var correction = new LoanEvent
            {
                LoanId = loan.Id, Type = LoanEventType.Corrected, OccurredAt = loan.StartedAt, RecordedAt = loan.StartedAt,
                Actor = "test", HolderId = newHolder.Id, Quantity = 1, Reason = "Wrong person selected",
                SupersedesEventId = original.Id,
            };
            db.LoanEvents.Add(correction);
            var staged = await svc.QueueLoanNotificationAsync(loan, correction, NotificationKind.Correction,
                [oldHolder, newHolder]);
            await db.SaveChangesAsync();
            Assert.All(staged, r => Assert.Equal(correction.Id, r.LoanEventId));
            return staged;
        });

        Assert.Equal(new[] { It, "ns.new@example.test", "ns.old@example.test" }, rows.Select(r => r.RecipientEmail).Order());
        Assert.All(rows, r =>
        {
            Assert.StartsWith("correction:", r.DedupeKey);
            Assert.Contains("Wrong person selected", r.Body);
            Assert.Contains("Previous holder:", r.Body);
            Assert.Contains("ns.old", r.Body);
        });
    }

    [Fact]
    public async Task Partial_pool_return_mentions_returned_and_outstanding_quantities()
    {
        var rows = await InScope(async (db, svc) =>
        {
            await NotificationSeed.SetItMailboxAsync(db, It);
            var site = await Seed.SiteAsync(db, "NS6");
            var holder = await Seed.PersonAsync(db, "ns.carol", site);
            var loan = await NotificationSeed.PoolLoanAsync(db, site, holder, "NS-HS-6", quantity: 5, returned: 0, due: null);
            loan.ReturnedQuantity = 2;
            var evt = new LoanEvent
            {
                LoanId = loan.Id, Type = LoanEventType.PartiallyReturned, OccurredAt = loan.StartedAt,
                RecordedAt = loan.StartedAt, Actor = "test", HolderId = holder.Id, Quantity = 2,
            };
            db.LoanEvents.Add(evt);
            var staged = await svc.QueueLoanNotificationAsync(loan, evt, NotificationKind.Return, [holder]);
            await db.SaveChangesAsync();
            return staged;
        });

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.StartsWith("Partial return recorded: 5 x NS-HS-6", r.Subject);
            Assert.Matches(@"Returned now:\s+2\r?\n", r.Body);
            Assert.Matches(@"Still outstanding:\s+3\r?\n", r.Body);
        });
    }

    [Fact]
    public async Task Queueing_twice_for_the_same_event_in_one_unit_of_work_does_not_duplicate()
    {
        var count = await InScope(async (db, svc) =>
        {
            await NotificationSeed.SetItMailboxAsync(db, It);
            var site = await Seed.SiteAsync(db, "NS7");
            var holder = await Seed.PersonAsync(db, "ns.dave", site);
            var asset = await Seed.AssetAsync(db, "NS-LT-007", site);
            var (loan, evt) = NotificationSeed.NewTaggedLoan(asset, holder, null);
            db.Loans.Add(loan);
            var first = await svc.QueueLoanNotificationAsync(loan, evt, NotificationKind.Checkout, [holder]);
            var second = await svc.QueueLoanNotificationAsync(loan, evt, NotificationKind.Checkout, [holder]);
            Assert.Equal(first, second);
            await db.SaveChangesAsync();
            return await db.Outbox.CountAsync(o => o.LoanId == loan.Id);
        });
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Phone_number_is_not_included_by_default()
    {
        var rows = await InScope(async (db, svc) =>
        {
            await NotificationSeed.SetItMailboxAsync(db, It);
            var site = await Seed.SiteAsync(db, "NS8");
            var holder = await Seed.PersonAsync(db, "ns.erin", site);
            var loan = await NotificationSeed.TaggedLoanAsync(db, site, holder, "NS-PH-008", null, AssetType.Phone, "+31209990008");
            var staged = await svc.QueueLoanNotificationAsync(loan, loan.Events.Single(), NotificationKind.Checkout, [holder]);
            await db.SaveChangesAsync();
            return staged;
        });
        Assert.All(rows, r => Assert.DoesNotContain("+31209990008", r.Body));
        Assert.All(rows, r => Assert.Contains("NS-PH-008", r.Body));
    }

    [Fact]
    public void Phone_number_is_included_when_enabled()
    {
        var asset = new IndividualAsset { Tag = "PH-1", Type = AssetType.Phone, PhoneNumber = "+31200000001" };
        var loan = new Loan { Quantity = 1, HolderNameSnapshot = "x" };
        Assert.Equal("+31200000001", LoanMailFacts.From(loan, asset, null, null, includePhoneNumber: true).PhoneNumber);
        Assert.Null(LoanMailFacts.From(loan, asset, null, null, includePhoneNumber: false).PhoneNumber);
    }

    [Theory]
    [InlineData("a.b@corp.example", true)]
    [InlineData("  a.b@corp.example ", true)]
    [InlineData("A.B@Corp.Example", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("no-at-sign", false)]
    [InlineData("user@localhost", false)]
    [InlineData("Name <a@corp.example>", false)]
    [InlineData("a@corp.example; b@corp.example", false)]
    [InlineData("a b@corp.example", false)]
    [InlineData("a@corp..example", false)]
    public void Email_validation(string? address, bool valid) => Assert.Equal(valid, EmailAddresses.IsValid(address));
}
