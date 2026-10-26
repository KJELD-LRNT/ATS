using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Notifications;
using AssetTracker.Core.Security;
using AssetTracker.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Lending;

/// <summary>One event of a loan's append-only trail with its supersede links resolved.</summary>
/// <param name="Event">The stored event (never modified).</param>
/// <param name="SupersededByEventId">The Corrected event that superseded this one, if any.</param>
/// <param name="Changes">For Corrected events: parsed <see cref="LoanEvent.ChangeJson"/>.</param>
public sealed record LoanHistoryEntry(LoanEvent Event, long? SupersededByEventId, IReadOnlyList<FieldChange> Changes)
{
    public bool IsSuperseded => SupersededByEventId is not null;
}

/// <summary>
/// A loan's effective state (the <see cref="Loan"/> columns, already corrected) plus its full event trail in
/// recording order. <see cref="People"/> resolves every holder id mentioned by the loan, its events and changes.
/// </summary>
public sealed record LoanHistory(Loan Loan, IReadOnlyList<LoanHistoryEntry> Events, IReadOnlyDictionary<int, Person> People)
{
    public int SiteId => Loan.Asset?.SiteId ?? Loan.Pool!.SiteId;
    public bool AnyDirectoryStale => Events.Any(e => e.Event.DirectoryStale);
    public string PersonName(int id) => People.TryGetValue(id, out var p) ? p.DisplayName : $"#{id}";
}

public sealed record LoanSearch(
    int? SiteId = null, bool OverdueOnly = false, string? Query = null, bool IncludeClosed = false,
    int Page = 1, int PageSize = Paging.DefaultPageSize);

/// <summary>
/// Read model for loans. Every query goes through <see cref="AccessScope.VisibleLoansAsync"/>: IT sees all loans;
/// a manager sees tagged-asset loans by the asset's <b>current</b> site (whole history, including holders from
/// before a site move) and pool loans by the pool's site. Effective state is in the <see cref="Loan"/> columns;
/// the original events and their corrections are in the event list.
/// </summary>
public class LoanHistoryService(AppDbContext db, AccessScope access, SiteCalendar calendar)
{
    /// <summary>The loan with its event trail, or <see cref="AccessDeniedException"/> (404) when not visible.</summary>
    public async Task<LoanHistory> GetAsync(int loanId, CancellationToken ct = default)
    {
        var loan = await (await access.VisibleLoansAsync(ct)).AsNoTracking()
            .Include(l => l.Holder)
            .Include(l => l.Asset).ThenInclude(a => a!.Site)
            .Include(l => l.Pool).ThenInclude(p => p!.Site)
            .FirstOrDefaultAsync(l => l.Id == loanId, ct) ?? throw new AccessDeniedException();

        var events = await db.LoanEvents.AsNoTracking().Where(e => e.LoanId == loanId)
            .OrderBy(e => e.Id).ToListAsync(ct);
        var supersededBy = events.Where(e => e.SupersedesEventId is not null)
            .GroupBy(e => e.SupersedesEventId!.Value)
            .ToDictionary(g => g.Key, g => g.Min(e => e.Id));
        var entries = events.Select(e => new LoanHistoryEntry(e,
            supersededBy.TryGetValue(e.Id, out var by) ? by : null,
            e.Type == LoanEventType.Corrected ? CorrectionService.ParseChanges(e.ChangeJson) : [])).ToList();

        var ids = new HashSet<int> { loan.HolderId };
        foreach (var e in entries)
        {
            ids.Add(e.Event.HolderId);
            foreach (var c in e.Changes.Where(c => c.Field == "holderId"))
            {
                if (int.TryParse(c.From, out var f)) ids.Add(f);
                if (int.TryParse(c.To, out var t)) ids.Add(t);
            }
        }
        var idList = ids.ToList();
        var people = await db.People.AsNoTracking().Where(p => idList.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        return new LoanHistory(loan, entries, people);
    }

    /// <summary>Paged loans (open by default), newest first, scoped to what the principal may read.</summary>
    public async Task<PagedResult<Loan>> SearchAsync(LoanSearch s, CancellationToken ct = default)
    {
        var q = (await access.VisibleLoansAsync(ct)).AsNoTracking()
            .Include(l => l.Holder)
            .Include(l => l.Asset).ThenInclude(a => a!.Site)
            .Include(l => l.Pool).ThenInclude(p => p!.Site)
            .AsQueryable();
        if (!s.IncludeClosed) q = q.Where(l => l.ClosedAt == null);
        if (s.SiteId is { } siteId)
            q = q.Where(l => (l.AssetId != null && l.Asset!.SiteId == siteId) || (l.PoolId != null && l.Pool!.SiteId == siteId));
        if (s.OverdueOnly)
            q = q.Where(l => l.ClosedAt == null).Where(await calendar.OverduePredicateAsync(ct)); // per-site day (D4)
        var term = s.Query?.Trim();
        if (!string.IsNullOrEmpty(term))
        {
            var pattern = Paging.ContainsPattern(term);
            q = q.Where(l =>
                EF.Functions.Like(l.HolderNameSnapshot, pattern, @"\") ||
                (l.Holder != null && EF.Functions.Like(l.Holder.DisplayName, pattern, @"\")) ||
                (l.AssetId != null && EF.Functions.Like(l.Asset!.Tag, pattern, @"\")) ||
                (l.PoolId != null && EF.Functions.Like(l.Pool!.Sku, pattern, @"\")));
        }
        return await q.OrderByDescending(l => l.Id).ToPageAsync(s.Page, s.PageSize, ct);
    }

    /// <summary>All loans of a tagged asset (visible by its current site), newest first.</summary>
    public async Task<IReadOnlyList<Loan>> ForAssetAsync(int assetId, CancellationToken ct = default) =>
        await (await access.VisibleLoansAsync(ct)).AsNoTracking().Include(l => l.Holder)
            .Where(l => l.AssetId == assetId).OrderByDescending(l => l.Id).ToListAsync(ct);

    /// <summary>Open loans of a pool (visible by the pool's site), newest first.</summary>
    public async Task<IReadOnlyList<Loan>> OpenForPoolAsync(int poolId, CancellationToken ct = default) =>
        await (await access.VisibleLoansAsync(ct)).AsNoTracking().Include(l => l.Holder)
            .Where(l => l.PoolId == poolId && l.ClosedAt == null).OrderByDescending(l => l.Id).ToListAsync(ct);
}
