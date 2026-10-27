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

public sealed record AssetOnSpot(int AssignmentId, int AssetId, string Tag, string Type, string? Model, string Description, string State);

public sealed record PoolOnSpot(int AssignmentId, int PoolId, string Sku, string Type, string Description, int Quantity);

/// <param name="Hints">Things worth a look at this workspot (see <see cref="AssignmentService.HintsAsync"/>).</param>
public sealed record SpotDetail(int WorkspotId, int FloorId, string Label, string? RoomName, string FloorName, string SiteKey,
    bool IsActive, PersonOnSpot? Person, IReadOnlyList<AssetOnSpot> Assets, IReadOnlyList<PoolOnSpot> Pools, IReadOnlyList<string> Hints);

/// <summary>What the map shows of a seat: who sits there and how many things are placed.</summary>
public sealed record SpotOccupancy(string? Person, bool PersonLeft, int Items);

public sealed record Hint(string Kind, int WorkspotId, string Label, string Text);

/// <param name="Kind"><c>person</c>, <c>asset</c> or <c>pool</c>.</param>
/// <param name="Place">Where it is now ("6th floor · D-12"), if it is placed.</param>
/// <param name="Reason">Why it cannot be placed here (shown instead of making the row selectable).</param>
public sealed record PlaceCandidate(string Kind, int Id, string Title, string? Subtitle, string? SiteKey, string? Place, string? Reason);

/// <param name="Quantity">Units for a pool, otherwise 1.</param>
public sealed record PlaceInfo(int FloorId, string FloorName, string SiteKey, int WorkspotId, string Label, string? RoomName, int Quantity);

/// <summary>
/// Places people, tagged assets and pool units at workspots. This is <b>location only</b>: it is separate from loans
/// (who is responsible for an item) and never touches stock. A person and an asset are in one place at a time
/// (assigning them elsewhere moves them, and the old row stays as history), a workspot has one person and any number
/// of assets and pools. Reading follows the Workplace scope (IT and facility all sites, managers their granted sites);
/// changing needs IT or facility; everything is audited in the same transaction.
/// </summary>
public class AssignmentService(AppDbContext db, AccessScope scope, AuditWriter audit, ICurrentUser user, IClock clock)
{
    public const int SearchLimit = 6;
    public const int HintLimit = 50;
    public const int QuantityMax = 1000;

    // ---- reading ---------------------------------------------------------------------------------------------

    /// <summary>What sits at a workspot, or null when it does not exist or is out of the principal's scope.</summary>
    public async Task<SpotDetail?> GetSpotAsync(int workspotId, CancellationToken ct = default)
    {
        var spot = await db.Workspots.AsNoTracking().Include(w => w.Floor!).ThenInclude(f => f.Site)
            .SingleOrDefaultAsync(w => w.Id == workspotId, ct);
        if (spot is null || !(await scope.GetWorkplaceAsync(ct)).CanRead(spot.Floor!.SiteId)) return null;
        if (!spot.Floor.IsActive && !scope.CanEditWorkplace) return null;

        var open = await db.WorkspotAssignments.AsNoTracking()
            .Where(a => a.WorkspotId == workspotId && a.ReleasedAt == null)
            .Include(a => a.Person).ThenInclude(p => p!.Site)
            .Include(a => a.Asset).ThenInclude(a => a!.Type)
            .Include(a => a.Asset).ThenInclude(a => a!.Model)
            .Include(a => a.Pool).ThenInclude(p => p!.Type)
            .OrderBy(a => a.Id).ToListAsync(ct);

        var person = open.Where(a => a.Person is not null).Select(a => new PersonOnSpot(a.Id, a.PersonId!.Value,
            a.Person!.DisplayName, a.Person.Username, a.Person.JobTitle, a.Person.Site?.Key, a.Person.IsActive)).FirstOrDefault();
        var assets = open.Where(a => a.Asset is not null).Select(a => new AssetOnSpot(a.Id, a.AssetId!.Value, a.Asset!.Tag,
            a.Asset.Type!.Name, a.Asset.Model?.Name, a.Asset.Description, a.Asset.RetiredAt != null ? "Retired" : a.Asset.State.ToString())).ToList();
        var pools = open.Where(a => a.Pool is not null).Select(a => new PoolOnSpot(a.Id, a.PoolId!.Value, a.Pool!.Sku,
            a.Pool.Type!.Name, a.Pool.Description, a.Quantity)).ToList();

        var hints = (await HintsForAsync(spot.Floor.Id, workspotId, ct)).Select(h => h.Text).ToList();
        return new SpotDetail(spot.Id, spot.FloorId, spot.Label, spot.RoomName, spot.Floor.Name, spot.Floor.Site!.Key, spot.IsActive,
            person, assets, pools, hints);
    }

    /// <summary>Who sits where and how much is placed, for colouring the map and its tooltips.</summary>
    public async Task<IReadOnlyDictionary<int, SpotOccupancy>> OccupancyAsync(int floorId, CancellationToken ct = default)
    {
        var rows = await db.WorkspotAssignments.AsNoTracking()
            .Where(a => a.Workspot!.FloorId == floorId && a.ReleasedAt == null)
            .Select(a => new { a.WorkspotId, Person = a.Person == null ? null : a.Person.DisplayName, Left = a.Person != null && !a.Person.IsActive, IsItem = a.PersonId == null })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.WorkspotId).ToDictionary(g => g.Key, g => new SpotOccupancy(
            g.Select(r => r.Person).FirstOrDefault(p => p != null), g.Any(r => r.Left), g.Count(r => r.IsItem)));
    }

    /// <summary>The floor's open points of attention (capped), for the side panel.</summary>
    public Task<IReadOnlyList<Hint>> HintsAsync(int floorId, CancellationToken ct = default) => HintsForAsync(floorId, null, ct);

    private async Task<IReadOnlyList<Hint>> HintsForAsync(int floorId, int? onlyWorkspot, CancellationToken ct)
    {
        var open = db.WorkspotAssignments.AsNoTracking().Where(a => a.Workspot!.FloorId == floorId && a.ReleasedAt == null);
        if (onlyWorkspot is { } only) open = open.Where(a => a.WorkspotId == only);
        var rows = await open.Select(a => new
        {
            a.WorkspotId, a.Workspot!.Label, SpotActive = a.Workspot.IsActive, a.PersonId, PersonName = a.Person == null ? null : a.Person.DisplayName,
            PersonActive = a.Person != null && a.Person.IsActive, a.AssetId, Tag = a.Asset == null ? null : a.Asset.Tag,
            AssetRetired = a.Asset != null && a.Asset.RetiredAt != null, AssetUnavailable = a.Asset != null && a.Asset.State == AssetState.Unavailable,
            Sku = a.Pool == null ? null : a.Pool.Sku,
        }).ToListAsync(ct);

        var hints = new List<Hint>();
        var seatPerson = rows.Where(r => r.PersonId != null).ToDictionary(r => r.WorkspotId);
        foreach (var r in rows.Where(r => r.PersonId != null && !r.PersonActive))
            hints.Add(new Hint("person-left", r.WorkspotId, r.Label, $"{r.PersonName} has left the organisation: the seat can be freed."));
        foreach (var g in rows.GroupBy(r => r.WorkspotId).Where(g => !g.First().SpotActive))
            hints.Add(new Hint("seat-gone", g.Key, g.First().Label, "This seat is switched off or no longer in the drawing but still has people or items placed."));
        foreach (var r in rows.Where(r => r.AssetId != null && (r.AssetRetired || r.AssetUnavailable)))
            hints.Add(new Hint("asset-state", r.WorkspotId, r.Label, $"{r.Tag} is {(r.AssetRetired ? "retired" : "unavailable")} but still placed here."));

        // An asset on loan to someone other than the person at its seat (location and loans are separate, so this is a hint).
        var assetIds = rows.Where(r => r.AssetId != null && seatPerson.ContainsKey(r.WorkspotId)).Select(r => r.AssetId!.Value).ToList();
        if (assetIds.Count > 0)
        {
            var loans = await db.Loans.AsNoTracking().Where(l => l.ClosedAt == null && l.AssetId != null && assetIds.Contains(l.AssetId.Value))
                .Select(l => new { AssetId = l.AssetId!.Value, l.HolderId, Holder = l.HolderNameSnapshot }).ToListAsync(ct);
            foreach (var r in rows.Where(r => r.AssetId != null && seatPerson.ContainsKey(r.WorkspotId)))
            {
                var loan = loans.FirstOrDefault(l => l.AssetId == r.AssetId);
                var sitting = seatPerson[r.WorkspotId];
                if (loan is null || loan.HolderId == sitting.PersonId) continue;
                var who = user.IsIt ? $"on loan to {loan.Holder}" : "on loan to someone else";
                hints.Add(new Hint("loan-mismatch", r.WorkspotId, r.Label, $"{r.Tag} is {who}, not to {sitting.PersonName} who sits here."));
            }
        }
        return hints.Take(HintLimit).ToList();
    }

    // ---- locations (for the asset and pool pages) ------------------------------------------------------------

    public async Task<PlaceInfo?> LocationOfAssetAsync(int assetId, CancellationToken ct = default) =>
        (await PlacesAsync(db.WorkspotAssignments.Where(a => a.AssetId == assetId && a.ReleasedAt == null), ct)).FirstOrDefault();

    public Task<IReadOnlyList<PlaceInfo>> LocationsOfPoolAsync(int poolId, CancellationToken ct = default) =>
        PlacesAsync(db.WorkspotAssignments.Where(a => a.PoolId == poolId && a.ReleasedAt == null), ct);

    private async Task<IReadOnlyList<PlaceInfo>> PlacesAsync(IQueryable<WorkspotAssignment> q, CancellationToken ct)
    {
        var access = await scope.GetWorkplaceAsync(ct);
        if (!access.CanReadInventory) return [];
        var editor = scope.CanEditWorkplace;
        var rows = await q.AsNoTracking().Where(a => a.Workspot!.Floor!.IsActive || editor)
            .OrderBy(a => a.Workspot!.Floor!.Name).ThenBy(a => a.Workspot!.Label)
            .Select(a => new PlaceInfo(a.Workspot!.FloorId, a.Workspot.Floor!.Name, a.Workspot.Floor.Site!.Key, a.WorkspotId,
                a.Workspot.Label, a.Workspot.RoomName, a.Quantity)).ToListAsync(ct);
        var siteIds = access.SiteIds;
        if (access.AllSites) return rows;
        var floors = await db.Floors.AsNoTracking().Where(f => rows.Select(r => r.FloorId).Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.SiteId, ct);
        return rows.Where(r => siteIds.Contains(floors[r.FloorId])).ToList();
    }

    // ---- searching what can be placed ------------------------------------------------------------------------

    /// <summary>
    /// People, assets and pools matching the text, with where each is now and why it cannot be placed on this floor (an
    /// asset or pool of another site, an inactive person). Editors only; assets are found by tag, description, model and
    /// non-sensitive fields, never by sensitive ones.
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
        var assets = await AssetService.MatchingTerm(db.Assets.AsNoTracking().Where(a => a.RetiredAt == null), term, includeSensitive: false)
            .OrderBy(a => a.Tag).Take(SearchLimit)
            .Select(a => new { a.Id, a.Tag, Type = a.Type!.Name, Model = a.Model == null ? null : a.Model.Name, a.Description, a.SiteId, SiteKey = a.Site!.Key }).ToListAsync(ct);
        var pools = await db.Pools.AsNoTracking()
            .Where(p => EF.Functions.Like(p.Sku, pattern, @"\") || EF.Functions.Like(p.Description, pattern, @"\"))
            .OrderBy(p => p.Sku).Take(SearchLimit)
            .Select(p => new { p.Id, p.Sku, Type = p.Type!.Name, p.Description, p.SiteId, SiteKey = p.Site!.Key, p.Available, p.Outstanding }).ToListAsync(ct);

        var personIds = people.Select(p => p.Id).ToList();
        var assetIds = assets.Select(a => a.Id).ToList();
        var poolIds = pools.Select(p => p.Id).ToList();
        var places = await db.WorkspotAssignments.AsNoTracking()
            .Where(a => a.ReleasedAt == null && ((a.PersonId != null && personIds.Contains(a.PersonId.Value)) ||
                                                 (a.AssetId != null && assetIds.Contains(a.AssetId.Value)) ||
                                                 (a.PoolId != null && poolIds.Contains(a.PoolId.Value))))
            .Select(a => new { a.PersonId, a.AssetId, a.PoolId, a.Quantity, Floor = a.Workspot!.Floor!.Name, a.Workspot.Label }).ToListAsync(ct);

        var result = new List<PlaceCandidate>();
        result.AddRange(people.Select(p => new PlaceCandidate("person", p.Id, p.DisplayName,
            string.Join(" · ", new[] { p.Username, p.JobTitle, p.SiteKey }.Where(s => !string.IsNullOrEmpty(s))), p.SiteKey,
            places.Where(x => x.PersonId == p.Id).Select(x => $"{x.Floor} · {x.Label}").FirstOrDefault(),
            p.IsActive ? null : "Inactive in the directory")));
        result.AddRange(assets.Select(a => new PlaceCandidate("asset", a.Id, a.Tag,
            string.Join(" · ", new[] { a.Type, a.Model, a.Description }.Where(s => !string.IsNullOrEmpty(s)).Distinct()), a.SiteKey,
            places.Where(x => x.AssetId == a.Id).Select(x => $"{x.Floor} · {x.Label}").FirstOrDefault(),
            a.SiteId == floor.SiteId ? null : $"Belongs to site {a.SiteKey}")));
        result.AddRange(pools.Select(p => new PlaceCandidate("pool", p.Id, p.Sku, $"{p.Type} · {p.Description} · {p.Available + p.Outstanding} on hand", p.SiteKey,
            places.Where(x => x.PoolId == p.Id).Select(x => $"{x.Quantity} at {x.Floor} · {x.Label}").FirstOrDefault(),
            p.SiteId == floor.SiteId ? null : $"Belongs to site {p.SiteKey}")));
        return result;
    }

    // ---- changing ---------------------------------------------------------------------------------------------

    private async Task<Workspot> LoadSpotAsync(int workspotId, CancellationToken ct)
    {
        scope.EnsureWorkplaceEditor();
        var spot = await db.Workspots.Include(w => w.Floor).SingleOrDefaultAsync(w => w.Id == workspotId, ct)
                   ?? throw new DomainException("workspot.not-found", "Workspot not found.");
        if (!spot.IsActive) throw new DomainException("workspot.inactive", "This workspot is switched off. Switch it on first.");
        if (!spot.Floor!.IsActive) throw new DomainException("floor.archived", "This floor is archived.");
        return spot;
    }

    /// <summary>Puts a person at the workspot (moving them if they sit elsewhere). A seat holds one person.</summary>
    public async Task<string> AssignPersonAsync(int workspotId, int personId, CancellationToken ct = default)
    {
        var spot = await LoadSpotAsync(workspotId, ct);
        var person = await db.People.Include(p => p.Site).SingleOrDefaultAsync(p => p.Id == personId, ct)
                     ?? throw new DomainException("assignment.person", "Person not found.");
        if (!person.IsActive) throw new DomainException("assignment.person-inactive", $"{person.DisplayName} is inactive in the directory.");

        var seat = await db.WorkspotAssignments.Include(a => a.Person)
            .FirstOrDefaultAsync(a => a.WorkspotId == workspotId && a.PersonId != null && a.ReleasedAt == null, ct);
        if (seat is not null)
        {
            if (seat.PersonId == personId) return $"{person.DisplayName} already sits at {spot.Label}.";
            throw new ConflictException("assignment.seat-taken", $"{spot.Label} is taken by {seat.Person!.DisplayName}. Release that seat first.");
        }
        var current = await db.WorkspotAssignments.Include(a => a.Workspot).ThenInclude(w => w!.Floor)
            .FirstOrDefaultAsync(a => a.PersonId == personId && a.ReleasedAt == null, ct);
        return await PlaceAsync(spot, current, new WorkspotAssignment { PersonId = personId },
            "person", person.DisplayName, new { person.Username }, ct);
    }

    /// <summary>Puts a tagged asset at the workspot (moving it if it is placed elsewhere). It must belong to the floor's site.</summary>
    public async Task<string> AssignAssetAsync(int workspotId, int assetId, CancellationToken ct = default)
    {
        var spot = await LoadSpotAsync(workspotId, ct);
        var asset = await db.Assets.Include(a => a.Site).SingleOrDefaultAsync(a => a.Id == assetId, ct)
                    ?? throw new DomainException("assignment.asset", "Asset not found.");
        if (asset.RetiredAt != null) throw new DomainException("assignment.asset-retired", $"{asset.Tag} is retired.");
        if (asset.SiteId != spot.Floor!.SiteId)
            throw new DomainException("assignment.asset-site", $"{asset.Tag} belongs to site {asset.Site!.Key}, this floor is at another site. Transfer the asset first.");

        var current = await db.WorkspotAssignments.Include(a => a.Workspot).ThenInclude(w => w!.Floor)
            .FirstOrDefaultAsync(a => a.AssetId == assetId && a.ReleasedAt == null, ct);
        if (current?.WorkspotId == workspotId) return $"{asset.Tag} is already at {spot.Label}.";
        return await PlaceAsync(spot, current, new WorkspotAssignment { AssetId = assetId }, "asset", asset.Tag, null, ct);
    }

    /// <summary>Puts <paramref name="quantity"/> units of a pool at the workspot (replacing the number already there).</summary>
    public async Task<string> AssignPoolAsync(int workspotId, int poolId, int quantity, CancellationToken ct = default)
    {
        var spot = await LoadSpotAsync(workspotId, ct);
        if (quantity is < 1 or > QuantityMax) throw new DomainException("assignment.quantity", $"The quantity must be between 1 and {QuantityMax}.");
        var pool = await db.Pools.Include(p => p.Site).SingleOrDefaultAsync(p => p.Id == poolId, ct)
                   ?? throw new DomainException("assignment.pool", "Pool not found.");
        if (pool.SiteId != spot.Floor!.SiteId)
            throw new DomainException("assignment.pool-site", $"{pool.Sku} belongs to site {pool.Site!.Key}, this floor is at another site.");

        var open = await db.WorkspotAssignments.Where(a => a.PoolId == poolId && a.ReleasedAt == null).ToListAsync(ct);
        var here = open.FirstOrDefault(a => a.WorkspotId == workspotId);
        var elsewhere = open.Where(a => a.WorkspotId != workspotId).Sum(a => a.Quantity);
        if (elsewhere + quantity > pool.OnHand)
            throw new DomainException("assignment.pool-too-many",
                $"{pool.Sku} has {pool.OnHand} unit(s) in total and {elsewhere} are already placed elsewhere.");

        if (here is not null)
        {
            if (here.Quantity == quantity) return $"{quantity} × {pool.Sku} is already at {spot.Label}.";
            var before = here.Quantity;
            here.Quantity = quantity;
            audit.Record("assignment.assign", nameof(WorkspotAssignment), here.Id, spot.Floor.SiteId,
                details: new { Kind = "pool", Name = pool.Sku, Workspot = spot.Label, Floor = spot.Floor.Name, Before = before, After = quantity });
            await db.SaveChangesAsync(ct);
            return $"{quantity} × {pool.Sku} at {spot.Label}.";
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = NewRow(spot, new WorkspotAssignment { PoolId = poolId, Quantity = quantity });
        db.WorkspotAssignments.Add(row);
        await db.SaveChangesAsync(ct);
        audit.Record("assignment.assign", nameof(WorkspotAssignment), row.Id, spot.Floor.SiteId,
            details: new { Kind = "pool", Name = pool.Sku, Workspot = spot.Label, Floor = spot.Floor.Name, Quantity = quantity });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return $"{quantity} × {pool.Sku} placed at {spot.Label}.";
    }

    /// <summary>Takes a person, asset or pool units away from a workspot. The row stays as history.</summary>
    public async Task<string> ReleaseAsync(int assignmentId, CancellationToken ct = default)
    {
        scope.EnsureWorkplaceEditor();
        var row = await db.WorkspotAssignments.Include(a => a.Workspot).ThenInclude(w => w!.Floor)
                      .Include(a => a.Person).Include(a => a.Asset).Include(a => a.Pool)
                      .SingleOrDefaultAsync(a => a.Id == assignmentId, ct)
                  ?? throw new DomainException("assignment.not-found", "Assignment not found.");
        if (row.ReleasedAt != null) throw new ConflictException("assignment.released", "This assignment was already released.");
        row.ReleasedAt = clock.UtcNow;
        row.ReleasedBy = user.Name;
        var (kind, name) = Describe(row);
        audit.Record("assignment.release", nameof(WorkspotAssignment), row.Id, row.Workspot!.Floor!.SiteId,
            details: new { Kind = kind, Name = name, Workspot = row.Workspot.Label, Floor = row.Workspot.Floor.Name, row.Quantity });
        await db.SaveChangesAsync(ct);
        return $"{name} released from {row.Workspot.Label}.";
    }

    private async Task<string> PlaceAsync(Workspot spot, WorkspotAssignment? current, WorkspotAssignment fresh, string kind, string name,
        object? extra, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        string? movedFrom = null;
        if (current is not null)
        {
            // Two saves: the filtered unique index must see the old row closed before the new one is inserted.
            current.ReleasedAt = clock.UtcNow;
            current.ReleasedBy = user.Name;
            movedFrom = $"{current.Workspot!.Floor!.Name} · {current.Workspot.Label}";
            await db.SaveChangesAsync(ct);
        }
        var row = NewRow(spot, fresh);
        db.WorkspotAssignments.Add(row);
        await db.SaveChangesAsync(ct);
        audit.Record("assignment.assign", nameof(WorkspotAssignment), row.Id, spot.Floor!.SiteId,
            details: new { Kind = kind, Name = name, Workspot = spot.Label, Floor = spot.Floor.Name, MovedFrom = movedFrom, Extra = extra });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return movedFrom is null ? $"{name} placed at {spot.Label}." : $"{name} moved from {movedFrom} to {spot.Label}.";
    }

    private WorkspotAssignment NewRow(Workspot spot, WorkspotAssignment row)
    {
        row.WorkspotId = spot.Id;
        row.AssignedAt = clock.UtcNow;
        row.AssignedBy = user.Name;
        return row;
    }

    private static (string Kind, string Name) Describe(WorkspotAssignment a) =>
        a.Person is not null ? ("person", a.Person.DisplayName) : a.Asset is not null ? ("asset", a.Asset.Tag) : ("pool", a.Pool?.Sku ?? "");
}
