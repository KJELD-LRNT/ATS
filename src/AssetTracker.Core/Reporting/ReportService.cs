using System.Linq.Expressions;
using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Auditing;
using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Notifications;
using AssetTracker.Core.Security;
using AssetTracker.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Reporting;

/// <summary>
/// Read-only reports and their CSV exports: inventory (tagged assets + pool balances), current assignments,
/// overdue assignments and loan history (loan level, plus an event level export that includes every
/// superseding correction).
/// <para>
/// <b>Access.</b> IT or manager only; any other principal (e.g. admin role alone) gets
/// <see cref="AccessDeniedException"/>. Every query starts from <see cref="AccessScope"/>
/// (<c>VisibleAssetsAsync</c>/<c>VisiblePoolsAsync</c>/<c>VisibleLoansAsync</c>), so managers only see granted
/// sites — tagged-asset loans by the asset's <i>current</i> site (whole history, including prior holders' full
/// snapshot, an intentional plan decision), pool loans by the pool's site. Filters only narrow that set.
/// </para>
/// <para>
/// <b>Exports</b> are unpaged (the 500–5,000 people scale fits in memory) but identically scoped, and each one
/// records an <c>export.*</c> audit event with the filters and row count. Exports are reads, so the audit row is
/// the only change and is saved by its own <c>SaveChangesAsync</c> before the file is returned.
/// </para>
/// </summary>
public class ReportService(AppDbContext db, AccessScope access, AuditWriter audit, ICurrentUser user, SiteCalendar calendar)
{
    public const string ExportInventoryAction = "export.inventory";
    public const string ExportAssignmentsAction = "export.assignments";
    public const string ExportOverdueAction = "export.overdue";
    public const string ExportHistoryAction = "export.history";

    // ---------------------------------------------------------------- inventory

    public async Task<InventorySummary> GetSummaryAsync(CancellationToken ct = default)
    {
        EnsureReader();
        var sites = await db.Sites.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Key, ct);
        string Key(int id) => sites.GetValueOrDefault(id, "?");

        var assetCounts = await (await access.VisibleAssetsAsync(ct))
            .GroupBy(a => new { a.SiteId, a.Type, a.State })
            .Select(g => new { g.Key.SiteId, g.Key.Type, g.Key.State, Count = g.Count() })
            .ToListAsync(ct);
        var poolTotals = await (await access.VisiblePoolsAsync(ct))
            .GroupBy(p => new { p.SiteId, p.Type })
            .Select(g => new
            {
                g.Key.SiteId, g.Key.Type, Pools = g.Count(),
                Available = g.Sum(p => p.Available), Outstanding = g.Sum(p => p.Outstanding),
            })
            .ToListAsync(ct);

        var open = (await access.VisibleLoansAsync(ct)).Where(l => l.ClosedAt == null);
        var openLoans = await open.CountAsync(ct);
        var openPoolUnits = await open.Where(l => l.PoolId != null).SumAsync(l => l.Quantity - l.ReturnedQuantity, ct);
        var overdue = await open.Where(await calendar.OverduePredicateAsync(ct)).CountAsync(ct);

        return new InventorySummary(
            assetCounts.Select(x => new AssetCount(Key(x.SiteId), x.Type, x.State, x.Count))
                .OrderBy(x => x.SiteKey).ThenBy(x => x.Type).ThenBy(x => x.State).ToList(),
            poolTotals.Select(x => new PoolTotal(Key(x.SiteId), x.Type, x.Pools, x.Available, x.Outstanding))
                .OrderBy(x => x.SiteKey).ThenBy(x => x.Type).ToList(),
            openLoans, openPoolUnits, overdue);
    }

    public async Task<PagedResult<InventoryAssetRow>> InventoryAssetsAsync(ReportFilter f, CancellationToken ct = default)
    {
        EnsureReader();
        return await (await AssetQueryAsync(f, ct)).ToPageAsync(f.Page, f.PageSize, ct);
    }

    public async Task<PagedResult<PoolBalanceRow>> PoolBalancesAsync(ReportFilter f, CancellationToken ct = default)
    {
        EnsureReader();
        return await (await PoolQueryAsync(f, ct)).ToPageAsync(f.Page, f.PageSize, ct);
    }

    /// <summary>One file: a row per tagged asset (quantity columns empty) followed by a row per pool balance.</summary>
    public async Task<CsvExport> ExportInventoryAsync(ReportFilter f, CancellationToken ct = default)
    {
        EnsureReader();
        var assets = await (await AssetQueryAsync(f, ct)).ToListAsync(ct);
        // The asset state filter has no pool equivalent; with a state filter only matching assets are exported.
        var pools = f.State is null ? await (await PoolQueryAsync(f, ct)).ToListAsync(ct) : [];

        var csv = new CsvWriter().Header("Kind", "Tag/SKU", "Type", "Site", "State", "Description", "Serial number",
            "Phone number", "IMEI", "Current holder", "Holder username", "Due date", "Available", "Outstanding", "On hand");
        foreach (var a in assets)
            csv.Text("Tagged").Text(a.Tag).Text(a.Type.ToString()).Text(a.SiteKey).Text(a.State.ToString())
                .Text(a.Description).Text(a.SerialNumber).Text(a.PhoneNumber).Text(a.Imei).Text(a.HolderName)
                .Text(a.HolderUsername).Date(a.DueDate).Text(null).Text(null).Text(null).EndRow();
        foreach (var p in pools)
            csv.Text("Pool").Text(p.Sku).Text(p.Type.ToString()).Text(p.SiteKey).Text(null).Text(p.Description)
                .Text(null).Text(null).Text(null).Text(null).Text(null).Date(null)
                .Number(p.Available).Number(p.Outstanding).Number(p.OnHand).EndRow();

        return await FinishExportAsync(ExportInventoryAction, "inventory", f, csv, assets.Count + pools.Count, ct);
    }

    private async Task<IQueryable<InventoryAssetRow>> AssetQueryAsync(ReportFilter f, CancellationToken ct)
    {
        var q = (await access.VisibleAssetsAsync(ct)).AsNoTracking();
        if (f.SiteId is { } siteId) q = q.Where(a => a.SiteId == siteId);
        if (f.Type is { } type) q = q.Where(a => a.Type == type);
        if (f.State is { } state) q = q.Where(a => a.State == state);
        if (Pattern(f.Query) is { } p)
            q = q.Where(a => EF.Functions.Like(a.Tag, p, @"\") || EF.Functions.Like(a.Description, p, @"\") ||
                             (a.SerialNumber != null && EF.Functions.Like(a.SerialNumber, p, @"\")));

        return q.OrderBy(a => a.Site!.Key).ThenBy(a => a.Tag).Select(a => new InventoryAssetRow(
            a.Id, a.Tag, a.Type, a.SiteId, a.Site!.Key, a.State, a.Description, a.SerialNumber, a.PhoneNumber, a.Imei,
            db.Loans.Where(l => l.AssetId == a.Id && l.ClosedAt == null).Select(l => l.Holder!.DisplayName).FirstOrDefault(),
            db.Loans.Where(l => l.AssetId == a.Id && l.ClosedAt == null).Select(l => l.Holder!.Username).FirstOrDefault(),
            db.Loans.Where(l => l.AssetId == a.Id && l.ClosedAt == null).Select(l => l.DueDate).FirstOrDefault()));
    }

    private async Task<IQueryable<PoolBalanceRow>> PoolQueryAsync(ReportFilter f, CancellationToken ct)
    {
        var q = (await access.VisiblePoolsAsync(ct)).AsNoTracking();
        if (f.SiteId is { } siteId) q = q.Where(p => p.SiteId == siteId);
        if (f.Type is { } type) q = q.Where(p => p.Type == type);
        if (Pattern(f.Query) is { } pat)
            q = q.Where(p => EF.Functions.Like(p.Sku, pat, @"\") || EF.Functions.Like(p.Description, pat, @"\"));

        return q.OrderBy(p => p.Site!.Key).ThenBy(p => p.Sku).Select(p => new PoolBalanceRow(
            p.Id, p.Sku, p.Type, p.SiteId, p.Site!.Key, p.Description, p.Available, p.Outstanding));
    }

    // ---------------------------------------------------------------- current assignments

    /// <summary>Open loans (tagged and pool), oldest first. Date range filters the due date.</summary>
    public async Task<PagedResult<LoanRow>> AssignmentsAsync(ReportFilter f, CancellationToken ct = default)
    {
        EnsureReader();
        return await (await AssignmentQueryAsync(f, ct)).ToPageAsync(f.Page, f.PageSize, ct);
    }

    public async Task<CsvExport> ExportAssignmentsAsync(ReportFilter f, CancellationToken ct = default)
    {
        EnsureReader();
        var rows = await (await AssignmentQueryAsync(f, ct)).ToListAsync(ct);
        var csv = LoanCsv(rows, includeHistory: false, includeOverdue: false);
        return await FinishExportAsync(ExportAssignmentsAction, "assignments", f, csv, rows.Count, ct);
    }

    private async Task<IQueryable<LoanRow>> AssignmentQueryAsync(ReportFilter f, CancellationToken ct)
    {
        var q = FilterLoans(await access.VisibleLoansAsync(ct), f, dueDateRange: true).Where(l => l.ClosedAt == null);
        return q.OrderBy(l => l.StartedAt).ThenBy(l => l.Id).Select(ToRow);
    }

    // ---------------------------------------------------------------- overdue

    /// <summary>
    /// Open loans that <see cref="OverduePolicy"/> considers overdue on their site's local today
    /// (<see cref="SiteCalendar"/>), most overdue first, with days overdue (0 = due today). Partially returned pool loans stay listed with their outstanding quantity.
    /// </summary>
    public async Task<PagedResult<LoanRow>> OverdueAsync(ReportFilter f, CancellationToken ct = default)
    {
        EnsureReader();
        var page = await (await OverdueQueryAsync(f, ct)).ToPageAsync(f.Page, f.PageSize, ct);
        var today = await SiteTodayAsync(ct);
        return page with { Items = page.Items.Select(r => WithDaysOverdue(r, today)).ToList() };
    }

    public async Task<CsvExport> ExportOverdueAsync(ReportFilter f, CancellationToken ct = default)
    {
        EnsureReader();
        var today = await SiteTodayAsync(ct);
        var rows = (await (await OverdueQueryAsync(f, ct)).ToListAsync(ct))
            .Select(r => WithDaysOverdue(r, today)).ToList();
        var csv = LoanCsv(rows, includeHistory: false, includeOverdue: true);
        return await FinishExportAsync(ExportOverdueAction, "overdue", f, csv, rows.Count, ct);
    }

    private async Task<IQueryable<LoanRow>> OverdueQueryAsync(ReportFilter f, CancellationToken ct)
    {
        var q = FilterLoans(await access.VisibleLoansAsync(ct), f, dueDateRange: true)
            .Where(l => l.ClosedAt == null)
            .Where(await calendar.OverduePredicateAsync(ct));
        return q.OrderBy(l => l.DueDate).ThenBy(l => l.Id).Select(ToRow);
    }

    private async Task<IReadOnlyDictionary<int, DateOnly>> SiteTodayAsync(CancellationToken ct) =>
        (await calendar.AllSitesAsync(ct)).ToDictionary(s => s.SiteId, s => s.Today);

    private static LoanRow WithDaysOverdue(LoanRow r, IReadOnlyDictionary<int, DateOnly> siteToday) =>
        r with { DaysOverdue = OverduePolicy.DaysOverdue(r.DueDate!.Value, siteToday[r.SiteId]) };

    // ---------------------------------------------------------------- history

    /// <summary>
    /// All loans (open and closed) in their effective, corrected state, newest first, with correction count and the
    /// latest correction reason. Date range filters the loan start (UTC calendar day).
    /// </summary>
    public async Task<PagedResult<LoanRow>> HistoryAsync(ReportFilter f, CancellationToken ct = default)
    {
        EnsureReader();
        return await (await HistoryQueryAsync(f, ct)).ToPageAsync(f.Page, f.PageSize, ct);
    }

    /// <summary>Loan-level history export: one row per loan in effective state plus correction summary.</summary>
    public async Task<CsvExport> ExportHistoryAsync(ReportFilter f, CancellationToken ct = default)
    {
        EnsureReader();
        var rows = await (await HistoryQueryAsync(f, ct)).ToListAsync(ct);
        var csv = LoanCsv(rows, includeHistory: true, includeOverdue: false);
        return await FinishExportAsync(ExportHistoryAction, "history", f, csv, rows.Count, ct, level: "loans");
    }

    /// <summary>
    /// Event-level history export: one row per <see cref="LoanEvent"/> of the matching loans, including
    /// <see cref="LoanEventType.Corrected"/> events with the event they supersede, reason and actor, so the original
    /// and the correction link are both visible.
    /// </summary>
    public async Task<CsvExport> ExportHistoryEventsAsync(ReportFilter f, CancellationToken ct = default)
    {
        EnsureReader();
        var loans = FilterLoans(await access.VisibleLoansAsync(ct), f, dueDateRange: false);
        var rows = await (
            from e in db.LoanEvents.AsNoTracking()
            join l in loans on e.LoanId equals l.Id
            join h in db.People on e.HolderId equals h.Id into hj
            from h in hj.DefaultIfEmpty()
            orderby e.LoanId, e.Id
            select new LoanEventRow
            {
                EventId = e.Id,
                LoanId = e.LoanId,
                Type = e.Type,
                OccurredAt = e.OccurredAt,
                RecordedAt = e.RecordedAt,
                Actor = e.Actor,
                AssetTag = l.Asset != null ? l.Asset.Tag : null,
                PoolSku = l.Pool != null ? l.Pool.Sku : null,
                SiteKey = l.Asset != null ? l.Asset.Site!.Key : l.Pool!.Site!.Key,
                HolderId = e.HolderId,
                HolderDisplayName = h.DisplayName,
                HolderUsername = h.Username,
                HolderEmail = h.Email,
                Quantity = e.Quantity,
                DueDate = e.DueDate,
                Condition = e.Condition,
                Note = e.Note,
                Reason = e.Reason,
                SupersedesEventId = e.SupersedesEventId,
                SupersededByEventId = db.LoanEvents.Where(c => c.SupersedesEventId == e.Id)
                    .OrderByDescending(c => c.Id).Select(c => (long?)c.Id).FirstOrDefault(),
            }).ToListAsync(ct);

        var csv = new CsvWriter().Header("Event id", "Loan id", "Event", "Occurred (UTC)", "Recorded (UTC)", "Actor",
            "Asset tag", "Pool SKU", "Site", "Holder", "Holder username", "Holder email", "Quantity", "Due date",
            "Condition", "Note", "Reason", "Supersedes event", "Superseded by event");
        foreach (var r in rows)
            csv.Number(r.EventId).Number(r.LoanId).Text(r.Type.ToString()).Timestamp(r.OccurredAt).Timestamp(r.RecordedAt)
                .Text(r.Actor).Text(r.AssetTag).Text(r.PoolSku).Text(r.SiteKey).Text(r.HolderDisplayName)
                .Text(r.HolderUsername).Text(r.HolderEmail).Number(r.Quantity).Date(r.DueDate).Text(r.Condition)
                .Text(r.Note).Text(r.Reason).Number(r.SupersedesEventId).Number(r.SupersededByEventId).EndRow();

        return await FinishExportAsync(ExportHistoryAction, "history-events", f, csv, rows.Count, ct, level: "events");
    }

    private async Task<IQueryable<LoanRow>> HistoryQueryAsync(ReportFilter f, CancellationToken ct)
    {
        var q = FilterLoans(await access.VisibleLoansAsync(ct), f, dueDateRange: false);
        return q.OrderByDescending(l => l.StartedAt).ThenByDescending(l => l.Id).Select(ToRow);
    }

    // ---------------------------------------------------------------- shared

    private void EnsureReader()
    {
        if (!user.IsIt && !user.IsManager) throw new AccessDeniedException();
    }

    private static string? Pattern(string? query)
    {
        var term = query?.Trim();
        return string.IsNullOrEmpty(term) ? null : Paging.ContainsPattern(term);
    }

    /// <summary>Applies site/type/text/date filters to an already access-scoped loan query.</summary>
    private static IQueryable<Loan> FilterLoans(IQueryable<Loan> q, ReportFilter f, bool dueDateRange)
    {
        q = q.AsNoTracking();
        if (f.SiteId is { } siteId)
            q = q.Where(l => (l.AssetId != null && l.Asset!.SiteId == siteId) || (l.PoolId != null && l.Pool!.SiteId == siteId));
        if (f.Type is { } type)
            q = q.Where(l => (l.AssetId != null && l.Asset!.Type == type) || (l.PoolId != null && l.Pool!.Type == type));
        if (Pattern(f.Query) is { } p)
            q = q.Where(l =>
                EF.Functions.Like(l.Holder!.DisplayName, p, @"\") ||
                EF.Functions.Like(l.Holder!.Username, p, @"\") ||
                (l.Holder!.Email != null && EF.Functions.Like(l.Holder!.Email, p, @"\")) ||
                EF.Functions.Like(l.HolderNameSnapshot, p, @"\") ||
                (l.AssetId != null && EF.Functions.Like(l.Asset!.Tag, p, @"\")) ||
                (l.PoolId != null && EF.Functions.Like(l.Pool!.Sku, p, @"\")));

        if (dueDateRange)
        {
            if (f.From is { } from) q = q.Where(l => l.DueDate != null && l.DueDate >= from);
            if (f.To is { } to) q = q.Where(l => l.DueDate != null && l.DueDate <= to);
        }
        else
        {
            // Timestamps are stored in UTC (offset 0), so UTC-midnight bounds compare correctly on every provider.
            if (f.From is { } from)
            {
                var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                q = q.Where(l => l.StartedAt >= start);
            }
            if (f.To is { } to)
            {
                var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                q = q.Where(l => l.StartedAt < end);
            }
        }
        return q;
    }

    private static readonly Expression<Func<Loan, LoanRow>> ToRow = l => new LoanRow
    {
        LoanId = l.Id,
        IsPool = l.PoolId != null,
        AssetTag = l.Asset != null ? l.Asset.Tag : null,
        PoolSku = l.Pool != null ? l.Pool.Sku : null,
        ItemDescription = l.Asset != null ? l.Asset.Description : l.Pool!.Description,
        Type = l.Asset != null ? l.Asset.Type : l.Pool!.Type,
        SiteId = l.AssetId != null ? l.Asset!.SiteId : l.Pool!.SiteId,
        SiteKey = l.Asset != null ? l.Asset.Site!.Key : l.Pool!.Site!.Key,
        Quantity = l.Quantity,
        ReturnedQuantity = l.ReturnedQuantity,
        StartedAt = l.StartedAt,
        DueDate = l.DueDate,
        ClosedAt = l.ClosedAt,
        HolderId = l.HolderId,
        HolderDisplayName = l.Holder!.DisplayName,
        HolderUsername = l.Holder!.Username,
        HolderEmail = l.Holder!.Email,
        HolderJobTitle = l.Holder!.JobTitle,
        HolderSiteKey = l.Holder!.Site != null ? l.Holder.Site.Key : null,
        HolderActive = l.Holder!.IsActive,
        HolderNameSnapshot = l.HolderNameSnapshot,
        CheckoutCondition = l.CheckoutCondition,
        ReturnCondition = l.ReturnCondition,
        CreatedBy = l.CreatedBy,
        EffectiveVersion = l.EffectiveVersion,
        CorrectionCount = l.Events.Count(e => e.Type == LoanEventType.Corrected),
        LatestCorrectionReason = l.Events.Where(e => e.Type == LoanEventType.Corrected)
            .OrderByDescending(e => e.Id).Select(e => e.Reason).FirstOrDefault(),
    };

    private static CsvWriter LoanCsv(IEnumerable<LoanRow> rows, bool includeHistory, bool includeOverdue)
    {
        var headers = new List<string>
        {
            "Loan id", "Kind", "Asset tag", "Pool SKU", "Item", "Type", "Site", "Quantity", "Returned", "Outstanding",
            "Started (UTC)", "Due date",
        };
        if (includeOverdue) headers.Add("Days overdue");
        if (includeHistory) headers.AddRange(["Closed (UTC)", "Status"]);
        headers.AddRange(["Holder", "Holder username", "Holder email", "Holder job title", "Holder site",
            "Holder active", "Name at checkout", "Checkout condition"]);
        if (includeHistory) headers.AddRange(["Return condition", "Corrections", "Latest correction reason"]);

        var csv = new CsvWriter().Header([.. headers]);
        foreach (var r in rows)
        {
            csv.Number(r.LoanId).Text(r.IsPool ? "Pool" : "Tagged").Text(r.AssetTag).Text(r.PoolSku)
                .Text(r.ItemDescription).Text(r.Type.ToString()).Text(r.SiteKey)
                .Number(r.Quantity).Number(r.ReturnedQuantity).Number(r.OutstandingQuantity)
                .Timestamp(r.StartedAt).Date(r.DueDate);
            if (includeOverdue) csv.Number(r.DaysOverdue);
            if (includeHistory) csv.Timestamp(r.ClosedAt).Text(r.IsOpen ? "Open" : "Returned");
            csv.Text(r.HolderDisplayName).Text(r.HolderUsername).Text(r.HolderEmail).Text(r.HolderJobTitle)
                .Text(r.HolderSiteKey).Bool(r.HolderActive).Text(r.HolderNameSnapshot).Text(r.CheckoutCondition);
            if (includeHistory) csv.Text(r.ReturnCondition).Number(r.CorrectionCount).Text(r.LatestCorrectionReason);
            csv.EndRow();
        }
        return csv;
    }

    private async Task<CsvExport> FinishExportAsync(string action, string name, ReportFilter f, CsvWriter csv,
        int rowCount, CancellationToken ct, string? level = null)
    {
        audit.Record(action, "Report", name, f.SiteId,
            details: new { Filters = f.ForAudit(), RowCount = rowCount, Level = level });
        await db.SaveChangesAsync(ct);
        // The file name uses the default timezone's date: an export can span sites.
        return new CsvExport($"{name}-{calendar.DefaultToday:yyyy-MM-dd}.csv", csv.ToUtf8Bytes(), rowCount);
    }
}
