using AssetTracker.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Core.Keys;

public sealed record KeyAccessBoard(long Revision, IReadOnlyList<Site> Sites, IReadOnlyList<AccessBuilding> Buildings,
    IReadOnlyList<AccessRoom> Rooms, IReadOnlyList<KeyDefinition> Definitions, IReadOnlyList<KeyRoom> RoomLinks,
    IReadOnlyList<KeyLink> Masters, IReadOnlyList<KeyCopy> Copies, IReadOnlyList<KeyLoan> Loans,
    IReadOnlyList<DigitalLock> Devices, IReadOnlyList<DigitalPermission> Permissions, IReadOnlyList<Floor> Floors,
    IReadOnlyList<AuditEvent> History, IReadOnlyDictionary<int, DateOnly> Today, DateTimeOffset Now)
{
    public IReadOnlySet<Guid> RoomsFor(Guid definition) => RoomLinks
        .Where(x => KeyAccessService.Descendants(definition, Masters).Contains(x.KeyDefinitionId))
        .Select(x => x.RoomId).ToHashSet();

    public string RoomLabel(Guid id)
    {
        var r = Rooms.Single(x => x.Id == id);
        return $"{r.Building!.Name} / {r.Code} — {r.Name}";
    }

    public string PermissionLabel(DigitalPermission p) => p.State switch
    {
        DigitalPermissionState.Revoked => "Revoked — verified",
        DigitalPermissionState.RevocationPending => "Revocation pending — device access may remain",
        DigitalPermissionState.Failed => "Failed — verify provider state",
        _ when p.EndsAt <= Now => "Expired window — revocation unverified",
        DigitalPermissionState.Pending => "Pending — configure in manufacturer app",
        _ when p.StartsAt > Now => "Scheduled — activation verified",
        _ => "Active — activation verified",
    };

    public bool Overdue(KeyLoan loan) => loan.ClosedAt is null && loan.State != KeyLoanState.Reserved
        && loan.DueDate is { } due && Today[loan.Copy!.Definition!.Building!.SiteId] >= due;

    public bool NeedsAttention(DigitalPermission p) => p.State != DigitalPermissionState.Revoked &&
        (p.State is DigitalPermissionState.RevocationPending or DigitalPermissionState.Failed || p.EndsAt <= Now || !p.Person!.IsActive);
}

public partial class KeyAccessService
{
    public async Task<KeyAccessBoard> BoardAsync(CancellationToken ct = default)
    {
        Authorize();
        // A coherent read matters when a master relationship changes during an access review.
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var revision = await db.KeyAccessRevisions.AsNoTracking().Select(x => x.Version).SingleAsync(ct);
        var sites = await db.Sites.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
        var buildings = await db.AccessBuildings.AsNoTracking().Include(x => x.Site).OrderBy(x => x.Name).ToListAsync(ct);
        var rooms = await db.AccessRooms.AsNoTracking().Include(x => x.Building).OrderBy(x => x.Code).ToListAsync(ct);
        var definitions = await db.KeyDefinitions.AsNoTracking().Include(x => x.Building).OrderBy(x => x.Code).ToListAsync(ct);
        var roomLinks = await db.KeyRooms.AsNoTracking().ToListAsync(ct);
        var masters = await db.KeyLinks.AsNoTracking().ToListAsync(ct);
        var copies = await db.KeyCopies.AsNoTracking().Include(x => x.Definition).ThenInclude(x => x!.Building)
            .Include(x => x.Lockbox).OrderBy(x => x.Tag).ToListAsync(ct);
        var loans = await db.KeyLoans.AsNoTracking().Include(x => x.Person).Include(x => x.Copy).ThenInclude(x => x!.Definition).ThenInclude(x => x!.Building)
            .OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
        var devices = await db.DigitalLocks.AsNoTracking().Include(x => x.Building).OrderBy(x => x.Name).ToListAsync(ct);
        var permissions = await db.DigitalPermissions.AsNoTracking().Include(x => x.Person).Include(x => x.Device)
            .OrderByDescending(x => x.StartsAt).ToListAsync(ct);
        var floors = await db.Floors.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name).ToListAsync(ct);
        // Dedicated Facility history, never the unrestricted IT audit endpoint.
        var history = await db.AuditEvents.AsNoTracking().Where(x => x.Action.StartsWith("keys."))
            .OrderByDescending(x => x.OccurredAt).Take(200).ToListAsync(ct);
        var today = new Dictionary<int, DateOnly>();
        foreach (var siteId in buildings.Select(x => x.SiteId).Distinct()) today[siteId] = await calendar.TodayForAsync(siteId, ct);
        await tx.CommitAsync(ct);
        return new(revision, sites, buildings, rooms, definitions, roomLinks, masters, copies, loans, devices, permissions, floors, history, today, clock.UtcNow);
    }

    public async Task<IReadOnlyList<Person>> SearchPeopleAsync(string? query, CancellationToken ct = default)
    {
        Authorize();
        query = query?.Trim();
        if (query is null || query.Length < 2) return [];
        return await db.People.AsNoTracking().Where(x => !x.IsAnonymized &&
            (x.DisplayName.Contains(query) || x.Username.Contains(query)))
            .OrderByDescending(x => x.IsActive).ThenBy(x => x.DisplayName).Take(30).ToListAsync(ct);
    }
}
