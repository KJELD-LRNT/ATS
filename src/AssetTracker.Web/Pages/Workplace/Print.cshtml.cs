using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using AssetTracker.Core.Workplace;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Workplace;

/// <summary>
/// A floor plan, or a part of it (<c>area=x,y,width,height</c> in drawing units), laid out for the browser's print
/// dialog: "Save as PDF" makes the PDF. Names and desktop tags are always shown; the desks in the printed part are
/// listed on a second page. Readable by everyone who can see the floor.
/// </summary>
[Authorize(Policy = Policies.WorkplaceRead)]
public class PrintModel(FloorService floors, AssignmentService assignments, ICurrentUser user) : PageModel
{
    public sealed record DeskRow(string? Zone, string? Room, string? Person, bool PersonLeft, string? Desktop, int Screens, string? Note);

    public FloorView View { get; private set; } = null!;
    public string Svg { get; private set; } = "";
    public Area? PrintArea { get; private set; }
    public IReadOnlyList<DeskRow> Rows { get; private set; } = [];
    public string PrintedBy => user.Name;
    public DateTimeOffset PrintedAt { get; } = DateTimeOffset.Now;

    /// <summary>What is printed, as the page asked for it ("Selected seats", "Zone Finance"...).</summary>
    [BindProperty(SupportsGet = true)] public string? What { get; set; }

    /// <summary>Open the print dialog as soon as the page is ready.</summary>
    [BindProperty(SupportsGet = true)] public bool Auto { get; set; }

    /// <summary>List the desks of the printed part on a second page (default on).</summary>
    [BindProperty(SupportsGet = true)] public bool List { get; set; } = true;

    public async Task<IActionResult> OnGetAsync(int id, string? area, CancellationToken ct)
    {
        var view = await floors.GetAsync(id, ct);
        if (view is null) return NotFound();
        View = view;
        PrintArea = Area.Parse(area);
        if (What is { Length: > 80 }) What = What[..80];

        var states = await assignments.StatesAsync(view.Spots, ct);
        Svg = FloorplanRenderer.Render(view.Floor, view.Spots, view.Zones, states, PrintArea);

        var status = await assignments.StatusAsync(view.Spots, ct);
        var flags = status.Flags.ToLookup(f => f.WorkspotId);
        var centers = FloorplanRenderer.Centers(view.Floor, view.Spots);
        var zoneNames = view.Zones.ToDictionary(t => t.Id, t => t.Name);
        string? ZoneOf(Workspot s) => s.ZoneId is { } t && zoneNames.TryGetValue(t, out var n) ? n : null;
        Rows = view.Spots
            .Where(s => (s.IsActive && s.IsDesk) || states.ContainsKey(s.Id))
            .Where(s => PrintArea is not { } a || (centers.TryGetValue(s.Id, out var c) && a.Contains(c.X, c.Y)))
            // By zone and room; within a room the drawing's order (its seat ids), which is never printed.
            .OrderBy(s => ZoneOf(s) is null).ThenBy(s => ZoneOf(s), StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.RoomName ?? s.RoomId, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Label, StringComparer.OrdinalIgnoreCase)
            .Select(s =>
            {
                states.TryGetValue(s.Id, out var st);
                var note = flags[s.Id].Any() ? string.Join(" ", flags[s.Id].Select(f => f.Text))
                    : st?.Person is null && s.IsActive && s.IsDesk ? "Free" : null;
                return new DeskRow(ZoneOf(s), s.RoomName ?? s.RoomId, st?.Person, st?.PersonLeft == true, st?.Desktop, st?.Screens ?? 0, note);
            })
            .ToList();
        return Page();
    }
}
