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

[Authorize(Policy = Policies.ItOnly)]
public class CreateModel(AssetService assets, AppDbContext db) : PageModel
{
    public class InputModel
    {
        public string? Tag { get; set; }
        public AssetType Type { get; set; } = AssetType.Laptop;
        public int SiteId { get; set; }
        public string? Description { get; set; }
        public string? Condition { get; set; }
        public string? SerialNumber { get; set; }
        public string? Imei { get; set; }
        public string? PhoneNumber { get; set; }
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
                var asset = await assets.CreateAsync(new CreateAssetInput(Input.Tag ?? "", Input.Type, Input.SiteId,
                    Input.Description ?? "", Input.Condition, Input.SerialNumber, Input.Imei, Input.PhoneNumber), ct);
                Message = $"Asset {asset.Tag} created.";
                return RedirectToPage("Details", new { id = asset.Id });
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
