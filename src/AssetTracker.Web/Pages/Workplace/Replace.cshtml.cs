using AssetTracker.Core.Common;
using AssetTracker.Core.Security;
using AssetTracker.Core.Workplace;
using AssetTracker.Web.Pages.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Workplace;

/// <summary>Replace the drawing of a floor with a revised one; seats are matched on their element id.</summary>
[Authorize(Policy = Policies.WorkplaceEdit)]
[RequestSizeLimit(FloorplanImporter.MaxBytes + 512 * 1024)]
[RequestFormLimits(MultipartBodyLengthLimit = FloorplanImporter.MaxBytes + 512 * 1024)]
public class ReplaceModel(FloorService floors) : PageModel
{
    public FloorView? View { get; private set; }

    [BindProperty] public IFormFile? Drawing { get; set; }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct)
    {
        View = await floors.GetAsync(id, ct);
        return View is null ? NotFound() : Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, CancellationToken ct)
    {
        View = await floors.GetAsync(id, ct);
        if (View is null) return NotFound();
        try
        {
            var xml = await ImportModel.ReadAsync(Drawing, ct);
            var r = await floors.ReplaceDrawingAsync(id, xml, ct);
            TempData["Message"] = $"Drawing replaced: {r.Added} workspot(s) added, {r.Reactivated} switched back on, {r.Deactivated} switched off.";
            return RedirectToPage("Floor", new { id });
        }
        catch (DomainException ex) { this.Show(ex); }
        return Page();
    }
}
