using System.Data;
using System.Text.Json;
using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Auditing;
using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Notifications;
using AssetTracker.Core.Security;
using AssetTracker.Core.Time;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Keys;

/// <summary>Facility-only operations. Every write acquires the module gate before reading mutable state.</summary>
public partial class KeyAccessService(AppDbContext db, ICurrentUser user, AuditWriter audit, IClock clock, SiteCalendar calendar)
{
    private void Authorize()
    {
        if (!user.IsFacility) throw new AccessDeniedException();
    }

    private async Task<T> Change<T>(Func<Task<T>> action, CancellationToken ct)
    {
        Authorize();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var acquired = await db.KeyAccessRevisions.Where(x => x.Id == 1)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Version, x => x.Version + 1), ct);
            Require(acquired == 1, "Key access is not initialized. Apply the KeyAccess migration before using the module.");
            var result = await action();
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return result;
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            throw new ConflictException("keys.conflict", "The record changed or its code is already in use. Refresh and try again.");
        }
        catch
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new DomainException("keys.validation", message);
    }

    private static string Text(string? value, int max = 200)
    {
        value = value?.Trim();
        Require(!string.IsNullOrEmpty(value) && value.Length <= max, $"Enter a value of 1 to {max} characters.");
        return value!;
    }

    private async Task<AccessBuilding> Building(Guid id, CancellationToken ct)
    {
        var b = await db.AccessBuildings.Include(x => x.Site).SingleOrDefaultAsync(x => x.Id == id, ct);
        Require(b is not null && b.IsActive && b.Site!.IsActive, "Select an active building and site.");
        return b!;
    }

    private async Task<Person> Borrower(int id, CancellationToken ct)
    {
        var p = await db.People.SingleOrDefaultAsync(x => x.Id == id, ct);
        Require(p is not null && p.IsActive && !p.IsAnonymized && EmailAddresses.Normalize(p.Email) is not null,
            "Select an active directory user with a valid email address.");
        return p!;
    }

    public Task<Guid> SaveBuildingAsync(Guid? id, int siteId, string name, bool active = true, CancellationToken ct = default) => Change(async () =>
    {
        Require(await db.Sites.AnyAsync(x => x.Id == siteId && x.IsActive, ct), "Select an active site.");
        var item = id is null ? new AccessBuilding { SiteId = siteId } : await db.AccessBuildings.RequiredAsync(x => x.Id == id, ct);
        Require(item.SiteId == siteId, "A building cannot move between sites.");
        if (id is null) db.AccessBuildings.Add(item);
        if (!active) await EnsureNoOpenBuildingWork(item.Id, ct);
        item.Name = Text(name); item.IsActive = active;
        audit.Record("keys.building.save", nameof(AccessBuilding), item.Id, siteId, details: new { item.Name, active });
        return item.Id;
    }, ct);

    public Task<Guid> SaveRoomAsync(Guid? id, Guid buildingId, string code, string name,
        int? floorId = null, string? drawingRoomId = null, bool active = true, CancellationToken ct = default) => Change(async () =>
    {
        var building = await Building(buildingId, ct);
        if (floorId is not null)
            Require(await db.Floors.AnyAsync(x => x.Id == floorId && x.SiteId == building.SiteId && x.IsActive, ct), "Floor must belong to this building's site.");
        Require(drawingRoomId is null || (floorId is not null && drawingRoomId.Length <= 100), "A drawing room link needs a floor and at most 100 characters.");
        var item = id is null ? new AccessRoom { BuildingId = buildingId } : await db.AccessRooms.RequiredAsync(x => x.Id == id, ct);
        Require(item.BuildingId == buildingId, "A room cannot move between buildings.");
        if (id is null) db.AccessRooms.Add(item);
        Require(active || (!await db.KeyRooms.AnyAsync(x => x.RoomId == item.Id, ct) && !await db.DigitalLocks.AnyAsync(x => x.RoomId == item.Id && x.IsActive, ct)),
            "Remove room access links and archive door locks before archiving a room.");
        item.Code = Text(code, 64).ToUpperInvariant(); item.Name = Text(name); item.IsActive = active;
        item.FloorId = floorId; item.DrawingRoomId = drawingRoomId;
        audit.Record("keys.room.save", nameof(AccessRoom), item.Id, building.SiteId, details: new { item.Code, item.Name, floorId, drawingRoomId, active });
        return item.Id;
    }, ct);

    public Task<Guid> SaveDefinitionAsync(Guid? id, Guid buildingId, string code, string name, bool active = true, CancellationToken ct = default) => Change(async () =>
    {
        var building = await Building(buildingId, ct);
        var item = id is null ? new KeyDefinition { BuildingId = buildingId } : await db.KeyDefinitions.RequiredAsync(x => x.Id == id, ct);
        Require(item.BuildingId == buildingId, "A key definition cannot move between buildings.");
        if (id is null) db.KeyDefinitions.Add(item);
        Require(active || (!await db.KeyCopies.AnyAsync(x => x.KeyDefinitionId == item.Id && x.State != KeyCopyState.Retired, ct)
            && !await db.KeyLinks.AnyAsync(x => x.MasterId == item.Id || x.SubordinateId == item.Id, ct)), "Retire copies and remove master relationships before archiving a definition.");
        item.Code = Text(code, 64).ToUpperInvariant(); item.Name = Text(name); item.IsActive = active;
        audit.Record("keys.definition.save", nameof(KeyDefinition), item.Id, building.SiteId, details: new { item.Code, item.Name, active });
        return item.Id;
    }, ct);

    public Task<bool> SetRoomAsync(Guid definitionId, Guid roomId, bool enabled, long expectedRevision, CancellationToken ct = default) => Change(async () =>
    {
        await CheckRevision(expectedRevision, ct);
        var key = await db.KeyDefinitions.RequiredAsync(x => x.Id == definitionId, ct);
        var room = await db.AccessRooms.RequiredAsync(x => x.Id == roomId, ct);
        Require(key.IsActive && room.IsActive && key.BuildingId == room.BuildingId, "Select an active key and room in the same building.");
        var building = await Building(key.BuildingId, ct);
        var before = await GraphSnapshot(ct);
        var link = await db.KeyRooms.SingleOrDefaultAsync(x => x.KeyDefinitionId == definitionId && x.RoomId == roomId, ct);
        if (enabled && link is null) db.KeyRooms.Add(new KeyRoom { KeyDefinitionId = definitionId, RoomId = roomId });
        if (!enabled && link is not null) db.KeyRooms.Remove(link);
        audit.Record("keys.room-access", nameof(KeyDefinition), definitionId, building.SiteId,
            details: new { roomId, enabled, before });
        return true;
    }, ct);

    public Task<bool> SetMasterAsync(Guid masterId, Guid subordinateId, bool enabled, long expectedRevision, CancellationToken ct = default) => Change(async () =>
    {
        await CheckRevision(expectedRevision, ct);
        var master = await db.KeyDefinitions.RequiredAsync(x => x.Id == masterId, ct);
        var sub = await db.KeyDefinitions.RequiredAsync(x => x.Id == subordinateId, ct);
        Require(masterId != subordinateId && master.IsActive && sub.IsActive && master.BuildingId == sub.BuildingId, "Select two different active keys in the same building.");
        var building = await Building(master.BuildingId, ct);
        var links = await db.KeyLinks.AsNoTracking().ToListAsync(ct);
        Require(!enabled || !Descendants(subordinateId, links).Contains(masterId), "This relationship would create a master key cycle.");
        var before = await GraphSnapshot(ct);
        var link = await db.KeyLinks.SingleOrDefaultAsync(x => x.MasterId == masterId && x.SubordinateId == subordinateId, ct);
        if (enabled && link is null) db.KeyLinks.Add(new KeyLink { MasterId = masterId, SubordinateId = subordinateId });
        if (!enabled && link is not null) db.KeyLinks.Remove(link);
        audit.Record("keys.master-access", nameof(KeyDefinition), masterId, building.SiteId, details: new { subordinateId, enabled, before });
        return true;
    }, ct);

    private async Task CheckRevision(long expected, CancellationToken ct)
    {
        // The gate has already advanced the revision. Refuse a confirmation made against stale access information.
        Require(await db.KeyAccessRevisions.Where(x => x.Id == 1).Select(x => x.Version).SingleAsync(ct) == expected + 1,
            "Access information changed. Refresh and review the affected rooms and holders again.");
    }

    private async Task<object> GraphSnapshot(CancellationToken ct) => new
    {
        rooms = await db.KeyRooms.AsNoTracking().ToListAsync(ct),
        masters = await db.KeyLinks.AsNoTracking().ToListAsync(ct),
        outstanding = await db.KeyLoans.Where(x => x.ClosedAt == null).Select(x => new { x.Id, x.CopyId, x.PersonId }).ToListAsync(ct),
    };

    internal static HashSet<Guid> Descendants(Guid id, IReadOnlyList<KeyLink> links)
    {
        var visited = new HashSet<Guid>(); var todo = new Stack<Guid>(); todo.Push(id);
        while (todo.TryPop(out var next))
            if (visited.Add(next)) foreach (var child in links.Where(x => x.MasterId == next)) todo.Push(child.SubordinateId);
        return visited;
    }

    private async Task<List<Guid>> EffectiveRooms(Guid definitionId, CancellationToken ct)
    {
        var keys = Descendants(definitionId, await db.KeyLinks.AsNoTracking().ToListAsync(ct));
        return await db.KeyRooms.Where(x => keys.Contains(x.KeyDefinitionId)).Select(x => x.RoomId).Distinct().ToListAsync(ct);
    }

    private async Task EnsureNoOpenBuildingWork(Guid id, CancellationToken ct)
    {
        Require(!await db.KeyLoans.AnyAsync(x => x.Copy!.Definition!.BuildingId == id && x.ClosedAt == null, ct)
            && !await db.DigitalPermissions.AnyAsync(x => x.Device!.BuildingId == id && x.State != DigitalPermissionState.Revoked, ct),
            "Close key loans and verify all digital revocations before archiving the building.");
    }
}
