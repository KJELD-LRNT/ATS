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
            TempData["Message"] = $"Drawing replaced: {r.Added} seat(s) added, {r.Reactivated} back, {r.Deactivated} switched off" +
                (r.Moved > 0 ? $", {r.Moved} found again under a new id or in a slightly different place" : "") + "." +
                (r.Orphaned > 0 ? $" {r.Orphaned} seat(s) that are gone still had someone or something placed: they are flagged under To check; drag them to their new desk." : "");
            return RedirectToPage("Floor", new { id });
        }
        catch (DomainException ex) { this.Show(ex); }
        return Page();
    }
}
