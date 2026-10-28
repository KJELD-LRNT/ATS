using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using AssetTracker.Core.Workplace;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Workplace;

/// <summary>
/// A floor plan, or a part of it (<c>area=x,y,width,height</c> in drawing units), laid out for the browser's print
/// dialog: "Save as PDF" makes the PDF. Names and desktop tags are always shown (site.js fits them, see the label
/// engine); the desks in the printed part are listed on the next pages, grouped by zone and room, with the free desks
/// of a room on one line. A4 landscape by default, A3 landscape with <c>paper=a3</c>. Readable by everyone who can see
/// the floor.
/// </summary>
[Authorize(Policy = Policies.WorkplaceRead)]
public class PrintModel(FloorService floors, AssignmentService assignments, ICurrentUser user) : PageModel
{
    public sealed record DeskRow(string? Zone, string? Room, string? Person, bool PersonLeft, string? Desktop, int Screens, string? Note)
    {
        /// <summary>A free desk with nothing placed and nothing to check: listed only as a count per room.</summary>
        public bool IsPlainFree => Note == "Free" && Desktop is null && Screens == 0;
    }

    /// <summary>The desks of one room in one zone: the listed ones and how many plain free desks there are.</summary>
    public sealed record RoomGroup(string? Room, IReadOnlyList<DeskRow> Desks, int Free);

    /// <summary>The desks of one zone (null: desks in no zone), room by room.</summary>
    public sealed record ZoneGroup(string? Zone, IReadOnlyList<RoomGroup> Rooms);

    public FloorView View { get; private set; } = null!;
    public string Svg { get; private set; } = "";
    public Area? PrintArea { get; private set; }
    public IReadOnlyList<DeskRow> Rows { get; private set; } = [];
    public IReadOnlyList<ZoneGroup> Groups { get; private set; } = [];
    public string PrintedBy => user.Name;
    public DateTimeOffset PrintedAt { get; } = DateTimeOffset.Now;

    /// <summary>What is printed, as the page asked for it ("Selected seats", "Zone Finance"...).</summary>
    [BindProperty(SupportsGet = true)] public string? What { get; set; }

    /// <summary>Open the print dialog as soon as the page is ready.</summary>
    [BindProperty(SupportsGet = true)] public bool Auto { get; set; }

    /// <summary>List the desks of the printed part on a second page (default on).</summary>
    [BindProperty(SupportsGet = true)] public bool List { get; set; } = true;

    /// <summary>The paper: <c>a4</c> (default) or <c>a3</c>, both landscape.</summary>
    [BindProperty(SupportsGet = true)] public string? Paper { get; set; }

    public bool IsA3 => string.Equals(Paper, "a3", StringComparison.OrdinalIgnoreCase);
    public string PaperName => IsA3 ? "A3" : "A4";

    /// <summary>The query string of this page with another paper size (and without auto-printing).</summary>
    public IDictionary<string, string> RouteFor(string paper)
    {
        var values = new Dictionary<string, string> { ["id"] = View.Floor.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (PrintArea is { } a) values["area"] = string.Join(',', new[] { a.X, a.Y, a.Width, a.Height }.Select(v => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)));
        if (What is not null) values["what"] = What;
        if (!List) values["list"] = "false";
        if (paper != "a4") values["paper"] = paper;
        return values;
    }

    public async Task<IActionResult> OnGetAsync(int id, string? area, CancellationToken ct)
    {
        var view = await floors.GetAsync(id, ct);
        if (view is null) return NotFound();
        View = view;
        PrintArea = Area.Parse(area);
        if (What is { Length: > 80 }) What = What[..80];

        var states = await assignments.StatesAsync(view.Spots, ct);
        // The whole floor prints the plan itself, not the empty paper around it in the drawing.
        Svg = FloorplanRenderer.Render(view.Floor, view.Spots, view.Zones, states, PrintArea ?? FloorplanRenderer.Content(view.Floor, view.Spots));

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
        Groups = Rows.GroupBy(r => r.Zone)
            .Select(z => new ZoneGroup(z.Key, z.GroupBy(r => r.Room)
                .Select(room => new RoomGroup(room.Key, room.Where(r => !r.IsPlainFree).ToList(), room.Count(r => r.IsPlainFree)))
                .ToList()))
            .ToList();
        return Page();
    }
}
