using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Auditing;
using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Inventory;
using AssetTracker.Core.Security;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Workplace;

public sealed record PersonOnSpot(int AssignmentId, int PersonId, string Name, string Username, string? JobTitle, string? SiteKey, bool IsActive);

public sealed record AssetOnSpot(int AssignmentId, int AssetId, string Tag, PlacementKind Kind, string Type, string? Model, string Description);

/// <param name="Flags">What is wrong at this seat (see <see cref="SeatFlag"/>).</param>
/// <param name="Place">The seat's zone and room (see <see cref="Workspot.Place(string?)"/>).</param>
public sealed record SpotDetail(int WorkspotId, int FloorId, string Place, string? RoomName, string? ZoneName, string FloorName, string SiteKey,
    bool IsActive, bool IsDesk, PersonOnSpot? Person, AssetOnSpot? Desktop, IReadOnlyList<AssetOnSpot> Screens, IReadOnlyList<string> Flags);

/// <summary>What the map draws at a seat: who sits there, the desktop's tag, how many screens, and whether it is flagged.</summary>
public sealed record SpotState(string? Person, bool PersonLeft, string? Desktop, int Screens, bool Flagged);

/// <summary>A seat that needs a look. <paramref name="Place"/> is its zone and room.</summary>
/// <param name="Kind"><c>person-left</c> (no longer in the directory), <c>seat-disabled</c> (the seat is switched off,
/// gone from the drawing or not a desk) or <c>missing</c> (a person without a desktop and/or a screen).</param>
public sealed record SeatFlag(string Kind, int WorkspotId, string Place, string? Person, string Text);

/// <summary>The floor at a glance: desks, who sits where, free desks and flags.</summary>
public sealed record FloorStatus(int Desks, int Seated, IReadOnlyList<int> FreeDesks, int Desktops, int Screens, IReadOnlyList<SeatFlag> Flags)
{
    public int Free => FreeDesks.Count;
}

/// <param name="Kind"><c>person</c>, <c>desktop</c> or <c>screen</c>.</param>
/// <param name="Place">Where it is now ("6th floor · Finance"), if it is placed.</param>
/// <param name="Reason">Why it cannot be placed here (shown instead of making the row selectable).</param>
public sealed record PlaceCandidate(string Kind, int Id, string Title, string? Subtitle, string? SiteKey, string? Place, string? Reason);

public sealed record PlaceInfo(int FloorId, string FloorName, string SiteKey, int WorkspotId, string Place);

/// <summary>
/// Places people, desktops and screens at desks. This is <b>location only</b>: it is separate from loans (who is
/// responsible for an item). A person and an asset are in one place at a time (placing them elsewhere moves them, and
/// the old row stays as history); a desk has at most one person and one desktop, and any number of screens. Only assets
/// whose type is a desktop or a screen (<see cref="AssetType.WorkspotRole"/>) are placed. Reading follows the Workplace
/// scope (IT and facility all sites, managers their granted sites); changing needs IT or facility; everything is audited
/// in the same transaction.
/// </summary>
public class AssignmentService(AppDbContext db, AccessScope scope, AuditWriter audit, ICurrentUser user, IClock clock)
{
    public const int SearchLimit = 6;
    public const int PaletteLimit = 40;

    // ---- reading ---------------------------------------------------------------------------------------------

    /// <summary>What sits at a workspot, or null when it does not exist or is out of the principal's scope.</summary>
    public async Task<SpotDetail?> GetSpotAsync(int workspotId, CancellationToken ct = default)
    {
        var spot = await db.Workspots.AsNoTracking().Include(w => w.Floor!).ThenInclude(f => f.Site).Include(w => w.Zone)
            .SingleOrDefaultAsync(w => w.Id == workspotId, ct);
        if (spot is null || !(await scope.GetWorkplaceAsync(ct)).CanRead(spot.Floor!.SiteId)) return null;
        if (!spot.Floor.IsActive && !scope.CanEditWorkplace) return null;

        var open = await db.WorkspotAssignments.AsNoTracking()
            .Where(a => a.WorkspotId == workspotId && a.ReleasedAt == null)
            .Include(a => a.Person).ThenInclude(p => p!.Site)
            .Include(a => a.Asset).ThenInclude(a => a!.Type)
            .Include(a => a.Asset).ThenInclude(a => a!.Model)
            .OrderBy(a => a.Id).ToListAsync(ct);

        var person = open.Where(a => a.Person is not null).Select(a => new PersonOnSpot(a.Id, a.PersonId!.Value,
            a.Person!.DisplayName, a.Person.Username, a.Person.JobTitle, a.Person.Site?.Key, a.Person.IsActive)).FirstOrDefault();
        var assets = open.Where(a => a.Asset is not null).Select(a => new AssetOnSpot(a.Id, a.AssetId!.Value, a.Asset!.Tag, a.Kind,
            a.Asset.Type!.Name, a.Asset.Model?.Name, a.Asset.Description)).ToList();

        var flags = Flags(spot, "", person?.Name, person is { IsActive: false }, assets.Any(a => a.Kind == PlacementKind.Desktop),
            assets.Any(a => a.Kind == PlacementKind.Screen)).Select(f => f.Text).ToList();
        return new SpotDetail(spot.Id, spot.FloorId, spot.Place(spot.Zone?.Name), spot.RoomName ?? spot.RoomId, spot.Zone?.Name, spot.Floor.Name, spot.Floor.Site!.Key, spot.IsActive,
            spot.IsDesk, person, assets.FirstOrDefault(a => a.Kind == PlacementKind.Desktop),
            assets.Where(a => a.Kind == PlacementKind.Screen).ToList(), flags);
    }

    private sealed record Row(int WorkspotId, PlacementKind Kind, string? Person, bool PersonActive, string? Tag);

    private Task<List<Row>> OpenRowsAsync(int floorId, CancellationToken ct) =>
        db.WorkspotAssignments.AsNoTracking()
            .Where(a => a.Workspot!.FloorId == floorId && a.ReleasedAt == null)
            .OrderBy(a => a.Id)
            .Select(a => new Row(a.WorkspotId, a.Kind, a.Person == null ? null : a.Person.DisplayName, a.Person != null && a.Person.IsActive,
                a.Asset == null ? null : a.Asset.Tag))
            .ToListAsync(ct);

    /// <summary>What the map draws at each seat that has something placed.</summary>
    public async Task<IReadOnlyDictionary<int, SpotState>> StatesAsync(IReadOnlyCollection<Workspot> spots, CancellationToken ct = default)
    {
        if (spots.Count == 0) return new Dictionary<int, SpotState>();
        var rows = await OpenRowsAsync(spots.First().FloorId, ct);
        var byId = spots.ToDictionary(s => s.Id);
        return rows.GroupBy(r => r.WorkspotId).Where(g => byId.ContainsKey(g.Key)).ToDictionary(g => g.Key, g =>
        {
            var person = g.FirstOrDefault(r => r.Kind == PlacementKind.Person);
            var desktop = g.FirstOrDefault(r => r.Kind == PlacementKind.Desktop)?.Tag;
            var screens = g.Count(r => r.Kind == PlacementKind.Screen);
            var flagged = Flags(byId[g.Key], "", person?.Person, person is { PersonActive: false }, desktop is not null, screens > 0).Count > 0;
            return new SpotState(person?.Person, person is { PersonActive: false }, desktop, screens, flagged);
        });
    }

    /// <summary>Desks, who sits where, free desks and the seats that need a look.</summary>
    public async Task<FloorStatus> StatusAsync(IReadOnlyCollection<Workspot> spots, CancellationToken ct = default)
    {
        var rows = spots.Count == 0 ? new List<Row>() : await OpenRowsAsync(spots.First().FloorId, ct);
        var bySeat = rows.ToLookup(r => r.WorkspotId);
        var desks = spots.Where(s => s.IsActive && s.IsDesk).ToList();
        var floorId = spots.FirstOrDefault()?.FloorId ?? 0;
        var zones = await db.FloorZones.AsNoTracking().Where(t => t.FloorId == floorId).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var flags = new List<SeatFlag>();
        foreach (var spot in spots.OrderBy(s => s.Label, StringComparer.OrdinalIgnoreCase))
        {
            var here = bySeat[spot.Id].ToList();
            if (here.Count == 0) continue;
            var person = here.FirstOrDefault(r => r.Kind == PlacementKind.Person);
            flags.AddRange(Flags(spot, spot.Place(spot.ZoneId is { } t && zones.TryGetValue(t, out var zone) ? zone : null), person?.Person, person is { PersonActive: false },
                here.Any(r => r.Kind == PlacementKind.Desktop), here.Any(r => r.Kind == PlacementKind.Screen)));
        }
        return new FloorStatus(
            desks.Count,
            desks.Count(d => bySeat[d.Id].Any(r => r.Kind == PlacementKind.Person)),
            desks.Where(d => !bySeat[d.Id].Any(r => r.Kind == PlacementKind.Person)).Select(d => d.Id).ToList(),
            rows.Count(r => r.Kind == PlacementKind.Desktop),
            rows.Count(r => r.Kind == PlacementKind.Screen),
            flags.OrderBy(f => f.Kind switch { "person-left" => 0, "seat-disabled" => 1, _ => 2 }).ToList());
    }

    /// <summary>
    /// The flags of one seat: a person who is no longer in the directory, a person at a seat that is switched off, gone
    /// from the drawing or not a desk, and a person without a desktop or a screen.
    /// </summary>
    private static List<SeatFlag> Flags(Workspot spot, string place, string? person, bool personLeft, bool hasDesktop, bool hasScreen)
    {
        var flags = new List<SeatFlag>();
        if (person is null) return flags;
        if (personLeft)
            flags.Add(new SeatFlag("person-left", spot.Id, place, person, $"{person} no longer works here: free the seat."));
        if (!spot.IsActive)
            flags.Add(new SeatFlag("seat-disabled", spot.Id, place, person, $"{person} is at a seat that is switched off or no longer in the drawing."));
        else if (!spot.IsDesk)
            flags.Add(new SeatFlag("seat-disabled", spot.Id, place, person, $"{person} is at a seat that is not a desk."));
        if (!hasDesktop || !hasScreen)
        {
            var missing = !hasDesktop && !hasScreen ? "no desktop and no screen" : !hasDesktop ? "no desktop" : "no screen";
            flags.Add(new SeatFlag("missing", spot.Id, place, person, $"{person} has {missing}."));
        }
        return flags;
    }

    // ---- locations (for the asset page) ----------------------------------------------------------------------

    public async Task<PlaceInfo?> LocationOfAssetAsync(int assetId, CancellationToken ct = default)
    {
        var access = await scope.GetWorkplaceAsync(ct);
        if (!access.CanReadInventory) return null;
        var editor = scope.CanEditWorkplace;
        var row = await db.WorkspotAssignments.AsNoTracking()
            .Where(a => a.AssetId == assetId && a.ReleasedAt == null && (a.Workspot!.Floor!.IsActive || editor))
            .Select(a => new { a.Workspot!.Floor!.SiteId, a.Workspot.FloorId, Floor = a.Workspot.Floor.Name, SiteKey = a.Workspot.Floor.Site!.Key,
                a.WorkspotId, Zone = a.Workspot.Zone == null ? null : a.Workspot.Zone.Name, Room = a.Workspot.RoomName ?? a.Workspot.RoomId })
            .FirstOrDefaultAsync(ct);
        return row is not null && access.CanRead(row.SiteId)
            ? new PlaceInfo(row.FloorId, row.Floor, row.SiteKey, row.WorkspotId, Workspot.Place(row.Zone, row.Room))
            : null;
    }

    // ---- what can be placed ----------------------------------------------------------------------------------

    /// <summary>
    /// The drag-and-drop list: people (<c>person</c>), desktops (<c>desktop</c>) or screens (<c>screen</c>) that are not
    /// placed anywhere yet. Without text: the floor's site (people of that site, its desktops or screens); with text: a
    /// search (people of every site). Editors only.
    /// </summary>
    public async Task<IReadOnlyList<PlaceCandidate>> PaletteAsync(int floorId, string kind, string? term, CancellationToken ct = default)
    {
        scope.EnsureWorkplaceEditor();
        var floor = await db.Floors.AsNoTracking().Include(f => f.Site).SingleOrDefaultAsync(f => f.Id == floorId, ct)
                    ?? throw new DomainException("floor.not-found", "Floor not found.");
        term = term?.Trim();
        var pattern = string.IsNullOrEmpty(term) ? null : Paging.ContainsPattern(term);
        var placed = db.WorkspotAssignments.Where(a => a.ReleasedAt == null);

        if (kind == "person")
        {
            var people = db.People.AsNoTracking().Where(p => p.IsActive && !placed.Any(a => a.PersonId == p.Id));
            people = pattern is null
                ? people.Where(p => p.SiteId == floor.SiteId)
                : people.Where(p => EF.Functions.Like(p.DisplayName, pattern, @"\") || EF.Functions.Like(p.Username, pattern, @"\") ||
                                    (p.JobTitle != null && EF.Functions.Like(p.JobTitle, pattern, @"\")));
            return await people.OrderBy(p => p.DisplayName).Take(PaletteLimit)
                .Select(p => new PlaceCandidate("person", p.Id, p.DisplayName,
                    p.JobTitle == null ? p.Username : p.Username + " · " + p.JobTitle, p.Site == null ? null : p.Site.Key, null, null))
                .ToListAsync(ct);
        }

        var role = kind switch
        {
            "desktop" => WorkspotRole.Desktop,
            "screen" => WorkspotRole.Screen,
            _ => throw new DomainException("palette.kind", "Choose people, desktops or screens."),
        };
        var assets = db.Assets.AsNoTracking()
            .Where(a => a.RetiredAt == null && a.SiteId == floor.SiteId && a.Type!.WorkspotRole == role && !placed.Any(x => x.AssetId == a.Id));
        if (term is not null && term.Length > 0) assets = AssetService.MatchingTerm(assets, term, includeSensitive: false);
        return await assets.OrderBy(a => a.Tag).Take(PaletteLimit)
            .Select(a => new PlaceCandidate(kind, a.Id, a.Tag,
                a.Model == null ? a.Description : a.Model.Name + (a.Description == "" ? "" : " · " + a.Description), a.Site!.Key, null, null))
            .ToListAsync(ct);
    }

    /// <summary>
    /// People, desktops and screens matching the text, with where each is now and why it cannot be placed here (another
    /// site, an inactive person). Editors only; assets are found by tag, description, model and non-sensitive fields.
    /// </summary>
    public async Task<IReadOnlyList<PlaceCandidate>> SearchAsync(int floorId, string? term, CancellationToken ct = default)
    {
        scope.EnsureWorkplaceEditor();
        term = term?.Trim();
        if (string.IsNullOrEmpty(term) || term.Length < 2) return [];
        var floor = await db.Floors.AsNoTracking().SingleOrDefaultAsync(f => f.Id == floorId, ct)
                    ?? throw new DomainException("floor.not-found", "Floor not found.");
        var pattern = Paging.ContainsPattern(term);

        var people = await db.People.AsNoTracking()
            .Where(p => EF.Functions.Like(p.DisplayName, pattern, @"\") || EF.Functions.Like(p.Username, pattern, @"\") ||
                        (p.JobTitle != null && EF.Functions.Like(p.JobTitle, pattern, @"\")))
            .OrderByDescending(p => p.IsActive).ThenBy(p => p.DisplayName).Take(SearchLimit)
            .Select(p => new { p.Id, p.DisplayName, p.Username, p.JobTitle, SiteKey = p.Site == null ? null : p.Site.Key, p.IsActive }).ToListAsync(ct);
        var assets = await AssetService.MatchingTerm(db.Assets.AsNoTracking()
                .Where(a => a.RetiredAt == null && a.Type!.WorkspotRole != WorkspotRole.None), term, includeSensitive: false)
            .OrderBy(a => a.Tag).Take(SearchLimit)
            .Select(a => new { a.Id, a.Tag, Role = a.Type!.WorkspotRole, Type = a.Type.Name, Model = a.Model == null ? null : a.Model.Name,
                a.Description, a.SiteId, SiteKey = a.Site!.Key }).ToListAsync(ct);

        var personIds = people.Select(p => p.Id).ToList();
        var assetIds = assets.Select(a => a.Id).ToList();
        var places = await db.WorkspotAssignments.AsNoTracking()
            .Where(a => a.ReleasedAt == null && ((a.PersonId != null && personIds.Contains(a.PersonId.Value)) ||
                                                 (a.AssetId != null && assetIds.Contains(a.AssetId.Value))))
            .Select(a => new { a.PersonId, a.AssetId, Floor = a.Workspot!.Floor!.Name, Zone = a.Workspot.Zone == null ? null : a.Workspot.Zone.Name,
                Room = a.Workspot.RoomName ?? a.Workspot.RoomId }).ToListAsync(ct);
        string At(string floorName, string? zone, string? room) =>
            Workspot.Place(zone, room) is { Length: > 0 } p ? $"{floorName} · {p}" : floorName;

        var result = new List<PlaceCandidate>();
        result.AddRange(people.Select(p => new PlaceCandidate("person", p.Id, p.DisplayName,
            string.Join(" · ", new[] { p.Username, p.JobTitle, p.SiteKey }.Where(s => !string.IsNullOrEmpty(s))), p.SiteKey,
            places.Where(x => x.PersonId == p.Id).Select(x => At(x.Floor, x.Zone, x.Room)).FirstOrDefault(),
            p.IsActive ? null : "No longer in the directory")));
        result.AddRange(assets.Select(a => new PlaceCandidate(a.Role == WorkspotRole.Desktop ? "desktop" : "screen", a.Id, a.Tag,
            string.Join(" · ", new[] { a.Type, a.Model, a.Description }.Where(s => !string.IsNullOrEmpty(s)).Distinct()), a.SiteKey,
            places.Where(x => x.AssetId == a.Id).Select(x => At(x.Floor, x.Zone, x.Room)).FirstOrDefault(),
            a.SiteId == floor.SiteId ? null : $"Belongs to site {a.SiteKey}")));
        return result;
    }

    // ---- changing ---------------------------------------------------------------------------------------------

    private async Task<Workspot> LoadDeskAsync(int workspotId, CancellationToken ct)
    {
        scope.EnsureWorkplaceEditor();
        var spot = await db.Workspots.Include(w => w.Floor).Include(w => w.Zone).SingleOrDefaultAsync(w => w.Id == workspotId, ct)
                   ?? throw new DomainException("workspot.not-found", "Workspot not found.");
        if (!spot.Floor!.IsActive) throw new DomainException("floor.archived", "This floor is archived.");
        if (!spot.IsActive) throw new DomainException("workspot.inactive", "This seat is switched off. Switch it on first.");
        if (!spot.IsDesk) throw new DomainException("workspot.not-desk", "This seat is not a desk. Make it a desk first.");
        return spot;
    }

    /// <summary>Puts a person at the desk (moving them if they sit elsewhere). A desk holds one person.</summary>
    public async Task<string> AssignPersonAsync(int workspotId, int personId, CancellationToken ct = default)
    {
        var spot = await LoadDeskAsync(workspotId, ct);
        var person = await db.People.SingleOrDefaultAsync(p => p.Id == personId, ct)
                     ?? throw new DomainException("assignment.person", "Person not found.");
        if (!person.IsActive) throw new DomainException("assignment.person-inactive", $"{person.DisplayName} is no longer in the directory.");

        var seat = await db.WorkspotAssignments.Include(a => a.Person)
            .FirstOrDefaultAsync(a => a.WorkspotId == workspotId && a.Kind == PlacementKind.Person && a.ReleasedAt == null, ct);
        if (seat is not null)
        {
            if (seat.PersonId == personId) return $"{person.DisplayName} already sits at this desk.";
            throw new ConflictException("assignment.seat-taken", $"This desk is taken by {seat.Person!.DisplayName}. Release them first.");
        }
        var current = await db.WorkspotAssignments.Include(a => a.Workspot).ThenInclude(w => w!.Floor)
            .Include(a => a.Workspot).ThenInclude(w => w!.Zone)
            .FirstOrDefaultAsync(a => a.PersonId == personId && a.ReleasedAt == null, ct);
        return await PlaceAsync(spot, current, new WorkspotAssignment { PersonId = personId, Kind = PlacementKind.Person },
            person.DisplayName, ct);
    }

    /// <summary>
    /// Puts a desktop or a screen at the desk (moving it if it is placed elsewhere). It must belong to the floor's site and
    /// its type must be a desktop or a screen; a desk has one desktop.
    /// </summary>
    public async Task<string> AssignAssetAsync(int workspotId, int assetId, CancellationToken ct = default)
    {
        var spot = await LoadDeskAsync(workspotId, ct);
        var asset = await db.Assets.Include(a => a.Site).Include(a => a.Type).SingleOrDefaultAsync(a => a.Id == assetId, ct)
                    ?? throw new DomainException("assignment.asset", "Asset not found.");
        var kind = asset.Type!.WorkspotRole switch
        {
            WorkspotRole.Desktop => PlacementKind.Desktop,
            WorkspotRole.Screen => PlacementKind.Screen,
            _ => throw new DomainException("assignment.asset-type", $"{asset.Tag} is a {asset.Type.Name}: only desktops and screens are placed at desks."),
        };
        if (asset.RetiredAt != null) throw new DomainException("assignment.asset-retired", $"{asset.Tag} is retired.");
        if (asset.SiteId != spot.Floor!.SiteId)
            throw new DomainException("assignment.asset-site", $"{asset.Tag} belongs to site {asset.Site!.Key}, this floor is at another site. Transfer the asset first.");

        var current = await db.WorkspotAssignments.Include(a => a.Workspot).ThenInclude(w => w!.Floor)
            .Include(a => a.Workspot).ThenInclude(w => w!.Zone)
            .FirstOrDefaultAsync(a => a.AssetId == assetId && a.ReleasedAt == null, ct);
        if (current?.WorkspotId == workspotId) return $"{asset.Tag} is already at this desk.";
        if (kind == PlacementKind.Desktop)
        {
            var other = await db.WorkspotAssignments.Include(a => a.Asset)
                .FirstOrDefaultAsync(a => a.WorkspotId == workspotId && a.Kind == PlacementKind.Desktop && a.ReleasedAt == null, ct);
            if (other is not null)
                throw new ConflictException("assignment.desktop-taken", $"This desk already has desktop {other.Asset!.Tag}. Release it first.");
        }
        return await PlaceAsync(spot, current, new WorkspotAssignment { AssetId = assetId, Kind = kind }, asset.Tag, ct);
    }

    /// <summary>Takes a person, desktop or screen away from a desk. The row stays as history.</summary>
    public async Task<string> ReleaseAsync(int assignmentId, CancellationToken ct = default)
    {
        scope.EnsureWorkplaceEditor();
        var row = await db.WorkspotAssignments.Include(a => a.Workspot).ThenInclude(w => w!.Floor)
                      .Include(a => a.Workspot).ThenInclude(w => w!.Zone).Include(a => a.Person).Include(a => a.Asset)
                      .SingleOrDefaultAsync(a => a.Id == assignmentId, ct)
                  ?? throw new DomainException("assignment.not-found", "Assignment not found.");
        if (row.ReleasedAt != null) throw new ConflictException("assignment.released", "This was already released.");
        row.ReleasedAt = clock.UtcNow;
        row.ReleasedBy = user.Name;
        var name = row.Person?.DisplayName ?? row.Asset?.Tag ?? "";
        audit.Record("assignment.release", nameof(WorkspotAssignment), row.Id, row.Workspot!.Floor!.SiteId,
            details: new { Kind = row.Kind.ToString(), Name = name, Workspot = row.Workspot.Label, Floor = row.Workspot.Floor.Name });
        await db.SaveChangesAsync(ct);
        return $"{name} released from {ADesk(row.Workspot)}.";
    }

    /// <summary>
    /// Closes the open placement of an asset that is retired or moved to another site (called by the inventory in its own
    /// transaction): a retired desktop or one at another site cannot stay at a desk.
    /// </summary>
    public static async Task ReleaseAssetAsync(AppDbContext db, AuditWriter audit, int assetId, string actor, DateTimeOffset now,
        string why, CancellationToken ct)
    {
        var rows = await db.WorkspotAssignments.Include(a => a.Workspot).ThenInclude(w => w!.Floor).Include(a => a.Asset)
            .Where(a => a.AssetId == assetId && a.ReleasedAt == null).ToListAsync(ct);
        foreach (var row in rows)
        {
            row.ReleasedAt = now;
            row.ReleasedBy = actor;
            audit.Record("assignment.release", nameof(WorkspotAssignment), row.Id, row.Workspot!.Floor!.SiteId, why,
                new { Kind = row.Kind.ToString(), Name = row.Asset!.Tag, Workspot = row.Workspot.Label, Floor = row.Workspot.Floor.Name });
        }
    }

    /// <summary>"a desk in Finance" (its zone and room) for messages; the drawing's seat id is never shown.</summary>
    private static string ADesk(Workspot w) => w.Place(w.Zone?.Name) is { Length: > 0 } p ? $"a desk in {p}" : "a desk";

    private async Task<string> PlaceAsync(Workspot spot, WorkspotAssignment? current, WorkspotAssignment fresh, string name, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        string? movedFrom = null;
        if (current is not null)
        {
            // Two saves: the filtered unique index must see the old row closed before the new one is inserted.
            current.ReleasedAt = clock.UtcNow;
            current.ReleasedBy = user.Name;
            var old = current.Workspot!;
            movedFrom = old.Place(old.Zone?.Name) is { Length: > 0 } from ? $"{old.Floor!.Name} · {from}" : old.Floor!.Name;
            await db.SaveChangesAsync(ct);
        }
        fresh.WorkspotId = spot.Id;
        fresh.AssignedAt = clock.UtcNow;
        fresh.AssignedBy = user.Name;
        db.WorkspotAssignments.Add(fresh);
        await db.SaveChangesAsync(ct);
        audit.Record("assignment.assign", nameof(WorkspotAssignment), fresh.Id, spot.Floor!.SiteId,
            details: new { Kind = fresh.Kind.ToString(), Name = name, Workspot = spot.Label, Floor = spot.Floor.Name, MovedFrom = movedFrom });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return movedFrom is null ? $"{name} placed at {ADesk(spot)}." : $"{name} moved from {movedFrom} to {ADesk(spot)}.";
    }
}
