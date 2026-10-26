using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Inventory;
using AssetTracker.Core.Lending;
using AssetTracker.Core.Security;
using AssetTracker.Web.Pages.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Pools;

[Authorize(Policy = Policies.InventoryRead)]
public class DetailsModel(PoolService pools, LoanHistoryService loans, ICurrentUser user) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "p")] public int PageNumber { get; set; } = 1;

    public Pool Pool { get; private set; } = null!;
    public PagedResult<StockEvent> Events { get; private set; } = null!;
    public IReadOnlyList<Loan> OpenLoans { get; private set; } = [];
    public bool CanEdit => user.IsIt;
    public PagerModel Pager => PagerModel.From(Events, []);

    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync(int id, CancellationToken ct)
    {
        Pool = await pools.GetAsync(id, ct);
        Events = await pools.GetStockEventsAsync(id, PageNumber, ct: ct);
        OpenLoans = await loans.OpenForPoolAsync(id, ct);
    }
}
