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
/// One floor plan: pan and zoom, find a seat, room or team, select seats and (IT and facility) rename them, group them
/// into a team, switch them off or mark shapes of a plain drawing as seats. The selection is made in the browser; the
/// forms post the selected ids.
/// </summary>
[Authorize(Policy = Policies.WorkplaceRead)]
public class FloorModel(FloorService floors, WorkspotService spots, TeamService teams, AccessScope access) : PageModel
{
    private const int MaxIds = 2000;

    public FloorView View { get; private set; } = null!;
    public string Svg { get; private set; } = "";
    public IReadOnlyList<string> FocusIds { get; private set; } = [];
    public bool CanEdit => access.CanEditWorkplace;

    /// <summary>Whether the drawing declares its own seats; if not, shapes are marked by hand.</summary>
    public bool HasDeclaredSeats { get; private set; }

    public IReadOnlyDictionary<int, int> TeamSeats { get; private set; } = new Dictionary<int, int>();
    public IReadOnlyList<(string Type, int Count)> RoomTypes { get; private set; } = [];

    [BindProperty(SupportsGet = true)] public string? Focus { get; set; }

    [TempData] public string? Message { get; set; }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken ct) => await LoadAsync(id, ct) ? Page() : NotFound();

    /// <summary>The floor's stylesheet (scoped, sanitised on import); the CSP only allows styles from the app itself.</summary>
    public async Task<IActionResult> OnGetCssAsync(int id, CancellationToken ct)
    {
        var view = await floors.GetAsync(id, ct);
        return view is null ? NotFound() : Content(view.Floor.Css, "text/css");
    }

    public Task<IActionResult> OnPostLabelAsync(int id, int workspotId, string? label, CancellationToken ct) =>
        RunAsync(id, async () => { await spots.RenameAsync(workspotId, label ?? "", ct); return "Label saved."; }, ct);

    public Task<IActionResult> OnPostTeamAsync(int id, string? name, string? ids, CancellationToken ct) =>
        RunAsync(id, async () =>
        {
            var team = await teams.CreateAsync(name ?? "", Ids(ids), ct);
            return $"Team '{team.Name}' created.";
        }, ct);

    public Task<IActionResult> OnPostTeamAddAsync(int id, int teamId, string? ids, CancellationToken ct) =>
        RunAsync(id, async () => $"{await teams.AddAsync(teamId, Ids(ids), ct)} workspot(s) added to the team.", ct);

    public Task<IActionResult> OnPostTeamRemoveAsync(int id, string? ids, CancellationToken ct) =>
        RunAsync(id, async () => $"{await teams.RemoveAsync(Ids(ids), ct)} workspot(s) taken out of their team.", ct);

    public Task<IActionResult> OnPostTeamRenameAsync(int id, int teamId, string? name, CancellationToken ct) =>
        RunAsync(id, async () => { await teams.RenameAsync(teamId, name ?? "", ct); return "Team renamed."; }, ct);

    public Task<IActionResult> OnPostTeamColorAsync(int id, int teamId, int color, CancellationToken ct) =>
        RunAsync(id, async () => { await teams.SetColorAsync(teamId, color, ct); return "Colour changed."; }, ct);

    public Task<IActionResult> OnPostTeamDeleteAsync(int id, int teamId, CancellationToken ct) =>
        RunAsync(id, async () => { await teams.DeleteAsync(teamId, ct); return "Team deleted; its workspots stay on the floor."; }, ct);

    public Task<IActionResult> OnPostRetireAsync(int id, string? ids, CancellationToken ct) =>
        RunAsync(id, async () => $"{await spots.SetActiveAsync(Ids(ids), false, ct)} workspot(s) switched off.", ct);

    public Task<IActionResult> OnPostRestoreAsync(int id, string? ids, CancellationToken ct) =>
        RunAsync(id, async () => $"{await spots.SetActiveAsync(Ids(ids), true, ct)} workspot(s) switched on.", ct);

    public Task<IActionResult> OnPostMarkAsync(int id, string? ids, string? prefix, CancellationToken ct) =>
        RunAsync(id, async () =>
        {
            var elements = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(MaxIds).ToList();
            return $"{await spots.MarkAsync(id, elements, prefix, ct)} workspot(s) marked.";
        }, ct);

    public Task<IActionResult> OnPostRenameFloorAsync(int id, string? name, CancellationToken ct) =>
        RunAsync(id, async () => { await floors.RenameAsync(id, name ?? "", ct); return "Floor renamed."; }, ct);

    public Task<IActionResult> OnPostArchiveAsync(int id, CancellationToken ct) =>
        RunAsync(id, async () => { await floors.SetActiveAsync(id, false, ct); return "Floor archived. It is hidden for everyone but IT and facility."; }, ct);

    public Task<IActionResult> OnPostUnarchiveAsync(int id, CancellationToken ct) =>
        RunAsync(id, async () => { await floors.SetActiveAsync(id, true, ct); return "Floor is visible again."; }, ct);

    private async Task<IActionResult> RunAsync(int id, Func<Task<string>> action, CancellationToken ct)
    {
        if (!CanEdit) return NotFound();
        try
        {
            Message = await action();
            return RedirectToPage(new { id });
        }
        catch (DomainException ex) { this.Show(ex); }
        return await LoadAsync(id, ct) ? Page() : NotFound();
    }

    private async Task<bool> LoadAsync(int id, CancellationToken ct)
    {
        var view = await floors.GetAsync(id, ct);
        if (view is null) return false;
        View = view;
        Svg = FloorplanRenderer.Render(view.Floor, view.Spots, view.Teams);
        HasDeclaredSeats = view.Spots.Any(s => s.RoomId is not null || s.TableId is not null) ||
                           view.Floor.Svg.Contains("data-kind=\"seat\"", StringComparison.Ordinal);
        FocusIds = await spots.ElementIdsForFocusAsync(id, Focus, ct);
        TeamSeats = view.ActiveSpots.Where(s => s.TeamId != null).GroupBy(s => s.TeamId!.Value).ToDictionary(g => g.Key, g => g.Count());
        RoomTypes = view.ActiveSpots.GroupBy(s => s.RoomType ?? "").OrderBy(g => g.Key == "" ? 1 : 0).ThenBy(g => g.Key)
            .Select(g => (g.Key, g.Count())).ToList();
        return true;
    }

    private static List<int> Ids(string? ids) =>
        (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).Take(MaxIds).ToList();
}
