using AssetTracker.Core.Common;
using AssetTracker.Core.Overview;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Admin;

/// <summary>Admin: which overview tiles IT and managers see.</summary>
[Authorize(Policy = Policies.AdminOnly)]
public class OverviewTilesModel(OverviewTileService tiles) : PageModel
{
    /// <summary>Tile keys ticked for IT and for managers (checkboxes post their tile key).</summary>
    [BindProperty] public List<string> It { get; set; } = [];
    [BindProperty] public List<string> Manager { get; set; } = [];

    public IReadOnlyDictionary<string, AppRoles> Current { get; private set; } = new Dictionary<string, AppRoles>();

    [TempData] public string? Message { get; set; }

    public async Task OnGetAsync(CancellationToken ct) => Current = await tiles.GetRolesAsync(ct);

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        try
        {
            // Every tile is posted (unticked = off for that role); the service limits each to the roles it may be shown to.
            var roles = OverviewTiles.All.ToDictionary(t => t.Key, t =>
                (It.Contains(t.Key) ? AppRoles.It : AppRoles.None) | (Manager.Contains(t.Key) ? AppRoles.Manager : AppRoles.None));
            await tiles.SaveAsync(roles, ct);
            Message = "Overview tiles saved. They apply to the next page view.";
            return RedirectToPage();
        }
        catch (DomainException ex) { this.Show(ex); }
        Current = await tiles.GetRolesAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostResetAsync(CancellationToken ct)
    {
        await tiles.ResetAsync(ct);
        Message = "Overview tiles are back to their defaults.";
        return RedirectToPage();
    }
}
