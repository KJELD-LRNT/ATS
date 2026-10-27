using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Auditing;
using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Workplace;

/// <summary>
/// Zones on a floor plan: clusters of workspots drawn in one colour with a name. They exist only on the map (no link to
/// the directory or to loans) and can be renamed at will. A workspot is in at most one zone.
/// </summary>
public class ZoneService(AppDbContext db, AccessScope scope, AuditWriter audit, WorkspotService spotService, IClock clock)
{
    /// <summary>Size of the map's colour palette (<c>zc-0</c>…<c>zc-7</c> in site.css).</summary>
    public const int Colors = 8;
    public const int NameMax = 100;

    public async Task<FloorZone> CreateAsync(string name, IReadOnlyCollection<int> workspotIds, CancellationToken ct = default)
    {
        scope.EnsureWorkplaceEditor();
        name = ValidName(name);
        var spots = await spotService.LoadSameFloorAsync(workspotIds, ct);
        if (spots.Any(s => !s.IsActive)) throw new DomainException("zone.inactive", "A switched-off workspot cannot join a zone.");
        var floor = spots[0].Floor!;
        var zones = await db.FloorZones.Where(t => t.FloorId == floor.Id).ToListAsync(ct);
        EnsureNameFree(zones, name, null);

        var used = zones.Select(t => t.ColorIndex).ToHashSet();
        var color = Enumerable.Range(0, Colors).FirstOrDefault(i => !used.Contains(i), zones.Count % Colors);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var zone = new FloorZone { FloorId = floor.Id, Name = name, ColorIndex = color, CreatedAt = clock.UtcNow };
        db.FloorZones.Add(zone);
        await db.SaveChangesAsync(ct);
        foreach (var s in spots) s.ZoneId = zone.Id;
        audit.Record("zone.create", nameof(FloorZone), zone.Id, floor.SiteId,
            details: new { FloorId = floor.Id, Floor = floor.Name, zone.Name, Workspots = spots.Select(s => s.Label).ToList() });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return zone;
    }

    /// <summary>Puts workspots into a zone (moving them out of any other zone).</summary>
    public async Task<int> AddAsync(int zoneId, IReadOnlyCollection<int> workspotIds, CancellationToken ct = default)
    {
        scope.EnsureWorkplaceEditor();
        var zone = await LoadAsync(zoneId, ct);
        var spots = await spotService.LoadSameFloorAsync(workspotIds, ct);
        if (spots[0].FloorId != zone.FloorId) throw new DomainException("workspot.floors", "The workspots are on another floor than the zone.");
        if (spots.Any(s => !s.IsActive)) throw new DomainException("zone.inactive", "A switched-off workspot cannot join a zone.");
        var moving = spots.Where(s => s.ZoneId != zone.Id).ToList();
        if (moving.Count == 0) return 0;
        foreach (var s in moving) s.ZoneId = zone.Id;
        audit.Record("zone.add", nameof(FloorZone), zone.Id, zone.Floor!.SiteId,
            details: new { zone.Name, Workspots = moving.Select(s => s.Label).ToList() });
        await db.SaveChangesAsync(ct);
        return moving.Count;
    }

    /// <summary>Takes workspots out of whatever zone they are in.</summary>
    public async Task<int> RemoveAsync(IReadOnlyCollection<int> workspotIds, CancellationToken ct = default)
    {
        scope.EnsureWorkplaceEditor();
        var spots = await spotService.LoadSameFloorAsync(workspotIds, ct);
        var moving = spots.Where(s => s.ZoneId != null).ToList();
        if (moving.Count == 0) return 0;
        var zones = await db.FloorZones.Where(t => moving.Select(s => s.ZoneId).Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        foreach (var s in moving) s.ZoneId = null;
        var floor = spots[0].Floor!;
        audit.Record("zone.remove", nameof(Floor), floor.Id, floor.SiteId, details: new
        {
            floor.Name, Workspots = moving.Select(s => s.Label).ToList(),
        });
        await db.SaveChangesAsync(ct);
        return moving.Count;
    }

    public async Task RenameAsync(int zoneId, string name, CancellationToken ct = default)
    {
        scope.EnsureWorkplaceEditor();
        name = ValidName(name);
        var zone = await LoadAsync(zoneId, ct);
        if (zone.Name == name) return;
        var zones = await db.FloorZones.Where(t => t.FloorId == zone.FloorId).ToListAsync(ct);
        EnsureNameFree(zones, name, zone.Id);
        var before = zone.Name;
        zone.Name = name;
        audit.Record("zone.rename", nameof(FloorZone), zone.Id, zone.Floor!.SiteId, details: new { Before = before, After = name });
        await db.SaveChangesAsync(ct);
    }

    public async Task SetColorAsync(int zoneId, int colorIndex, CancellationToken ct = default)
    {
        scope.EnsureWorkplaceEditor();
        if (colorIndex is < 0 or >= Colors) throw new DomainException("zone.color", "Unknown colour.");
        var zone = await LoadAsync(zoneId, ct);
        if (zone.ColorIndex == colorIndex) return;
        var before = zone.ColorIndex;
        zone.ColorIndex = colorIndex;
        audit.Record("zone.color", nameof(FloorZone), zone.Id, zone.Floor!.SiteId, details: new { zone.Name, Before = before, After = colorIndex });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Deletes the zone; its workspots stay on the floor, ungrouped.</summary>
    public async Task DeleteAsync(int zoneId, CancellationToken ct = default)
    {
        scope.EnsureWorkplaceEditor();
        var zone = await LoadAsync(zoneId, ct);
        var spots = await db.Workspots.Where(w => w.ZoneId == zoneId).ToListAsync(ct);
        foreach (var s in spots) s.ZoneId = null;
        await db.SaveChangesAsync(ct);
        db.FloorZones.Remove(zone);
        audit.Record("zone.delete", nameof(FloorZone), zone.Id, zone.Floor!.SiteId, details: new { zone.Name, Workspots = spots.Count });
        await db.SaveChangesAsync(ct);
    }

    private async Task<FloorZone> LoadAsync(int id, CancellationToken ct) =>
        await db.FloorZones.Include(t => t.Floor).SingleOrDefaultAsync(t => t.Id == id, ct)
        ?? throw new DomainException("zone.not-found", "Zone not found.");

    private static string ValidName(string? name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new DomainException("zone.name", "Give the zone a name.");
        if (name.Length > NameMax) throw new DomainException("zone.name", $"The name can have at most {NameMax} characters.");
        return name;
    }

    private static void EnsureNameFree(IEnumerable<FloorZone> zones, string name, int? exceptId)
    {
        if (zones.Any(t => t.Id != exceptId && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ConflictException("zone.exists", $"This floor already has a zone called '{name}'.");
    }
}
