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

/// <summary>
/// New tagged asset. Step 1 (a GET form) picks type and site; the page then shows the standard fields that type uses,
/// its custom fields, the vendor/model dropdowns and the tag the generator would hand out. Step 2 (POST) creates it.
/// </summary>
[Authorize(Policy = Policies.ItOnly)]
public class CreateModel(AssetService assets, CatalogService catalog, TagGenerator tags, AppDbContext db) : PageModel
{
    public class InputModel
    {
        public string? Tag { get; set; }
        public int TypeId { get; set; }
        public int SiteId { get; set; }
        public int? VendorId { get; set; }
        public int? ModelId { get; set; }
        public string? Description { get; set; }
        public string? Condition { get; set; }
        public string? SerialNumber { get; set; }

        /// <summary>Custom field id → submitted values (several for multi-value fields).</summary>
        public Dictionary<int, string?[]> Fields { get; set; } = [];
    }

    /// <summary>Step 1 selection (query string); the POST carries it in <see cref="Input"/>.</summary>
    [BindProperty(SupportsGet = true)] public int? TypeId { get; set; }
    [BindProperty(SupportsGet = true)] public int? SiteId { get; set; }

    [BindProperty] public InputModel Input { get; set; } = new();

    public AssetType? Type { get; private set; }
    public List<SelectListItem> Types { get; private set; } = [];
    public List<SelectListItem> Sites { get; private set; } = [];
    public IReadOnlyList<Vendor> Vendors { get; private set; } = [];
    public IReadOnlyList<AssetModel> Models { get; private set; } = [];
    public TagPreview? Preview { get; private set; }

    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        Types = await InventoryUi.TypeOptionsAsync(db, InventoryMode.Tagged, ct: ct);
        Sites = await InventoryUi.SiteOptionsAsync(db, ct: ct);
        Input.TypeId = TypeId ?? (Types.Count > 0 ? int.Parse(Types[0].Value) : 0);
        Input.SiteId = SiteId ?? (Sites.Count > 0 ? int.Parse(Sites[0].Value) : 0);
        await LoadAsync(ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var fields = Input.Fields.ToDictionary(f => f.Key, f => (IReadOnlyList<string?>)f.Value);
                var asset = await assets.CreateAsync(new CreateAssetInput(Input.Tag, Input.TypeId, Input.SiteId,
                    Input.Description, Input.Condition, Input.SerialNumber, Input.VendorId, Input.ModelId, fields), ct);
                Message = $"Asset {asset.Tag} created.";
                return RedirectToPage("Details", new { id = asset.Id });
            }
            catch (DomainException ex)
            {
                ModelState.AddModelError("", ex.Message);
            }
        }
        Types = await InventoryUi.TypeOptionsAsync(db, InventoryMode.Tagged, ct: ct);
        Sites = await InventoryUi.SiteOptionsAsync(db, ct: ct);
        await LoadAsync(ct);
        return Page();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var type = Input.TypeId > 0 ? await catalog.GetTypeAsync(Input.TypeId, ct) : null;
        if (type is null || type.Mode != InventoryMode.Tagged) return;
        Type = type;
        if (type.UsesModel)
        {
            Vendors = await catalog.ListVendorsAsync(ct: ct);
            Models = await catalog.ListModelsAsync(typeId: type.Id, ct: ct);
        }
        if (Input.SiteId > 0) Preview = await tags.PreviewAsync(type.Id, Input.SiteId, ct);
    }
}
