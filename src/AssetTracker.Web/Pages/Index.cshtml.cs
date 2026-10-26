using AssetTracker.Core.Reporting;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages;

/// <summary>
/// Overview. For IT and managers it shows figures the reports already compute (scoped to the principal's sites):
/// the report summary, the first overdue/due-today loans and the pools lowest on stock. Others see a short notice.
/// </summary>
public class IndexModel(ICurrentUser user, ReportService reports) : PageModel
{
    public const int OverdueRows = 8;
    public const int StockRows = 5;

    public string UserName => user.Name;
    public AppRoles Roles => user.Roles;
    public bool CanRead => user.IsIt || user.IsManager;
    public bool CanEdit => user.IsIt;

    public InventorySummary? Summary { get; private set; }
    public IReadOnlyList<LoanRow> Overdue { get; private set; } = [];
    public int OverdueTotal { get; private set; }
    public IReadOnlyList<PoolBalanceRow> LowStock { get; private set; } = [];
    public int PoolCount { get; private set; }

    public int PoolAvailable => Summary?.Pools.Sum(p => p.Available) ?? 0;
    public int PoolOnHand => Summary?.Pools.Sum(p => p.OnHand) ?? 0;
    public int AssetsAvailable => Summary?.Assets.Where(a => a.State == Core.Domain.AssetState.Available).Sum(a => a.Count) ?? 0;
    public int AssetsTotal => Summary?.Assets.Where(a => a.State != Core.Domain.AssetState.Retired).Sum(a => a.Count) ?? 0;

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (!CanRead) return;
        Summary = await reports.GetSummaryAsync(ct);
        var overdue = await reports.OverdueAsync(new ReportFilter(PageSize: OverdueRows), ct);
        Overdue = overdue.Items;
        OverdueTotal = overdue.TotalCount;
        var pools = await reports.PoolBalancesAsync(new ReportFilter(PageSize: Core.Common.Paging.MaxPageSize), ct);
        PoolCount = pools.TotalCount;
        LowStock = pools.Items
            .OrderBy(p => p.OnHand == 0 ? 0 : (double)p.Available / p.OnHand).ThenBy(p => p.Available).ThenBy(p => p.Sku)
            .Take(StockRows).ToList();
    }
}
