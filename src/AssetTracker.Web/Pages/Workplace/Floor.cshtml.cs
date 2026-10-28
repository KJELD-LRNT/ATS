using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using AssetTracker.Core.Workplace;
using AssetTracker.Web.Pages.Admin;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AssetTracker.Web.Pages.Workplace;

/// <summary>
/// One floor plan. Everyone who can read it can pan, zoom, find a seat, room, zone, person or desktop, and print (part
/// of) it. IT and facility drag people, desktops and screens onto desks, release them, make seats desks or not, give a
/// table (or part of one) a zone, switch seats off or mark shapes of a plain drawing as seats. Seats have no names of
/// their own: a desk is known by who sits there, its desktop, its zone and its room. The selection and dragging
/// happen in the browser; forms (or fetch, for drops, so the map keeps its view) post the ids.
/// </summary>
[Authorize(Policy = Policies.WorkplaceRead)]
public class FloorModel(FloorService floors, WorkspotService spots, ZoneService zones, AssignmentService assignments, AccessScope access) : PageModel
{
    private const int MaxIds = 2000;

    public FloorView View { get; private set; } = null!;
    public string Svg { get; private set; } = "";
    public IReadOnlyList<string> FocusIds { get; private set; } = [];
    public bool CanEdit => access.CanEditWorkplace;

    /// <summary>Whether the drawing declares its own seats; if not, shapes are marked by hand.</summary>
    public bool HasDeclaredSeats { get; private set; }

    public IReadOnlyDictionary<int, int> ZoneSeats { get; private set; } = new Dictionary<int, int>();

    /// <summary>Desks, who sits where, free desks and the seats that need a look.</summary>
    public FloorStatus Status { get; private set; } = new(0, 0, [], 0, 0, []);

    [BindProperty(SupportsGet = true)] public string? Focus { get; set; }

    [TempData] public string? Message { get; set; }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct) => await LoadAsync(id, ct) ? Page() : NotFound();

    /// <summary>The floor's stylesheet (scoped, sanitised on import); the CSP only allows styles from the app itself.</summary>
    public async Task<IActionResult> OnGetCssAsync(int id, CancellationToken ct)
    {
        var view = await floors.GetAsync(id, ct);
        return view is null ? NotFound() : Content(view.Floor.Css, "text/css");
    }

    /// <summary>The map alone (after a drop the page swaps it in and keeps its zoom).</summary>
    public async Task<IActionResult> OnGetMapAsync(int id, CancellationToken ct) =>
        await LoadAsync(id, ct) ? Partial("_FloorMap", Svg) : NotFound();

    /// <summary>The flags, free desks and summary (swapped in after a drop).</summary>
    public async Task<IActionResult> OnGetStatusAsync(int id, CancellationToken ct) =>
        await LoadAsync(id, ct) ? Partial("_FloorStatus", this) : NotFound();

    /// <summary>What sits at one workspot (JSON for the side panel).</summary>
    public async Task<IActionResult> OnGetSpotAsync(int id, int workspotId, CancellationToken ct)
    {
        var detail = await assignments.GetSpotAsync(workspotId, ct);
        return detail is null || detail.FloorId != id ? NotFound() : new JsonResult(detail);
    }

    /// <summary>People, desktops and screens matching the text, to place at the selected desk (JSON, IT and facility).</summary>
    public async Task<IActionResult> OnGetPlaceAsync(int id, string? q, CancellationToken ct) =>
        new JsonResult(await assignments.SearchAsync(id, q, ct));

    /// <summary>The drag-and-drop list: people, desktops or screens that are not placed yet (JSON, IT and facility).</summary>
    public async Task<IActionResult> OnGetPaletteAsync(int id, string? kind, string? q, CancellationToken ct) =>
        new JsonResult(await assignments.PaletteAsync(id, kind ?? "person", q, ct));

    /// <summary>
    /// The suggestions of one slot of the desk view (<c>kind</c>: person, desktop or screen) for a desk: with an empty
    /// <paramref name="q"/> what is not placed yet at this site, else a search, ranked (JSON, IT and facility).
    /// </summary>
    public async Task<IActionResult> OnGetDeskSearchAsync(int id, int workspotId, string? kind, string? q, CancellationToken ct) =>
        new JsonResult(await assignments.DeskSearchAsync(id, workspotId, kind ?? "person", q, ct));

    /// <summary>Replaces the person or the desktop of a desk in one step (desk view, IT and facility).</summary>
    public Task<IActionResult> OnPostReplaceAsync(int id, int workspotId, string? kind, int targetId, CancellationToken ct) =>
        RunAsync(id, () => assignments.ReplaceAsync(workspotId, kind ?? "", targetId, ct), ct, $"ws:{workspotId}");

    /// <summary>Places a person (<c>person</c>) or a desktop or screen (<c>desktop</c>, <c>screen</c>, <c>asset</c>) at a desk.</summary>
    public Task<IActionResult> OnPostAssignAsync(int id, int workspotId, string? kind, int targetId, CancellationToken ct) =>
        RunAsync(id, () => kind switch
        {
            "person" => assignments.AssignPersonAsync(workspotId, targetId, ct),
            "desktop" or "screen" or "asset" => assignments.AssignAssetAsync(workspotId, targetId, ct),
            _ => throw new DomainException("assignment.kind", "Choose a person, a desktop or a screen."),
        }, ct, $"ws:{workspotId}");

    public Task<IActionResult> OnPostReleaseAsync(int id, int assignmentId, int workspotId, CancellationToken ct) =>
        RunAsync(id, () => assignments.ReleaseAsync(assignmentId, ct), ct, $"ws:{workspotId}");

    /// <summary>Makes the selected seats desks (<paramref name="desk"/> true) or ordinary seats.</summary>
    public Task<IActionResult> OnPostDeskAsync(int id, string? ids, bool desk, CancellationToken ct) =>
        RunAsync(id, async () => $"{await spots.SetDeskAsync(Ids(ids), desk, ct)} seat(s) {(desk ? "made desks" : "are no longer desks")}.", ct, FocusOn(ids));

    /// <summary>
    /// Gives the selected seats (a table, part of a table, a room) a zone: an existing one (<paramref name="zone"/> is its id),
    /// a new one (<c>new</c>, named <paramref name="name"/>) or none (<c>none</c>). A table is split between zones by
    /// giving some of its seats another zone.
    /// </summary>
    public Task<IActionResult> OnPostSetZoneAsync(int id, string? ids, string? zone, string? name, CancellationToken ct)
    {
        var seats = Ids(ids);
        return RunAsync(id, async () => zone switch
        {
            "none" => $"{await zones.RemoveAsync(seats, ct)} seat(s) taken out of their zone.",
            "new" => $"Zone '{(await zones.CreateAsync(name ?? "", seats, ct)).Name}' created with {seats.Count} seat(s).",
            _ when int.TryParse(zone, out var zoneId) => $"{await zones.AddAsync(zoneId, seats, ct)} seat(s) added to the zone.",
            _ => throw new DomainException("zone.choose", "Choose a zone, a new zone or no zone."),
        }, ct, FocusOn(ids));
    }

    public Task<IActionResult> OnPostZoneRenameAsync(int id, int zoneId, string? name, CancellationToken ct) =>
        RunAsync(id, async () => { await zones.RenameAsync(zoneId, name ?? "", ct); return "Zone renamed."; }, ct);

    public Task<IActionResult> OnPostZoneColorAsync(int id, int zoneId, int color, CancellationToken ct) =>
        RunAsync(id, async () => { await zones.SetColorAsync(zoneId, color, ct); return "Colour changed."; }, ct);

    public Task<IActionResult> OnPostZoneDeleteAsync(int id, int zoneId, CancellationToken ct) =>
        RunAsync(id, async () => { await zones.DeleteAsync(zoneId, ct); return "Zone deleted; its workspots stay on the floor."; }, ct);

    public Task<IActionResult> OnPostRetireAsync(int id, string? ids, CancellationToken ct) =>
        RunAsync(id, async () => $"{await spots.SetActiveAsync(Ids(ids), false, ct)} seat(s) switched off.", ct, FocusOn(ids));

    public Task<IActionResult> OnPostRestoreAsync(int id, string? ids, CancellationToken ct) =>
        RunAsync(id, async () => $"{await spots.SetActiveAsync(Ids(ids), true, ct)} seat(s) switched on.", ct, FocusOn(ids));

    public Task<IActionResult> OnPostMarkAsync(int id, string? ids, CancellationToken ct) =>
        RunAsync(id, async () =>
        {
            var elements = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(MaxIds).ToList();
            return $"{await spots.MarkAsync(id, elements, ct)} workspot(s) marked.";
        }, ct);

    public Task<IActionResult> OnPostRenameFloorAsync(int id, string? name, CancellationToken ct) =>
        RunAsync(id, async () => { await floors.RenameAsync(id, name ?? "", ct); return "Floor renamed."; }, ct);

    public Task<IActionResult> OnPostArchiveAsync(int id, CancellationToken ct) =>
        RunAsync(id, async () => { await floors.SetActiveAsync(id, false, ct); return "Floor archived. It is hidden for everyone but IT and facility."; }, ct);

    public Task<IActionResult> OnPostUnarchiveAsync(int id, CancellationToken ct) =>
        RunAsync(id, async () => { await floors.SetActiveAsync(id, true, ct); return "Floor is visible again."; }, ct);

    /// <summary>
    /// Runs a change. A form post gets a redirect (with the changed seats selected again, <paramref name="focus"/>); a
    /// fetch that asks for JSON (a drop on the map, a release in the panel) gets <c>{ message }</c> or, for a rule that is
    /// broken, the message with a 400/409 status.
    /// </summary>
    private async Task<IActionResult> RunAsync(int id, Func<Task<string>> action, CancellationToken ct, string? focus = null)
    {
        if (!CanEdit) return NotFound();
        var wantsJson = Request.Headers.Accept.Any(a => a?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true);
        try
        {
            var message = await action();
            if (wantsJson) return new JsonResult(new { message });
            Message = message;
            return RedirectToPage(new { id, focus });
        }
        catch (DomainException ex)
        {
            if (wantsJson)
                return new JsonResult(new { message = ex.Message }) { StatusCode = ex is ConflictException ? 409 : 400 };
            this.Show(ex);
        }
        return await LoadAsync(id, ct) ? Page() : NotFound();
    }

    private async Task<bool> LoadAsync(int id, CancellationToken ct)
    {
        var view = await floors.GetAsync(id, ct);
        if (view is null) return false;
        View = view;
        var states = await assignments.StatesAsync(view.Spots, ct);
        Svg = FloorplanRenderer.Render(view.Floor, view.Spots, view.Zones, states);
        Status = await assignments.StatusAsync(view.Spots, ct);
        HasDeclaredSeats = view.Spots.Any(s => s.RoomId is not null || s.TableId is not null) ||
                           view.Floor.Svg.Contains("data-kind=\"seat\"", StringComparison.Ordinal);
        FocusIds = await spots.ElementIdsForFocusAsync(id, Focus, ct);
        ZoneSeats = view.ActiveSpots.Where(s => s.ZoneId != null).GroupBy(s => s.ZoneId!.Value).ToDictionary(g => g.Key, g => g.Count());
        return true;
    }

    /// <summary>The <c>focus</c> that selects these seats again after the redirect (none for a very large selection).</summary>
    private static string? FocusOn(string? ids) =>
        Ids(ids) is { Count: > 0 and <= WorkspotService.FocusMax } list ? "ws:" + string.Join(',', list) : null;

    private static List<int> Ids(string? ids) =>
        (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).Take(MaxIds).ToList();
}
