using System.ComponentModel.DataAnnotations;
using AssetTracker.Core.Catalog;
using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Admin;

/// <summary>Admin: the asset types (list and create). A type is edited, with its custom fields, on <c>TypeEdit</c>.</summary>
[Authorize(Policy = Policies.AdminOnly)]
public class TypesModel(CatalogService catalog) : PageModel
{
    public IReadOnlyList<AssetType> Types { get; private set; } = [];

    [BindProperty] public TypeForm Input { get; set; } = new();

    [TempData] public string? Message { get; set; }

    public class TypeForm
    {
        [Required, StringLength(100)] public string Name { get; set; } = "";

        [Display(Name = "Tracked as")] public InventoryMode Mode { get; set; } = InventoryMode.Tagged;

        [Display(Name = "Vendor and model")] public bool UsesModel { get; set; } = true;
        [Display(Name = "Description")] public bool UsesDescription { get; set; } = true;
        [Display(Name = "Serial number")] public bool UsesSerialNumber { get; set; } = true;
        [Display(Name = "Condition")] public bool UsesCondition { get; set; } = true;

        [StringLength(100), Display(Name = "Tag pattern")] public string? TagPattern { get; set; }
    }

    public async Task OnGetAsync(CancellationToken ct) => Types = await catalog.ListTypesAsync(includeInactive: true, ct: ct);

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var type = await catalog.CreateTypeAsync(new TypeInput(Input.Name, Input.Mode, Input.UsesModel,
                    Input.UsesDescription, Input.UsesSerialNumber, Input.UsesCondition, Input.TagPattern), ct);
                Message = type.Mode == InventoryMode.Tagged
                    ? $"Asset type {type.Name} created. Add its custom fields below."
                    : $"Asset type {type.Name} created.";
                return RedirectToPage("TypeEdit", new { id = type.Id });
            }
            catch (DomainException ex) { this.Show(ex); }
        }
        else Response.StatusCode = StatusCodes.Status400BadRequest;
        Types = await catalog.ListTypesAsync(includeInactive: true, ct: ct);
        return Page();
    }
}
