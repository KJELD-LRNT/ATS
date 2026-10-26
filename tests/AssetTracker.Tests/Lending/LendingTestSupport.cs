using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Lending;
using AssetTracker.Tests.Infrastructure;
using AssetTracker.Tests.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Lending;

/// <summary>Shared helpers for lending tests. Fictional data only.</summary>
public static class LendingTestSupport
{
    public const string ItMailbox = "it-lending@example.test";

    public static Task<T> AsItLending<T>(this AppFactory f, Func<LendingService, Task<T>> action) =>
        f.RunAsAsync(TestUser.It, sp => action(sp.GetRequiredService<LendingService>()));

    public static Task<T> AsItCorrection<T>(this AppFactory f, Func<CorrectionService, Task<T>> action) =>
        f.RunAsAsync(TestUser.It, sp => action(sp.GetRequiredService<CorrectionService>()));

    /// <summary>A site with the IT mailbox configured.</summary>
    public static Task<Site> SiteAsync(this AppFactory f, string key) =>
        f.WithDbAsync(async db =>
        {
            await NotificationSeed.SetItMailboxAsync(db, ItMailbox);
            return await Seed.SiteAsync(db, key);
        });

    public static Task<Person> PersonAsync(this AppFactory f, string username, Site site, bool active = true,
        string? email = "default") =>
        f.WithDbAsync(db => Seed.PersonAsync(db, username, site, active, email));

    public static Task<IndividualAsset> AssetAsync(this AppFactory f, string tag, Site site) =>
        f.WithDbAsync(db => Seed.AssetAsync(db, tag, site));

    public static Task<Pool> PoolAsync(this AppFactory f, string sku, Site site, int available) =>
        f.WithDbAsync(db => Seed.PoolAsync(db, sku, site, available));

    public static Task<IndividualAsset> ReloadAssetAsync(this AppFactory f, int id) =>
        f.WithDbAsync(db => db.Assets.AsNoTracking().SingleAsync(a => a.Id == id));

    public static Task<Pool> ReloadPoolAsync(this AppFactory f, int id) =>
        f.WithDbAsync(db => db.Pools.AsNoTracking().SingleAsync(p => p.Id == id));

    public static Task<Loan> ReloadLoanAsync(this AppFactory f, int id) =>
        f.WithDbAsync(db => db.Loans.AsNoTracking().SingleAsync(l => l.Id == id));

    public static Task<List<LoanEvent>> EventsAsync(this AppFactory f, int loanId) =>
        f.WithDbAsync(db => db.LoanEvents.AsNoTracking().Where(e => e.LoanId == loanId).OrderBy(e => e.Id).ToListAsync());

    public static Task<List<NotificationOutbox>> OutboxAsync(this AppFactory f, int loanId, NotificationKind kind) =>
        f.WithDbAsync(db => db.Outbox.AsNoTracking().Where(o => o.LoanId == loanId && o.Kind == kind)
            .OrderBy(o => o.Id).ToListAsync());

    public static Task<List<AuditEvent>> AuditAsync(this AppFactory f, string action, int loanId) =>
        f.WithDbAsync(db => db.AuditEvents.AsNoTracking()
            .Where(a => a.Action == action && a.EntityId == loanId.ToString()).ToListAsync());

    public static Task<CheckoutResult> CheckoutTaggedAsync(this AppFactory f, IndividualAsset asset, Person holder,
        DateOnly? due = null) =>
        f.AsItLending(s => s.CheckoutAsync(new CheckoutRequest(holder.Id, AssetId: asset.Id, DueDate: due)));

    public static Task<CheckoutResult> CheckoutPoolAsync(this AppFactory f, Pool pool, Person holder, int quantity,
        DateOnly? due = null) =>
        f.AsItLending(s => s.CheckoutAsync(new CheckoutRequest(holder.Id, PoolId: pool.Id, Quantity: quantity, DueDate: due)));

    public static async Task SetPersonActiveAsync(this AppFactory f, int personId, bool active) =>
        await f.WithDbAsync(db => db.People.Where(p => p.Id == personId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.IsActive, active)));
}
