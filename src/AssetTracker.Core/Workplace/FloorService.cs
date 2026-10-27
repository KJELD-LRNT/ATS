using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Auditing;
using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Workplace;

public sealed record FloorSummary(int Id, int SiteId, string SiteKey, string SiteName, string Name, bool IsActive,
    int Spots, int Zones, DateTimeOffset ImportedAt);

public sealed record FloorView(Floor Floor, string SiteKey, string SiteName, IReadOnlyList<Workspot> Spots,
    IReadOnlyList<FloorZone> Zones)
{
    public IEnumerable<Workspot> ActiveSpots => Spots.Where(s => s.IsActive);
}

/// <param name="Added">New seats.</param>
/// <param name="Reactivated">Seats that had been deactivated and are in the drawing again.</param>
/// <param name="Deactivated">Seats whose element is no longer in the drawing (kept, switched off).</param>
/// <param name="Moved">Seats found again by their seat id or position because their element changed.</param>
/// <param name="Orphaned">People, desktops and screens left on seats that are no longer in the drawing (they are flagged).</param>
public sealed record ImportResult(Floor Floor, int Added, int Reactivated, int Deactivated, int Moved = 0, int Orphaned = 0);

/// <summary>
/// Floor plans: importing a drawing for a site, replacing it with a revised one, renaming and archiving. Everyone who
/// can look at the Workplace sees the floors of their sites (IT and facility all, managers their granted sites);
/// changing needs IT or facility. Every change is audited.
/// </summary>
public class FloorService(AppDbContext db, AccessScope scope, AuditWriter audit, ICurrentUser user, IClock clock)
{
    public const int NameMax = 100;

    public async Task<IReadOnlyList<FloorSummary>> ListAsync(CancellationToken ct = default)
    {
        var access = await scope.GetWorkplaceAsync(ct);
        if (!access.CanReadInventory) return [];
        var editor = scope.CanEditWorkplace;
        var floors = db.Floors.AsNoTracking().Where(f => editor || f.IsActive);
        if (!access.AllSites)
        {
            var ids = access.SiteIds.ToList();
            floors = floors.Where(f => ids.Contains(f.SiteId));
        }
        return await floors
            .OrderBy(f => f.Site!.Key).ThenBy(f => f.SortOrder).ThenBy(f => f.Name)
            .Select(f => new FloorSummary(f.Id, f.SiteId, f.Site!.Key, f.Site.Name, f.Name, f.IsActive,
                f.Workspots.Count(w => w.IsActive), f.Zones.Count, f.ImportedAt))
            .ToListAsync(ct);
    }

    /// <summary>The floor with its workspots and zones, or null when it does not exist or is out of the principal's scope.</summary>
    public async Task<FloorView?> GetAsync(int id, CancellationToken ct = default)
    {
        var floor = await db.Floors.AsNoTracking().Include(f => f.Site).SingleOrDefaultAsync(f => f.Id == id, ct);
        if (floor is null) return null;
        if (!(await scope.GetWorkplaceAsync(ct)).CanRead(floor.SiteId)) return null;
        if (!floor.IsActive && !scope.CanEditWorkplace) return null;
        var spots = await db.Workspots.AsNoTracking().Where(w => w.FloorId == id).OrderBy(w => w.Label).ToListAsync(ct);
        var zones = await db.FloorZones.AsNoTracking().Where(t => t.FloorId == id).OrderBy(t => t.Name).ToListAsync(ct);
        return new FloorView(floor, floor.Site!.Key, floor.Site.Name, spots, zones);
    }

    /// <summary>Sites a floor can be imported for (editors only; all active sites).</summary>
    public async Task<IReadOnlyList<Site>> SitesAsync(CancellationToken ct = default)
    {
        scope.EnsureWorkplaceEditor();
        return await db.Sites.AsNoTracking().Where(s => s.IsActive).OrderBy(s => s.Key).ToListAsync(ct);
    }

    public async Task<ImportResult> ImportAsync(int siteId, string name, string svgXml, CancellationToken ct = default)
    {
        scope.EnsureWorkplaceEditor();
        name = ValidName(name);
        var site = await db.Sites.AsNoTracking().SingleOrDefaultAsync(s => s.Id == siteId && s.IsActive, ct)
                   ?? throw new DomainException("floor.site", "Choose a site for this floor.");
        var existing = await db.Floors.Where(f => f.SiteId == siteId).Select(f => new { f.Name, f.SortOrder }).ToListAsync(ct);
        if (existing.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ConflictException("floor.exists", $"Site {site.Key} already has a floor called '{name}'. Use Replace drawing to update it.");

        var parsed = FloorplanImporter.Parse(svgXml);
        var now = clock.UtcNow;
        var floor = new Floor
        {
            SiteId = siteId, Name = name, SortOrder = existing.Count == 0 ? 1 : existing.Max(f => f.SortOrder) + 1,
            Svg = parsed.Svg, Css = parsed.Css, ViewBox = parsed.ViewBox, Version = 1, ImportedAt = now, ImportedBy = user.Name,
        };
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in parsed.Spots)
            floor.Workspots.Add(NewSpot(s, UniqueLabel(s.Label, labels), now));

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Floors.Add(floor);
        await db.SaveChangesAsync(ct);
        audit.Record("floor.import", nameof(Floor), floor.Id, siteId,
            details: new { floor.Name, Spots = floor.Workspots.Count, parsed.HasAutoIds, Bytes = svgXml.Length });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new ImportResult(floor, floor.Workspots.Count, 0, 0);
    }

    /// <summary>
    /// Replaces the drawing with a revised one. Seats are found again by element id, the drawing's seat id or position
    /// (<see cref="SeatMatcher"/>), so a re-made drawing keeps zones, placements and desk settings; new seats are added,
    /// seats that are back are switched on, seats that are gone are switched off (never deleted; whoever was placed there
    /// is flagged). Rooms and tables are read again; a seat whose room changed type is a desk or not by its new type, unless
    /// something is placed at it.
    /// </summary>
    public async Task<ImportResult> ReplaceDrawingAsync(int floorId, string svgXml, CancellationToken ct = default)
    {
        scope.EnsureWorkplaceEditor();
        var floor = await db.Floors.Include(f => f.Workspots).SingleOrDefaultAsync(f => f.Id == floorId, ct)
                    ?? throw new DomainException("floor.not-found", "Floor not found.");
        var parsed = FloorplanImporter.Parse(svgXml);
        var now = clock.UtcNow;
        var pairs = SeatMatcher.Match(floor.Workspots, floor.Svg, floor.ViewBox, parsed.Spots, parsed.Svg, parsed.ViewBox);
        var placed = (await db.WorkspotAssignments.Where(a => a.Workspot!.FloorId == floorId && a.ReleasedAt == null)
            .Select(a => a.WorkspotId).ToListAsync(ct)).ToHashSet();
        var labels = new HashSet<string>(floor.Workspots.Select(w => w.Label), StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<int>();
        int added = 0, reactivated = 0, deactivated = 0, moved = 0;

        foreach (var pair in pairs)
        {
            if (pair.Spot is not { } spot)
            {
                floor.Workspots.Add(NewSpot(pair.Parsed!, UniqueLabel(pair.Parsed!.Label, labels), now));
                added++;
                continue;
            }
            found.Add(spot.Id);
            if (pair.Moved) moved++;
            spot.ElementId = pair.ElementId;
            if (!spot.IsActive) { spot.IsActive = true; reactivated++; }
            if (pair.Parsed is not { } s) continue;
            if (s.Label != spot.Label && labels.Remove(spot.Label)) spot.Label = UniqueLabel(s.Label, labels);
            if (!string.Equals(s.RoomType, spot.RoomType, StringComparison.OrdinalIgnoreCase) && !placed.Contains(spot.Id))
                spot.IsDesk = Workspot.IsDeskRoomType(s.RoomType);
            spot.RoomId = s.RoomId; spot.RoomName = s.RoomName; spot.RoomType = s.RoomType; spot.TableId = s.TableId;
        }
        var orphaned = 0;
        foreach (var spot in floor.Workspots.Where(w => w.Id != 0 && !found.Contains(w.Id) && w.IsActive))
        {
            spot.IsActive = false; // its zone stays, for when a later drawing brings the seat back
            deactivated++;
            if (placed.Contains(spot.Id)) orphaned++;
        }

        floor.Svg = parsed.Svg; floor.Css = parsed.Css; floor.ViewBox = parsed.ViewBox;
        floor.Version++; floor.ImportedAt = now; floor.ImportedBy = user.Name;
        audit.Record("floor.replace", nameof(Floor), floor.Id, floor.SiteId,
            details: new { floor.Name, floor.Version, Added = added, Reactivated = reactivated, Deactivated = deactivated, Moved = moved, Orphaned = orphaned });
        await db.SaveChangesAsync(ct);
        return new ImportResult(floor, added, reactivated, deactivated, moved, orphaned);
    }

    public async Task RenameAsync(int floorId, string name, CancellationToken ct = default)
    {
        scope.EnsureWorkplaceEditor();
        name = ValidName(name);
        var floor = await db.Floors.SingleOrDefaultAsync(f => f.Id == floorId, ct)
                    ?? throw new DomainException("floor.not-found", "Floor not found.");
        if (floor.Name == name) return;
        var others = await db.Floors.Where(f => f.SiteId == floor.SiteId && f.Id != floorId).Select(f => f.Name).ToListAsync(ct);
        if (others.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            throw new ConflictException("floor.exists", $"This site already has a floor called '{name}'.");
        var before = floor.Name;
        floor.Name = name;
        audit.Record("floor.rename", nameof(Floor), floor.Id, floor.SiteId, details: new { Before = before, After = name });
        await db.SaveChangesAsync(ct);
    }

    public async Task SetActiveAsync(int floorId, bool active, CancellationToken ct = default)
    {
        scope.EnsureWorkplaceEditor();
        var floor = await db.Floors.SingleOrDefaultAsync(f => f.Id == floorId, ct)
                    ?? throw new DomainException("floor.not-found", "Floor not found.");
        if (floor.IsActive == active) return;
        floor.IsActive = active;
        audit.Record(active ? "floor.restore" : "floor.archive", nameof(Floor), floor.Id, floor.SiteId, details: new { floor.Name });
        await db.SaveChangesAsync(ct);
    }

    private static string ValidName(string? name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new DomainException("floor.name", "Give the floor a name, for example '6th floor'.");
        if (name.Length > NameMax) throw new DomainException("floor.name", $"The name can have at most {NameMax} characters.");
        return name;
    }

    private static Workspot NewSpot(ParsedSpot s, string label, DateTimeOffset now) => new()
    {
        ElementId = s.ElementId, Label = label, RoomId = s.RoomId, RoomName = s.RoomName, RoomType = s.RoomType,
        TableId = s.TableId, IsActive = true, IsDesk = Workspot.IsDeskRoomType(s.RoomType), CreatedAt = now,
    };

    /// <summary>The label, or "label (2)", "label (3)"... when it is taken.</summary>
    internal static string UniqueLabel(string label, ISet<string> taken)
    {
        var candidate = label;
        for (var n = 2; !taken.Add(candidate); n++) candidate = $"{label} ({n})";
        return candidate;
    }
}
