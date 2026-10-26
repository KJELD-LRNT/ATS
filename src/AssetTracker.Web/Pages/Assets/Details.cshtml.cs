using AssetTracker.Core.Domain;
using AssetTracker.Core.Inventory;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Assets;

[Authorize(Policy = Policies.InventoryRead)]
public class DetailsModel(AssetService assets, ICurrentUser user) : PageModel
{
    public IndividualAsset Asset { get; private set; } = null!;
    public IReadOnlyList<Loan> Loans { get; private set; } = [];
    public bool CanEdit => user.IsIt;

    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync(int id, CancellationToken ct)
    {
        Asset = await assets.GetAsync(id, ct);
        Loans = await assets.GetLoanHistoryAsync(id, ct);
    }
}
