using AssetTracker.Core.Catalog;
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
public class EditModel(AssetService assets, CatalogService catalog, AppDbContext db) : PageModel
{
    public class InputModel
    {
        public string? Description { get; set; }
        public string? Condition { get; set; }
        public string? SerialNumber { get; set; }
        public int? VendorId { get; set; }
        public int? ModelId { get; set; }

        /// <summary>Custom field id → values (several for multi-value fields).</summary>
        public Dictionary<int, string?[]> Fields { get; set; } = [];
    }

    [BindProperty] public InputModel Input { get; set; } = new();
    public IndividualAsset Asset { get; private set; } = null!;
    public AssetType Type { get; private set; } = null!;
    public IReadOnlyList<Vendor> Vendors { get; private set; } = [];
    public IReadOnlyList<AssetModel> Models { get; private set; } = [];
    public List<SelectListItem> Sites { get; private set; } = [];

    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync(int id, CancellationToken ct)
    {
        await LoadAsync(id, ct);
        Input = new InputModel
        {
            Description = Asset.Description, Condition = Asset.Condition, SerialNumber = Asset.SerialNumber,
            VendorId = Asset.VendorId, ModelId = Asset.ModelId,
            Fields = Asset.Values.GroupBy(v => v.FieldId)
                .ToDictionary(g => g.Key, g => g.OrderBy(v => v.Position).Select(v => (string?)v.Value).ToArray()),
        };
    }

    public async Task<IActionResult> OnPostAsync(int id, CancellationToken ct)
    {
        // Every active field is sent explicitly, so a field the user cleared is cleared (a missing field would keep its values).
        var type = await catalog.GetTypeAsync((await assets.GetAsync(id, ct)).TypeId, ct);
        var fields = type.Fields.Where(f => f.IsActive).ToDictionary(f => f.Id,
            f => (IReadOnlyList<string?>)(Input.Fields.TryGetValue(f.Id, out var v) ? v : []));
        return await RunAsync(id, "Asset updated.", () => assets.EditAsync(id, new EditAssetInput(Input.Description,
            Input.Condition, Input.SerialNumber, Input.VendorId, Input.ModelId, fields), ct), ct);
    }

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
        Type = await catalog.GetTypeAsync(Asset.TypeId, ct);
        if (Type.UsesModel)
        {
            Vendors = await catalog.ListVendorsAsync(includeInactive: true, ct: ct);
            Models = await catalog.ListModelsAsync(typeId: Type.Id, includeInactive: true, ct: ct);
        }
        Sites = (await InventoryUi.SiteOptionsAsync(db, ct: ct)).Where(s => s.Value != Asset.SiteId.ToString()).ToList();
    }
}
