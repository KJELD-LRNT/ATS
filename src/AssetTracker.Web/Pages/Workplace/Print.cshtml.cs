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
/// engine); the people in the printed part are listed on the next page as one plain table (person, zone,
/// desktop), sorted by zone then name. A4 landscape by default, A3 landscape with <c>paper=a3</c>. Readable by everyone who can see
/// the floor.
/// </summary>
[Authorize(Policy = Policies.WorkplaceRead)]
public class PrintModel(FloorService floors, AssignmentService assignments, ICurrentUser user) : PageModel
{
    /// <summary>One desk of the printed part, for the header counts.</summary>
    public sealed record DeskRow(string? Person, bool IsFree);

    /// <summary>One line of the printed list: a person at a desk, its zone and the desktop placed there.</summary>
    public sealed record PersonRow(string Person, string? Zone, string? Desktop);

    public FloorView View { get; private set; } = null!;
    public string Svg { get; private set; } = "";
    public Area? PrintArea { get; private set; }
    public IReadOnlyList<DeskRow> Rows { get; private set; } = [];
    public IReadOnlyList<PersonRow> People { get; private set; } = [];
    public string PrintedBy => user.Name;
    public DateTimeOffset PrintedAt { get; } = DateTimeOffset.Now;

    /// <summary>What is printed, as the page asked for it ("Selected seats", "Zone Finance"...).</summary>
    [BindProperty(SupportsGet = true)] public string? What { get; set; }

    /// <summary>Open the print dialog as soon as the page is ready.</summary>
    [BindProperty(SupportsGet = true)] public bool Auto { get; set; }

    /// <summary>List the people of the printed part on a second page (default on).</summary>
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
        Svg = FloorplanRenderer.Render(view.Floor, view.Spots, view.Zones, states, PrintArea ?? FloorplanRenderer.Content(view.Floor, view.Spots), clipToViewBox: PrintArea is not null);

        var centers = FloorplanRenderer.Centers(view.Floor, view.Spots);
        var zoneNames = view.Zones.ToDictionary(t => t.Id, t => t.Name);
        string? ZoneOf(Workspot s) => s.ZoneId is { } t && zoneNames.TryGetValue(t, out var n) ? n : null;
        var spots = view.Spots
            .Where(s => (s.IsActive && s.IsDesk) || states.ContainsKey(s.Id))
            .Where(s => PrintArea is not { } a || (centers.TryGetValue(s.Id, out var c) && a.Contains(c.X, c.Y)))
            .ToList();
        Rows = spots
            .Select(s =>
            {
                states.TryGetValue(s.Id, out var st);
                return new DeskRow(st?.Person, st?.Person is null && s.IsActive && s.IsDesk);
            })
            .ToList();
        // Just who sits where: no notes, screens, rooms or free desks. Zone first (no zone last), then name.
        People = spots
            .Select(s => (Spot: s, State: states.TryGetValue(s.Id, out var st) ? st : null))
            .Where(x => x.State?.Person is not null)
            .Select(x => new PersonRow(x.State!.Person!, ZoneOf(x.Spot), x.State.Desktop))
            .OrderBy(r => r.Zone is null).ThenBy(r => r.Zone, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Person, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Page();
    }
}
