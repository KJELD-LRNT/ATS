using AssetTracker.Core.Reporting;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Reports;

/// <summary>Reports landing page: scoped summary counts and links to the four reports/exports.</summary>
[Authorize(Policy = Policies.InventoryRead)]
public class IndexModel(ReportService reports) : PageModel
{
    public InventorySummary Summary { get; private set; } = null!;

    public async Task OnGetAsync(CancellationToken ct) => Summary = await reports.GetSummaryAsync(ct);
}
