using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Workplace;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Workplace;

public class WorkspotZoneTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public WorkspotZoneTests(AppFactory f) => _f = f;

    private Task<T> As<T>(TestUser user, Func<WorkspotService, ZoneService, FloorService, Task<T>> action) =>
        _f.RunAsAsync(user, sp => action(sp.GetRequiredService<WorkspotService>(), sp.GetRequiredService<ZoneService>(), sp.GetRequiredService<FloorService>()));

    private Task<T> AsIt<T>(Func<WorkspotService, ZoneService, FloorService, Task<T>> action) => As(TestUser.It, action);

    private async Task<(Floor Floor, List<Workspot> Spots)> NewFloorAsync(string siteKey, int seats = 6, string name = "Floor")
    {
        var site = await _f.WithDbAsync(db => Seed.SiteAsync(db, siteKey));
        var floor = (await WorkplaceSeed.ImportAsync(_f, site, name, WorkplaceSeed.Annotated(seats))).Floor;
        var spots = await _f.WithDbAsync(db => db.Workspots.AsNoTracking().Where(w => w.FloorId == floor.Id).OrderBy(w => w.ElementId).ToListAsync());
        return (floor, spots);
    }

    private Task<List<Workspot>> ReloadAsync(int floorId) =>
        _f.WithDbAsync(db => db.Workspots.AsNoTracking().Where(w => w.FloorId == floorId).OrderBy(w => w.ElementId).ToListAsync());

    // ---- workspots -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Marking_shapes_of_a_plain_drawing_makes_workspots_once_with_the_element_id_as_internal_label()
    {
        var site = await _f.WithDbAsync(db => Seed.SiteAsync(db, "WS02"));
        var floor = (await WorkplaceSeed.ImportAsync(_f, site, "Plain", WorkplaceSeed.Plain())).Floor;
        Assert.Empty(await ReloadAsync(floor.Id));

        var n = await AsIt((w, t, f) => w.MarkAsync(floor.Id, ["fp-e3", "fp-e2", "fp-e3"]));
        Assert.Equal(2, n);
        var spots = await ReloadAsync(floor.Id);
        Assert.Equal(["fp-e3", "fp-e2"], spots.OrderBy(s => s.Id).Select(s => s.Label).ToArray());
        Assert.Equal(["fp-e3", "fp-e2"], spots.OrderBy(s => s.Id).Select(s => s.ElementId).ToArray());

        // marking again adds only the new shape
        Assert.Equal(1, await AsIt((w, t, f) => w.MarkAsync(floor.Id, ["fp-e4", "fp-e2"])));
        Assert.Equal(3, (await ReloadAsync(floor.Id)).Count);
    }

    [Fact]
    public async Task Marking_rejects_shapes_that_are_not_in_the_drawing_and_nothing_selected()
    {
        var site = await _f.WithDbAsync(db => Seed.SiteAsync(db, "WS04"));
        var floor = (await WorkplaceSeed.ImportAsync(_f, site, "Plain", WorkplaceSeed.Plain())).Floor;
        await Assert.ThrowsAsync<DomainException>(() => AsIt((w, t, f) => w.MarkAsync(floor.Id, ["fp-e99"])));
        await Assert.ThrowsAsync<DomainException>(() => AsIt((w, t, f) => w.MarkAsync(floor.Id, [])));
        Assert.Empty(await ReloadAsync(floor.Id));
    }

    [Fact]
    public async Task Switching_a_seat_off_takes_it_out_of_its_zone_and_back_on_does_not_rejoin()
    {
        var (floor, spots) = await NewFloorAsync("WS05");
        await AsIt((w, t, f) => t.CreateAsync("Ops", [spots[0].Id, spots[1].Id]));
        var changed = await AsIt((w, t, f) => w.SetActiveAsync([spots[0].Id], false));
        Assert.Equal(1, changed);
        var after = await ReloadAsync(floor.Id);
        Assert.False(after[0].IsActive);
        Assert.Null(after[0].ZoneId);
        Assert.NotNull(after[1].ZoneId);
        Assert.Equal(1, await AsIt((w, t, f) => w.SetActiveAsync([spots[0].Id], true)));
        Assert.Null((await ReloadAsync(floor.Id))[0].ZoneId);
        Assert.Equal(0, await AsIt((w, t, f) => w.SetActiveAsync([spots[0].Id], true))); // nothing to do
    }

    // ---- zones -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_zone_groups_seats_gets_the_next_free_colour_and_audits()
    {
        var (floor, spots) = await NewFloorAsync("WS06", seats: 6);
        var a = await AsIt((w, t, f) => t.CreateAsync("Finance", spots.Take(2).Select(s => s.Id).ToList()));
        var b = await AsIt((w, t, f) => t.CreateAsync("Sales", spots.Skip(2).Take(2).Select(s => s.Id).ToList()));
        Assert.Equal(0, a.ColorIndex);
        Assert.Equal(1, b.ColorIndex);
        Assert.Equal(2, (await ReloadAsync(floor.Id)).Count(s => s.ZoneId == a.Id));
        Assert.True(await _f.WithDbAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "zone.create" && e.EntityId == a.Id.ToString())));

        // a seat is in one zone only: grouping it again moves it
        var c = await AsIt((w, t, f) => t.CreateAsync("Moved", [spots[0].Id]));
        var reloaded = await ReloadAsync(floor.Id);
        Assert.Equal(c.Id, reloaded[0].ZoneId);
        Assert.Equal(a.Id, reloaded[1].ZoneId);
    }

    [Fact]
    public async Task Zone_names_are_unique_per_floor_and_checked()
    {
        var (floor, spots) = await NewFloorAsync("WS07");
        await AsIt((w, t, f) => t.CreateAsync("Finance", [spots[0].Id]));
        await Assert.ThrowsAsync<ConflictException>(() => AsIt((w, t, f) => t.CreateAsync("finance", [spots[1].Id])));
        await Assert.ThrowsAsync<DomainException>(() => AsIt((w, t, f) => t.CreateAsync(" ", [spots[1].Id])));
        await Assert.ThrowsAsync<DomainException>(() => AsIt((w, t, f) => t.CreateAsync("Empty", [])));
        await Assert.ThrowsAsync<AccessDeniedException>(() => As(TestUser.Manager, (w, t, f) => t.CreateAsync("Nope", [spots[1].Id])));
        // the same name on another floor is fine
        var (_, other) = await NewFloorAsync("WS08");
        await AsIt((w, t, f) => t.CreateAsync("Finance", [other[0].Id]));
    }

    [Fact]
    public async Task A_zone_can_be_renamed_recoloured_extended_emptied_and_deleted()
    {
        var (floor, spots) = await NewFloorAsync("WS09", seats: 5);
        var zone = await AsIt((w, t, f) => t.CreateAsync("Alpha", [spots[0].Id, spots[1].Id]));
        await AsIt(async (w, t, f) => { await t.RenameAsync(zone.Id, "Beta"); return 0; });
        await AsIt(async (w, t, f) => { await t.SetColorAsync(zone.Id, 5); return 0; });
        Assert.Equal(1, await AsIt((w, t, f) => t.AddAsync(zone.Id, [spots[1].Id, spots[2].Id]))); // spot 1 was already in
        var stored = await _f.WithDbAsync(db => db.FloorZones.AsNoTracking().SingleAsync(x => x.Id == zone.Id));
        Assert.Equal("Beta", stored.Name);
        Assert.Equal(5, stored.ColorIndex);
        Assert.Equal(3, (await ReloadAsync(floor.Id)).Count(s => s.ZoneId == zone.Id));

        Assert.Equal(2, await AsIt((w, t, f) => t.RemoveAsync([spots[0].Id, spots[1].Id, spots[4].Id])));
        Assert.Single(await ReloadAsync(floor.Id), s => s.ZoneId == zone.Id);

        await AsIt(async (w, t, f) => { await t.DeleteAsync(zone.Id); return 0; });
        Assert.All(await ReloadAsync(floor.Id), s => Assert.Null(s.ZoneId));
        Assert.Empty(await _f.WithDbAsync(db => db.FloorZones.Where(x => x.FloorId == floor.Id).ToListAsync()));
        var actions = await _f.WithDbAsync(db => db.AuditEvents.Where(e => e.EntityType == nameof(FloorZone) && e.EntityId == zone.Id.ToString()).Select(e => e.Action).ToListAsync());
        Assert.Contains("zone.rename", actions);
        Assert.Contains("zone.color", actions);
        Assert.Contains("zone.add", actions);
        Assert.Contains("zone.delete", actions);
        await Assert.ThrowsAsync<DomainException>(() => AsIt(async (w, t, f) => { await t.SetColorAsync(zone.Id, 1); return 0; }));
    }

    [Fact]
    public async Task Seats_of_different_floors_cannot_be_mixed_and_switched_off_seats_cannot_join()
    {
        var (floorA, a) = await NewFloorAsync("WS10");
        var (floorB, b) = await NewFloorAsync("WS11");
        await Assert.ThrowsAsync<DomainException>(() => AsIt((w, t, f) => t.CreateAsync("Mixed", [a[0].Id, b[0].Id])));
        var zone = await AsIt((w, t, f) => t.CreateAsync("A zone", [a[0].Id]));
        await Assert.ThrowsAsync<DomainException>(() => AsIt((w, t, f) => t.AddAsync(zone.Id, [b[0].Id])));
        await AsIt((w, t, f) => w.SetActiveAsync([a[1].Id], false));
        await Assert.ThrowsAsync<DomainException>(() => AsIt((w, t, f) => t.AddAsync(zone.Id, [a[1].Id])));
        await Assert.ThrowsAsync<DomainException>(() => AsIt((w, t, f) => t.CreateAsync("Ghost", [987654])));
    }

    // ---- find and focus --------------------------------------------------------------------------------------

    [Fact]
    public async Task Find_returns_rooms_and_zones_across_floors_within_the_principals_scope_but_not_seat_ids()
    {
        var manager = new TestUser(@"CORP\mgr.ws12", "S-1-5-21-1000-1000-1000-3512", TestGroups.Manager);
        var (mine, theirs) = await _f.WithDbAsync(async db =>
        {
            var granted = await Seed.SiteAsync(db, "WS12");
            var other = await Seed.SiteAsync(db, "WS13");
            await Seed.ManagerWithGrantsAsync(db, manager, granted);
            return (granted, other);
        });
        var f1 = (await WorkplaceSeed.ImportAsync(_f, mine, "Mine", WorkplaceSeed.Annotated(3, room: "Q9", roomName: "Zebra room"))).Floor;
        var f2 = (await WorkplaceSeed.ImportAsync(_f, theirs, "Theirs", WorkplaceSeed.Annotated(3, room: "Q8", roomName: "Zebra annex"))).Floor;
        var spots1 = await ReloadAsync(f1.Id);
        await AsIt((w, t, f) => t.CreateAsync("Zebra herd", spots1.Select(s => s.Id).ToList()));

        var it = await AsIt((w, t, f) => w.FindAsync("zebra"));
        Assert.Contains(it, h => h.Kind == "zone" && h.Title == "Zebra herd" && h.Seats == 3);
        Assert.Contains(it, h => h.Kind == "room" && h.FloorId == f1.Id && h.Title == "Zebra room (Q9)" && h.Seats == 3);
        Assert.Contains(it, h => h.Kind == "room" && h.FloorId == f2.Id);

        var mgr = await As(manager, (w, t, f) => w.FindAsync("zebra"));
        Assert.DoesNotContain(mgr, h => h.FloorId == f2.Id);
        Assert.Contains(mgr, h => h.FloorId == f1.Id);

        // a seat is not found by the id it has in the drawing; at least two characters; nobody else finds anything
        Assert.Empty(await AsIt((w, t, f) => w.FindAsync("Q9-T01-S02")));
        Assert.Empty(await AsIt((w, t, f) => w.FindAsync("z")));
        Assert.Empty(await As(TestUser.Nobody, (w, t, f) => w.FindAsync("zebra")));
        // LIKE wildcards in the text are literal
        Assert.Empty(await AsIt((w, t, f) => w.FindAsync("%%")));
    }

    [Fact]
    public async Task Focus_resolves_a_seat_a_room_or_a_zone_to_element_ids()
    {
        var (floor, spots) = await NewFloorAsync("WS14", seats: 4);
        var zone = await AsIt((w, t, f) => t.CreateAsync("Focus zone", [spots[0].Id, spots[2].Id]));
        Assert.Equal([spots[1].ElementId], await AsIt((w, t, f) => w.ElementIdsForFocusAsync(floor.Id, $"ws:{spots[1].Id}")));
        Assert.Equal(2, (await AsIt((w, t, f) => w.ElementIdsForFocusAsync(floor.Id, $"ws:{spots[1].Id},{spots[3].Id},x"))).Count);
        Assert.Equal(4, (await AsIt((w, t, f) => w.ElementIdsForFocusAsync(floor.Id, "room:R1"))).Count);
        Assert.Equal(2, (await AsIt((w, t, f) => w.ElementIdsForFocusAsync(floor.Id, $"zone:{zone.Id}"))).Count);
        Assert.Empty(await AsIt((w, t, f) => w.ElementIdsForFocusAsync(floor.Id, "nonsense")));
        Assert.Empty(await AsIt((w, t, f) => w.ElementIdsForFocusAsync(floor.Id, "ws:abc")));
        Assert.Empty(await AsIt((w, t, f) => w.ElementIdsForFocusAsync(floor.Id, null)));
    }
}
