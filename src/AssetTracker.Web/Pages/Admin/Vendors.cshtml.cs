using AssetTracker.Core.Catalog;
using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Admin;

/// <summary>Admin: the vendors (manufacturers) offered in the vendor dropdown of asset forms.</summary>
[Authorize(Policy = Policies.AdminOnly)]
public class VendorsModel(CatalogService catalog) : PageModel
{
    public IReadOnlyList<Vendor> Vendors { get; private set; } = [];

    /// <summary>Vendor being edited (from <c>?editId=</c>), or null for the create form.</summary>
    public Vendor? Editing { get; private set; }

    [BindProperty] public VendorForm Input { get; set; } = new();

    [TempData] public string? Message { get; set; }

    public class VendorForm
    {
        public string Name { get; set; } = "";
        public bool IsActive { get; set; } = true;
    }

    public async Task OnGetAsync(int? editId, CancellationToken ct)
    {
        Vendors = await catalog.ListVendorsAsync(includeInactive: true, ct: ct);
        if (Vendors.FirstOrDefault(v => v.Id == editId) is { } v)
        {
            Editing = v;
            Input = new VendorForm { Name = v.Name, IsActive = v.IsActive };
        }
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        try
        {
            var vendor = await catalog.CreateVendorAsync(Input.Name, ct);
            Message = $"Vendor {vendor.Name} added.";
            return RedirectToPage();
        }
        catch (DomainException ex) { this.Show(ex); }
        Vendors = await catalog.ListVendorsAsync(includeInactive: true, ct: ct);
        return Page();
    }

    public async Task<IActionResult> OnPostUpdateAsync(int id, CancellationToken ct)
    {
        try
        {
            var vendor = await catalog.UpdateVendorAsync(id, Input.Name, Input.IsActive, ct);
            Message = $"Vendor {vendor.Name} saved.";
            return RedirectToPage();
        }
        catch (DomainException ex) { this.Show(ex); }
        Vendors = await catalog.ListVendorsAsync(includeInactive: true, ct: ct);
        Editing = Vendors.FirstOrDefault(v => v.Id == id);
        return Page();
    }
}
