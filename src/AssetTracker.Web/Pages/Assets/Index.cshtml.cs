using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Inventory;
using AssetTracker.Core.Security;
using AssetTracker.Web.Pages.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AssetTracker.Web.Pages.Assets;

[Authorize(Policy = Policies.InventoryRead)]
public class IndexModel(AssetService assets, AccessScope access, AppDbContext db, ICurrentUser user) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public int? SiteId { get; set; }
    [BindProperty(SupportsGet = true, Name = "type")] public int? TypeId { get; set; }
    [BindProperty(SupportsGet = true)] public AssetState? State { get; set; }
    [BindProperty(SupportsGet = true, Name = "p")] public int PageNumber { get; set; } = 1;

    public PagedResult<IndividualAsset> Results { get; private set; } = null!;
    public List<SelectListItem> Sites { get; private set; } = [];
    public List<SelectListItem> Types { get; private set; } = [];
    public bool CanEdit => user.IsIt;

    public PagerModel Pager => PagerModel.From(Results, new Dictionary<string, object?>
    {
        ["q"] = Q, ["siteId"] = SiteId, ["type"] = TypeId, ["state"] = State,
    });

    public async Task OnGetAsync(CancellationToken ct)
    {
        Results = await assets.SearchAsync(new AssetSearch(Q, SiteId, TypeId, State, PageNumber), ct);
        Sites = await InventoryUi.SiteOptionsAsync(db, await access.GetAsync(ct), ct);
        Types = await InventoryUi.TypeOptionsAsync(db, InventoryMode.Tagged, includeInactive: true, ct: ct);
    }
}
