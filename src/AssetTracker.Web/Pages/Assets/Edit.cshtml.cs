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

/// <summary>Edit fields plus the explicit state actions (retire, unavailable/available, site transfer).</summary>
[Authorize(Policy = Policies.ItOnly)]
public class EditModel(AssetService assets, AppDbContext db) : PageModel
{
    public class InputModel
    {
        public string? Description { get; set; }
        public string? Condition { get; set; }
        public string? SerialNumber { get; set; }
        public string? Imei { get; set; }
        public string? PhoneNumber { get; set; }
    }

    [BindProperty] public InputModel Input { get; set; } = new();
    public IndividualAsset Asset { get; private set; } = null!;
    public List<SelectListItem> Sites { get; private set; } = [];

    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync(int id, CancellationToken ct)
    {
        await LoadAsync(id, ct);
        Input = new InputModel
        {
            Description = Asset.Description, Condition = Asset.Condition, SerialNumber = Asset.SerialNumber,
            Imei = Asset.Imei, PhoneNumber = Asset.PhoneNumber,
        };
    }

    public Task<IActionResult> OnPostAsync(int id, CancellationToken ct) =>
        RunAsync(id, "Asset updated.", () => assets.EditAsync(id, new EditAssetInput(Input.Description ?? "",
            Input.Condition, Input.SerialNumber, Input.Imei, Input.PhoneNumber), ct), ct);

    public Task<IActionResult> OnPostRetireAsync(int id, string? reason, CancellationToken ct) =>
        RunAsync(id, "Asset retired.", () => assets.RetireAsync(id, reason, ct), ct);

    public Task<IActionResult> OnPostMarkUnavailableAsync(int id, string? reason, CancellationToken ct) =>
        RunAsync(id, "Asset marked unavailable.", () => assets.MarkUnavailableAsync(id, reason, ct), ct);

    public Task<IActionResult> OnPostMarkAvailableAsync(int id, string? reason, CancellationToken ct) =>
        RunAsync(id, "Asset marked available.", () => assets.MarkAvailableAsync(id, reason, ct), ct);

    public Task<IActionResult> OnPostTransferAsync(int id, int toSiteId, string? reason, CancellationToken ct) =>
        RunAsync(id, "Asset moved to another site.", () => assets.TransferSiteAsync(id, toSiteId, reason, ct), ct);

    private async Task<IActionResult> RunAsync(int id, string success, Func<Task> action, CancellationToken ct)
    {
        try
        {
            await action();
            Message = success;
            return RedirectToPage("Details", new { id });
        }
        catch (DomainException ex)
        {
            ModelState.AddModelError("", ex.Message);
            await LoadAsync(id, ct);
            return Page();
        }
    }

    private async Task LoadAsync(int id, CancellationToken ct)
    {
        Asset = await assets.GetAsync(id, ct);
        Sites = (await InventoryUi.SiteOptionsAsync(db, ct: ct)).Where(s => s.Value != Asset.SiteId.ToString()).ToList();
    }
}
