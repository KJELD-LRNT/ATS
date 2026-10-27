using AssetTracker.Core.Domain;
using AssetTracker.Core.Inventory;
using AssetTracker.Core.Security;
using AssetTracker.Core.Workplace;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Assets;

[Authorize(Policy = Policies.InventoryRead)]
public class DetailsModel(AssetService assets, AssignmentService assignments, ICurrentUser user) : PageModel
{
    public IndividualAsset Asset { get; private set; } = null!;
    public IReadOnlyList<Loan> Loans { get; private set; } = [];
    public bool CanEdit => user.IsIt;

    /// <summary>Where the asset is placed on a floor plan (location only, separate from the loan), if the principal can see that floor.</summary>
    public PlaceInfo? Place { get; private set; }

    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync(int id, CancellationToken ct)
    {
        Asset = await assets.GetAsync(id, ct);
        Loans = await assets.GetLoanHistoryAsync(id, ct);
        Place = await assignments.LocationOfAssetAsync(id, ct);
    }
}
