using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Keys;
using AssetTracker.Core.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Infrastructure.Demo;

internal static class DemoKeyAccessSeed
{
    public static async Task SeedAsync(IServiceProvider sp, int siteId, CancellationToken ct)
    {
        var keys = sp.GetRequiredService<KeyAccessService>();
        var db = sp.GetRequiredService<AppDbContext>();
        var now = sp.GetRequiredService<IClock>().UtcNow;
        var today = await sp.GetRequiredService<SiteCalendar>().TodayForAsync(siteId, ct);
        var person = await db.People.Where(p => p.SiteId == siteId && p.IsActive && p.Email != null).OrderBy(p => p.Id).FirstAsync(ct);
        var building = await keys.SaveBuildingAsync(null, siteId, "Building A (demo)", ct: ct);
        var finance = await keys.SaveRoomAsync(null, building, "A.1.12", "Finance", ct: ct);
        var server = await keys.SaveRoomAsync(null, building, "A.0.02", "Server room", ct: ct);
        var fk = await keys.SaveDefinitionAsync(null, building, "FIN", "Finance key", ct: ct);
        var sk = await keys.SaveDefinitionAsync(null, building, "TECH", "Technical key", ct: ct);
        var master = await keys.SaveDefinitionAsync(null, building, "GM", "Building grand master", ct: ct);
        async Task<long> Revision() => await db.KeyAccessRevisions.Select(x => x.Version).SingleAsync(ct);
        await keys.SetRoomAsync(fk, finance, true, await Revision(), ct);
        await keys.SetRoomAsync(sk, server, true, await Revision(), ct);
        await keys.SetMasterAsync(master, fk, true, await Revision(), ct);
        await keys.SetMasterAsync(master, sk, true, await Revision(), ct);
        var box = await keys.SaveDeviceAsync(null, building, null, "Reception key box (demo)", "Fictional vendor", "Demo box", "DEMO-BOX-01", "Reception counter",
            DigitalLockKind.Lockbox, LockOpeningMethod.Pin | LockOpeningMethod.BluetoothApp, ct: ct);
        var door = await keys.SaveDeviceAsync(null, building, server, "Server room door (demo)", "Fictional vendor", "Demo lock", "DEMO-DOOR-01", "Ground floor",
            DigitalLockKind.Door, LockOpeningMethod.Pin, ct: ct);
        await keys.SaveCopyAsync(null, master, "AMS-GM-001", "Reception", box, ct: ct);
        var reserved = await keys.SaveCopyAsync(null, fk, "AMS-FIN-002", "Reception", box, ct: ct);
        await keys.LendAsync(reserved, person.Id, today.AddDays(3), "Contractor collection (demo)", true,
            now.AddMinutes(-5), now.AddHours(2), LockOpeningMethod.Pin, await Revision(), ct);
        var copy = await keys.SaveCopyAsync(null, fk, "AMS-FIN-001", "Facility cabinet", null, ct: ct);
        await keys.LendAsync(copy, person.Id, today, "Office access (demo)", false, expectedRevision: await Revision(), ct: ct);
        var lost = await keys.SaveCopyAsync(null, sk, "AMS-TECH-001", "Facility cabinet", null, ct: ct);
        var loan = await keys.LendAsync(lost, person.Id, null, "Technical duty (demo)", false, expectedRevision: await Revision(), ct: ct);
        await keys.LoanActionAsync(loan, "lost", "Reported missing; review room impact (demo)", ct: ct);
        var permission = await keys.GrantAsync(door, person.Id, now.AddMinutes(-5), now.AddDays(1), LockOpeningMethod.Pin, "Server maintenance (demo)", ct);
        await keys.PermissionActionAsync(permission, "confirm", "Fictional provider verification DEMO-01", ct);
    }
}
