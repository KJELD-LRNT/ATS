using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Reporting;
using AssetTracker.Core.Security;
using AssetTracker.Web.Pages.Shared;
using Microsoft.AspNetCore.Mvc;

namespace AssetTracker.Web.Pages.Reports;

/// <summary>Open assignments that are overdue on their site's local day per <c>OverduePolicy</c>.</summary>
public class OverdueModel(ReportService reports, AccessScope access, AppDbContext db)
    : ReportPageModel(access, db)
{
    public override string? DateRangeLabel => "Due";

    public PagedResult<LoanRow> Results { get; private set; } = null!;
    public PagerModel Pager => PagerFor(Results);

    public async Task OnGetAsync(CancellationToken ct)
    {
        Results = await reports.OverdueAsync(Filter, ct);
        await LoadSitesAsync(ct);
    }

    public async Task<IActionResult> OnGetExportAsync(CancellationToken ct) =>
        Csv(await reports.ExportOverdueAsync(Filter, ct));
}
