using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Reporting;
using AssetTracker.Core.Security;
using AssetTracker.Web.Pages.Shared;
using Microsoft.AspNetCore.Mvc;

namespace AssetTracker.Web.Pages.Reports;

/// <summary>
/// Full loan history in effective (corrected) state with correction summary. Two exports: loan level
/// (<c>?handler=Export</c>) and event level including superseding corrections (<c>?handler=ExportEvents</c>).
/// </summary>
public class HistoryModel(ReportService reports, AccessScope access, AppDbContext db) : ReportPageModel(access, db)
{
    public const string EventsHandler = "ExportEvents";

    public override string? DateRangeLabel => "Started (UTC)";

    public PagedResult<LoanRow> Results { get; private set; } = null!;
    public PagerModel Pager => PagerFor(Results);

    public async Task OnGetAsync(CancellationToken ct)
    {
        Results = await reports.HistoryAsync(Filter, ct);
        await LoadSitesAsync(ct);
    }

    public async Task<IActionResult> OnGetExportAsync(CancellationToken ct) =>
        Csv(await reports.ExportHistoryAsync(Filter, ct));

    public async Task<IActionResult> OnGetExportEventsAsync(CancellationToken ct) =>
        Csv(await reports.ExportHistoryEventsAsync(Filter, ct));
}
