using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Reporting;
using AssetTracker.Core.Security;
using AssetTracker.Web.Pages.Shared;
using Microsoft.AspNetCore.Mvc;

namespace AssetTracker.Web.Pages.Reports;

/// <summary>Inventory by site/type/status: tagged assets or pool balances (<c>view=pools</c>), one export for both.</summary>
public class InventoryModel(ReportService reports, AccessScope access, AppDbContext db) : ReportPageModel(access, db)
{
    [BindProperty(SupportsGet = true)] public AssetState? State { get; set; }
    [BindProperty(SupportsGet = true)] public string? View { get; set; }

    public bool ShowPools => string.Equals(View, "pools", StringComparison.OrdinalIgnoreCase);
    public override string SearchPlaceholder => "Tag, SKU, description or serial";

    public PagedResult<InventoryAssetRow>? Assets { get; private set; }
    public PagedResult<PoolBalanceRow>? Pools { get; private set; }

    protected override ReportFilter Filter => new(Q, SiteId, Type, State, null, null, PageNumber);

    public override Dictionary<string, object?> FilterValues => new()
    {
        ["q"] = Q, ["siteId"] = SiteId, ["type"] = Type, ["state"] = State, ["view"] = ShowPools ? "pools" : null,
    };

    public PagerModel Pager => Pools is not null ? PagerFor(Pools) : PagerFor(Assets!);

    public async Task OnGetAsync(CancellationToken ct)
    {
        if (ShowPools) Pools = await reports.PoolBalancesAsync(Filter, ct);
        else Assets = await reports.InventoryAssetsAsync(Filter, ct);
        await LoadSitesAsync(ct);
    }

    public async Task<IActionResult> OnGetExportAsync(CancellationToken ct) =>
        Csv(await reports.ExportInventoryAsync(Filter, ct));
}
