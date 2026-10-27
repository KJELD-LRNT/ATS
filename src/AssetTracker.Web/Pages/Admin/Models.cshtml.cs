using AssetTracker.Core.Catalog;
using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using AssetTracker.Web.Pages.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AssetTracker.Web.Pages.Admin;

/// <summary>Admin: the models offered in the model dropdown of asset forms (per vendor and asset type).</summary>
[Authorize(Policy = Policies.AdminOnly)]
public class ModelsModel(CatalogService catalog) : PageModel
{
    [BindProperty(SupportsGet = true, Name = "type")] public int? TypeFilter { get; set; }
    [BindProperty(SupportsGet = true, Name = "vendor")] public int? VendorFilter { get; set; }

    public IReadOnlyList<AssetModel> Models { get; private set; } = [];
    public List<SelectListItem> Types { get; private set; } = [];
    public List<SelectListItem> Vendors { get; private set; } = [];

    /// <summary>Model being edited (from <c>?editId=</c>), or null for the create form.</summary>
    public AssetModel? Editing { get; private set; }

    [BindProperty] public ModelForm Input { get; set; } = new();

    [TempData] public string? Message { get; set; }

    public class ModelForm
    {
        public int TypeId { get; set; }
        public int VendorId { get; set; }
        public string Name { get; set; } = "";
        public bool IsActive { get; set; } = true;
    }

    public async Task OnGetAsync(int? editId, CancellationToken ct)
    {
        await LoadAsync(ct);
        if (Models.FirstOrDefault(m => m.Id == editId) is { } m)
        {
            Editing = m;
            Input = new ModelForm { TypeId = m.TypeId, VendorId = m.VendorId, Name = m.Name, IsActive = m.IsActive };
        }
        else
        {
            // Pre-select the filter in the create form.
            Input.TypeId = TypeFilter ?? 0;
            Input.VendorId = VendorFilter ?? 0;
        }
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        try
        {
            var model = await catalog.CreateModelAsync(Input.TypeId, Input.VendorId, Input.Name, ct);
            Message = $"Model {model.DisplayName} added.";
            return RedirectToPage(new { type = model.TypeId, vendor = model.VendorId });
        }
        catch (DomainException ex) { this.Show(ex); }
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateAsync(int id, CancellationToken ct)
    {
        try
        {
            var model = await catalog.UpdateModelAsync(id, Input.Name, Input.IsActive, ct);
            Message = $"Model {model.DisplayName} saved.";
            return RedirectToPage(new { type = TypeFilter, vendor = VendorFilter });
        }
        catch (DomainException ex) { this.Show(ex); }
        await LoadAsync(ct);
        Editing = Models.FirstOrDefault(m => m.Id == id);
        return Page();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Models = await catalog.ListModelsAsync(TypeFilter, VendorFilter, includeInactive: true, ct: ct);
        Types = (await catalog.ListTypesAsync(InventoryMode.Tagged, includeInactive: true, ct: ct))
            .Select(t => new SelectListItem(t.IsActive ? t.Name : $"{t.Name} (inactive)", t.Id.ToString())).ToList();
        Vendors = (await catalog.ListVendorsAsync(includeInactive: true, ct: ct))
            .Select(v => new SelectListItem(v.IsActive ? v.Name : $"{v.Name} (inactive)", v.Id.ToString())).ToList();
    }
}
