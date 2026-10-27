using AssetTracker.Core.Security;
using AssetTracker.Core.Workplace;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Workplace;

/// <summary>
/// Workplace: the floor plans of the sites the principal may see, and one search box that finds a seat, a room or a
/// zone on any of them. IT and facility can import floor plans; managers see their granted sites read-only.
/// </summary>
[Authorize(Policy = Policies.WorkplaceRead)]
public class IndexModel(FloorService floors, WorkspotService spots, AccessScope access) : PageModel
{
    public IReadOnlyList<FloorSummary> Floors { get; private set; } = [];
    public bool CanEdit => access.CanEditWorkplace;

    public IEnumerable<IGrouping<string, FloorSummary>> BySite =>
        Floors.GroupBy(f => $"{f.SiteKey} – {f.SiteName}");

    public async Task OnGetAsync(CancellationToken ct) => Floors = await floors.ListAsync(ct);

    /// <summary>Suggestions for the search box (seats, rooms and zones on every floor the principal can see).</summary>
    public async Task<IActionResult> OnGetFindAsync(string? q, CancellationToken ct)
    {
        var hits = await spots.FindAsync(q, ct);
        return new JsonResult(hits.Select(h => new
        {
            kind = h.Kind, floorId = h.FloorId, floorName = h.FloorName, siteKey = h.SiteKey, title = h.Title,
            subtitle = h.Subtitle, focus = h.Focus, seats = h.Seats,
        }));
    }
}
