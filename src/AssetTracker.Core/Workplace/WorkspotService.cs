using System.Text.RegularExpressions;
using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Auditing;
using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Workplace;

/// <param name="Kind"><c>workspot</c>, <c>room</c> or <c>team</c>.</param>
/// <param name="Focus">What to highlight on the floor: <c>ws:12</c>, <c>room:ROOMID</c> or <c>team:3</c>.</param>
public sealed record WorkplaceHit(string Kind, int FloorId, string FloorName, string SiteKey, string Title, string? Subtitle,
    string Focus, int Seats);

/// <summary>
/// Workspots (seats) of a floor: renaming, marking shapes of a plain drawing as seats, switching seats off and on, and
/// the search that finds a seat, a room or a team on any floor the principal may see.
/// </summary>
public partial class WorkspotService(AppDbContext db, AccessScope scope, AuditWriter audit, IClock clock)
{
    public const int LabelMax = 100;
    public const int MarkMax = 500;
    public const int FindLimit = 8;

    [GeneratedRegex(@"\sid=""([^""]+)""")]
    private static partial Regex IdAttribute();

    [GeneratedRegex(@"<(rect|circle|ellipse|polygon|path|polyline|line|g)\b[^>]*?\sid=""([^""]+)""")]
    private static partial Regex ShapeWithId();

    public async Task RenameAsync(int workspotId, string label, CancellationToken ct = default)
    {
        scope.EnsureWorkplaceEditor();
        label = (label ?? "").Trim();
        if (label.Length == 0) throw new DomainException("workspot.label", "A workspot needs a label.");
        if (label.Length > LabelMax) throw new DomainException("workspot.label", $"The label can have at most {LabelMax} characters.");
        var spot = await db.Workspots.Include(w => w.Floor).SingleOrDefaultAsync(w => w.Id == workspotId, ct)
                   ?? throw new DomainException("workspot.not-found", "Workspot not found.");
        if (spot.Label == label) return;
        var taken = await db.Workspots.AnyAsync(w => w.FloorId == spot.FloorId && w.Id != spot.Id && w.Label == label, ct);
        if (taken) throw new ConflictException("workspot.label-taken", $"Another workspot on this floor is already called '{label}'.");
        var before = spot.Label;
        spot.Label = label;
        audit.Record("workspot.rename", nameof(Workspot), spot.Id, spot.Floor!.SiteId,
            details: new { FloorId = spot.FloorId, spot.ElementId, Before = before, After = label });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Marks shapes of the drawing as seats (for a drawing that does not declare its own). With a prefix the new seats
    /// are labelled "prefix1", "prefix2"... in the order given; without one the label is the element id.
    /// </summary>
    public async Task<int> MarkAsync(int floorId, IReadOnlyCollection<string> elementIds, string? labelPrefix, CancellationToken ct = default)
    {
        scope.EnsureWorkplaceEditor();
        var ids = elementIds.Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0) throw new DomainException("workspot.none", "Select at least one shape in the drawing.");
        if (ids.Count > MarkMax) throw new DomainException("workspot.too-many", $"Mark at most {MarkMax} shapes at a time.");
        var floor = await db.Floors.SingleOrDefaultAsync(f => f.Id == floorId, ct)
                    ?? throw new DomainException("floor.not-found", "Floor not found.");

        var shapes = ShapeWithId().Matches(floor.Svg)
            .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[2].Value)).ToHashSet(StringComparer.Ordinal);
        if (ids.Any(i => !shapes.Contains(i)))
            throw new DomainException("workspot.shape", "One of the selected shapes is not part of this drawing.");

        var existing = await db.Workspots.Where(w => w.FloorId == floorId).ToListAsync(ct);
        var byElement = existing.ToDictionary(w => w.ElementId, StringComparer.Ordinal);
        var labels = new HashSet<string>(existing.Select(w => w.Label), StringComparer.OrdinalIgnoreCase);
        var prefix = (labelPrefix ?? "").Trim();
        if (prefix.Length > 40) throw new DomainException("workspot.label", "The label prefix can have at most 40 characters.");
        var next = 1;
        var now = clock.UtcNow;
        var changed = 0;

        foreach (var id in ids)
        {
            if (byElement.TryGetValue(id, out var spot))
            {
                if (!spot.IsActive) { spot.IsActive = true; changed++; }
                continue;
            }
            string label;
            if (prefix.Length > 0)
            {
                while (labels.Contains(prefix + next)) next++;
                label = prefix + next;
                labels.Add(label);
            }
            else
            {
                label = FloorService.UniqueLabel(id.Length > LabelMax ? id[..LabelMax] : id, labels);
            }
            db.Workspots.Add(new Workspot { FloorId = floorId, ElementId = id, Label = label, IsActive = true, CreatedAt = now });
            changed++;
        }
        if (changed == 0) return 0;
        audit.Record("workspot.mark", nameof(Floor), floor.Id, floor.SiteId, details: new { floor.Name, Count = changed, Prefix = prefix });
        await db.SaveChangesAsync(ct);
        return changed;
    }

    /// <summary>Switches seats off (a seat that is not used or gone) or back on. Nothing is deleted.</summary>
    public async Task<int> SetActiveAsync(IReadOnlyCollection<int> workspotIds, bool active, CancellationToken ct = default)
    {
        scope.EnsureWorkplaceEditor();
        var spots = await LoadSameFloorAsync(workspotIds, ct);
        var changing = spots.Where(s => s.IsActive != active).ToList();
        if (changing.Count == 0) return 0;
        foreach (var s in changing)
        {
            s.IsActive = active;
            if (!active) s.TeamId = null;
        }
        var floor = changing[0].Floor!;
        audit.Record(active ? "workspot.restore" : "workspot.retire", nameof(Floor), floor.Id, floor.SiteId,
            details: new { floor.Name, Workspots = changing.Select(s => s.Label).ToList() });
        await db.SaveChangesAsync(ct);
        return changing.Count;
    }

    internal async Task<List<Workspot>> LoadSameFloorAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0) throw new DomainException("workspot.none", "Select at least one workspot.");
        var spots = await db.Workspots.Include(w => w.Floor).Where(w => wanted.Contains(w.Id)).ToListAsync(ct);
        if (spots.Count != wanted.Count) throw new DomainException("workspot.not-found", "A selected workspot no longer exists.");
        if (spots.Select(s => s.FloorId).Distinct().Count() > 1)
            throw new DomainException("workspot.floors", "Select workspots on one floor only.");
        return spots;
    }

    /// <summary>Seats, rooms and teams matching the text, on every floor the principal can see (at least two characters).</summary>
    public async Task<IReadOnlyList<WorkplaceHit>> FindAsync(string? term, CancellationToken ct = default)
    {
        term = term?.Trim();
        if (string.IsNullOrEmpty(term) || term.Length < 2) return [];
        var access = await scope.GetWorkplaceAsync(ct);
        if (!access.CanReadInventory) return [];
        var pattern = "%" + term.ToLowerInvariant().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[") + "%";
        var ids = access.SiteIds.ToList();
        var floors = db.Floors.AsNoTracking().Where(f => f.IsActive && (access.AllSites || ids.Contains(f.SiteId)));

        var spots = await (
            from w in db.Workspots.AsNoTracking()
            join f in floors on w.FloorId equals f.Id
            where w.IsActive && (EF.Functions.Like(w.Label.ToLower(), pattern, "\\") || EF.Functions.Like(w.ElementId.ToLower(), pattern, "\\"))
            orderby f.Site!.Key, f.Name, w.Label
            select new { w.Id, w.Label, w.RoomName, w.RoomId, FloorId = f.Id, FloorName = f.Name, SiteKey = f.Site!.Key, w.TeamId })
            .Take(FindLimit).ToListAsync(ct);

        var rooms = await (
            from w in db.Workspots.AsNoTracking()
            join f in floors on w.FloorId equals f.Id
            where w.IsActive && w.RoomId != null &&
                  (EF.Functions.Like(w.RoomId.ToLower(), pattern, "\\") || (w.RoomName != null && EF.Functions.Like(w.RoomName.ToLower(), pattern, "\\")))
            group w by new { FloorId = f.Id, FloorName = f.Name, SiteKey = f.Site!.Key, w.RoomId, w.RoomName } into g
            orderby g.Key.SiteKey, g.Key.FloorName, g.Key.RoomId
            select new { g.Key.FloorId, g.Key.FloorName, g.Key.SiteKey, g.Key.RoomId, g.Key.RoomName, Seats = g.Count() })
            .Take(FindLimit).ToListAsync(ct);

        var teams = await (
            from t in db.FloorTeams.AsNoTracking()
            join f in floors on t.FloorId equals f.Id
            where EF.Functions.Like(t.Name.ToLower(), pattern, "\\")
            orderby f.Site!.Key, f.Name, t.Name
            select new { t.Id, t.Name, FloorId = f.Id, FloorName = f.Name, SiteKey = f.Site!.Key,
                Seats = db.Workspots.Count(w => w.TeamId == t.Id && w.IsActive) })
            .Take(FindLimit).ToListAsync(ct);

        var hits = new List<WorkplaceHit>();
        hits.AddRange(teams.Select(t => new WorkplaceHit("team", t.FloorId, t.FloorName, t.SiteKey, t.Name, $"Team · {t.Seats} seats", $"team:{t.Id}", t.Seats)));
        hits.AddRange(rooms.Select(r => new WorkplaceHit("room", r.FloorId, r.FloorName, r.SiteKey,
            r.RoomName is { Length: > 0 } ? $"{r.RoomName} ({r.RoomId})" : r.RoomId!, $"Room · {r.Seats} seats", $"room:{r.RoomId}", r.Seats)));
        hits.AddRange(spots.Select(s => new WorkplaceHit("workspot", s.FloorId, s.FloorName, s.SiteKey, s.Label,
            s.RoomName ?? s.RoomId, $"ws:{s.Id}", 1)));
        return hits;
    }

    /// <summary>The element ids to highlight for a <c>focus</c> value (<c>ws:12</c>, <c>room:ID</c>, <c>team:3</c>) on a floor.</summary>
    public async Task<IReadOnlyList<string>> ElementIdsForFocusAsync(int floorId, string? focus, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(focus) || focus.IndexOf(':') is var c && c <= 0) return [];
        var kind = focus[..c];
        var value = focus[(c + 1)..];
        var spots = db.Workspots.AsNoTracking().Where(w => w.FloorId == floorId && w.IsActive);
        switch (kind)
        {
            case "ws" when int.TryParse(value, out var id):
                return await spots.Where(w => w.Id == id).Select(w => w.ElementId).ToListAsync(ct);
            case "room":
                return await spots.Where(w => w.RoomId == value).Select(w => w.ElementId).ToListAsync(ct);
            case "team" when int.TryParse(value, out var teamId):
                return await spots.Where(w => w.TeamId == teamId).Select(w => w.ElementId).ToListAsync(ct);
            default:
                return [];
        }
    }
}
