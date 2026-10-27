using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Inventory;
using AssetTracker.Core.Lending;
using AssetTracker.Core.Security;
using AssetTracker.Core.Workplace;
using AssetTracker.Web.Pages.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Pools;

/// <summary>Pool balances, open loans and stock ledger. IT may also set the pool's minimum stock level.</summary>
[Authorize(Policy = Policies.InventoryRead)]
public class DetailsModel(PoolService pools, LoanHistoryService loans, AssignmentService assignments, ICurrentUser user) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "p")] public int PageNumber { get; set; } = 1;

    public Pool Pool { get; private set; } = null!;
    public PagedResult<StockEvent> Events { get; private set; } = null!;
    public IReadOnlyList<Loan> OpenLoans { get; private set; } = [];
    public bool CanEdit => user.IsIt;

    /// <summary>Workspots on floor plans where units of this pool are placed (location only).</summary>
    public IReadOnlyList<PlaceInfo> Places { get; private set; } = [];
    public PagerModel Pager => PagerModel.From(Events, []);

    [TempData] public string? Message { get; set; }

    public Task OnGetAsync(int id, CancellationToken ct) => LoadAsync(id, ct);

    /// <summary>Sets the pool's minimum stock level; empty clears it.</summary>
    public async Task<IActionResult> OnPostMinimumAsync(int id, int? minimum, CancellationToken ct)
    {
        try
        {
            await pools.SetMinimumAsync(id, minimum, ct);
            Message = minimum is null ? "Minimum stock cleared." : $"Minimum stock set to {minimum}.";
            return RedirectToPage(new { id });
        }
        catch (DomainException ex)
        {
            ModelState.AddModelError("", ex.Message);
        }
        await LoadAsync(id, ct);
        return Page();
    }

    private async Task LoadAsync(int id, CancellationToken ct)
    {
        Pool = await pools.GetAsync(id, ct);
        Events = await pools.GetStockEventsAsync(id, PageNumber, ct: ct);
        OpenLoans = await loans.OpenForPoolAsync(id, ct);
        Places = await assignments.LocationsOfPoolAsync(id, ct);
    }
}
