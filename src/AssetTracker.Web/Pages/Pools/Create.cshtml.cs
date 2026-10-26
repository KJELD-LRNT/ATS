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

namespace AssetTracker.Web.Pages.Pools;

[Authorize(Policy = Policies.ItOnly)]
public class CreateModel(PoolService pools, AppDbContext db) : PageModel
{
    public class InputModel
    {
        public AssetType Type { get; set; } = AssetType.Headset;
        public string? Sku { get; set; }
        public int SiteId { get; set; }
        public string? Description { get; set; }
        public int InitialQuantity { get; set; }
        public string? Reason { get; set; }
    }

    [BindProperty] public InputModel Input { get; set; } = new();
    public List<SelectListItem> Sites { get; private set; } = [];

    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync(CancellationToken ct) => Sites = await InventoryUi.SiteOptionsAsync(db, ct: ct);

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var pool = await pools.CreateAsync(new CreatePoolInput(Input.Type, Input.Sku ?? "", Input.SiteId,
                    Input.Description ?? "", Input.InitialQuantity, Input.Reason), ct);
                Message = $"Pool {pool.Sku} created.";
                return RedirectToPage("Details", new { id = pool.Id });
            }
            catch (DomainException ex)
            {
                ModelState.AddModelError("", ex.Message);
            }
        }
        Sites = await InventoryUi.SiteOptionsAsync(db, ct: ct);
        return Page();
    }
}
