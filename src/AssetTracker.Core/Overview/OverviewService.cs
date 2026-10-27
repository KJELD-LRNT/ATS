using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Overview;

/// <summary>Things on the overview's "Needs attention" tile. Each count links to the list behind it.</summary>
/// <param name="InactiveHolderLoans">Open loans held by someone who is no longer active in the directory.</param>
/// <param name="UnavailableAssets">Assets taken out of circulation (repair, pending retirement).</param>
/// <param name="PoolsBelowMinimum">Pools that have a minimum stock level and are under it.</param>
/// <param name="FailedMails">Notification mails that failed to send (not limited by site).</param>
public sealed record AttentionSummary(int InactiveHolderLoans, int UnavailableAssets, int PoolsBelowMinimum, int FailedMails)
{
    public bool IsEmpty => InactiveHolderLoans + UnavailableAssets + PoolsBelowMinimum + FailedMails == 0;
}

/// <summary>Counts for the overview that no report computes. IT only (it includes mail status); scoped like every other read.</summary>
public class OverviewService(AppDbContext db, AccessScope access)
{
    public async Task<AttentionSummary> GetAttentionAsync(int? siteId = null, CancellationToken ct = default)
    {
        access.EnsureIt();
        var loans = (await access.VisibleLoansAsync(ct)).Where(l => l.ClosedAt == null && l.Holder != null && !l.Holder.IsActive);
        var assets = (await access.VisibleAssetsAsync(ct)).Where(a => a.State == AssetState.Unavailable);
        var pools = (await access.VisiblePoolsAsync(ct)).Where(p => p.MinimumStock != null && p.Available < p.MinimumStock);
        if (siteId is { } site)
        {
            loans = loans.Where(l => (l.AssetId != null && l.Asset!.SiteId == site) || (l.PoolId != null && l.Pool!.SiteId == site));
            assets = assets.Where(a => a.SiteId == site);
            pools = pools.Where(p => p.SiteId == site);
        }
        return new AttentionSummary(
            await loans.CountAsync(ct), await assets.CountAsync(ct), await pools.CountAsync(ct),
            await db.Outbox.CountAsync(o => o.Status == NotificationStatus.Failed, ct));
    }
}
