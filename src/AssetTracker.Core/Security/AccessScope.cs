using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Security;

/// <summary>What inventory the current principal may read.</summary>
public sealed record SiteAccess(bool AllSites, IReadOnlySet<int> SiteIds)
{
    public static readonly SiteAccess None = new(false, new HashSet<int>());

    public bool CanReadInventory => AllSites || SiteIds.Count > 0;
    public bool CanRead(int siteId) => AllSites || SiteIds.Contains(siteId);
}

/// <summary>
/// Resolves site-level read access. Must be used by every inventory/report/export query path.
/// IT → all sites. Manager → granted sites (grants only count while in the manager group and while the granted
/// person is active in the directory snapshot; sponsor decision D9 keeps the grant row but it then has no effect).
/// Admin role alone → nothing.
/// </summary>
public class AccessScope(AppDbContext db, ICurrentUser user)
{
    private SiteAccess? _cached;

    public async Task<SiteAccess> GetAsync(CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;
        if (user.IsIt) return _cached = new SiteAccess(true, new HashSet<int>());
        if (!user.IsManager || string.IsNullOrEmpty(user.Sid)) return _cached = SiteAccess.None;

        var siteIds = await (
            from g in db.ManagerSiteGrants
            join p in db.People on g.PrincipalDirectoryId equals p.DirectoryId
            where p.Sid == user.Sid && p.IsActive
            select g.SiteId).ToListAsync(ct);
        return _cached = new SiteAccess(false, siteIds.ToHashSet());
    }

    public async Task<IQueryable<IndividualAsset>> VisibleAssetsAsync(CancellationToken ct = default)
    {
        var a = await GetAsync(ct);
        if (a.AllSites) return db.Assets;
        var ids = a.SiteIds.ToList();
        return db.Assets.Where(x => ids.Contains(x.SiteId));
    }

    public async Task<IQueryable<Pool>> VisiblePoolsAsync(CancellationToken ct = default)
    {
        var a = await GetAsync(ct);
        if (a.AllSites) return db.Pools;
        var ids = a.SiteIds.ToList();
        return db.Pools.Where(x => ids.Contains(x.SiteId));
    }

    /// <summary>
    /// Loans visible to the principal: tagged-asset loans by the asset's <b>current</b> site (whole history),
    /// pool loans by the pool's site.
    /// </summary>
    public async Task<IQueryable<Loan>> VisibleLoansAsync(CancellationToken ct = default)
    {
        var a = await GetAsync(ct);
        if (a.AllSites) return db.Loans;
        var ids = a.SiteIds.ToList();
        return db.Loans.Where(l =>
            (l.AssetId != null && ids.Contains(l.Asset!.SiteId)) ||
            (l.PoolId != null && ids.Contains(l.Pool!.SiteId)));
    }

    public async Task EnsureCanReadSiteAsync(int siteId, CancellationToken ct = default)
    {
        if (!(await GetAsync(ct)).CanRead(siteId)) throw new Common.AccessDeniedException();
    }

    /// <summary>Whether the principal may see values of sensitive custom fields (e.g. a SIM PUC): IT only.</summary>
    public bool CanSeeSensitiveFields => user.IsIt;

    public void EnsureIt()
    {
        if (!user.IsIt) throw new Common.AccessDeniedException();
    }

    public void EnsureAdmin()
    {
        if (!user.IsAdmin) throw new Common.AccessDeniedException();
    }
}
