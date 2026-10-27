using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Workplace;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Workplace;

public class FloorServiceTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public FloorServiceTests(AppFactory f) => _f = f;

    private Task<T> As<T>(TestUser user, Func<FloorService, Task<T>> action) =>
        _f.RunAsAsync(user, sp => action(sp.GetRequiredService<FloorService>()));

    private Task Do(TestUser user, Func<FloorService, Task> action) =>
        _f.RunAsAsync(user, sp => action(sp.GetRequiredService<FloorService>()));

    private Task<Site> SiteAsync(string key) => _f.WithDbAsync(db => Seed.SiteAsync(db, key));

    [Fact]
    public async Task Import_creates_the_floor_and_a_workspot_per_declared_seat_and_audits()
    {
        var site = await SiteAsync("FS01");
        var result = await WorkplaceSeed.ImportAsync(_f, site, "6th floor", WorkplaceSeed.Annotated(seats: 5, room: "6.010", roomName: "Aquarium"));

        Assert.Equal(5, result.Added);
        var view = (await As(TestUser.It, s => s.GetAsync(result.Floor.Id)))!;
        Assert.Equal("6th floor", view.Floor.Name);
        Assert.Equal(5, view.Spots.Count);
        Assert.All(view.Spots, s => { Assert.True(s.IsActive); Assert.Equal("Aquarium", s.RoomName); Assert.Equal("6.010", s.RoomId); });
        Assert.Contains(view.Spots, s => s.Label == "6.010-T01-S03" && s.ElementId == "f1-seat-6.010-T01-S03");
        Assert.True(await _f.WithDbAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "floor.import" && e.EntityId == result.Floor.Id.ToString())));
    }

    [Fact]
    public async Task A_floor_name_is_unique_per_site_ignoring_case_but_can_repeat_on_another_site()
    {
        var a = await SiteAsync("FS02");
        var b = await SiteAsync("FS03");
        await WorkplaceSeed.ImportAsync(_f, a, "Ground", WorkplaceSeed.Annotated());
        await Assert.ThrowsAsync<ConflictException>(() => WorkplaceSeed.ImportAsync(_f, a, "ground", WorkplaceSeed.Annotated()));
        await WorkplaceSeed.ImportAsync(_f, b, "Ground", WorkplaceSeed.Annotated());
        await Assert.ThrowsAsync<DomainException>(() => WorkplaceSeed.ImportAsync(_f, a, "  ", WorkplaceSeed.Annotated()));
    }

    [Fact]
    public async Task Only_it_and_facility_can_change_floors()
    {
        var site = await SiteAsync("FS04");
        await WorkplaceSeed.FacilityAsync(_f, TestUser.Facility);
        foreach (var user in new[] { TestUser.Manager, TestUser.Admin, TestUser.Nobody })
            await Assert.ThrowsAsync<AccessDeniedException>(() =>
                As(user, s => s.ImportAsync(site.Id, "Nope " + user.Name, WorkplaceSeed.Annotated())));
        // facility can
        var ok = await As(TestUser.Facility, s => s.ImportAsync(site.Id, "By facility", WorkplaceSeed.Annotated()));
        Assert.Equal("By facility", ok.Floor.Name);
    }

    [Fact]
    public async Task Managers_see_the_floors_of_granted_sites_only_and_not_archived_ones()
    {
        var manager = new TestUser(@"CORP\mgr.fs05", "S-1-5-21-1000-1000-1000-3501", TestGroups.Manager);
        var (mine, theirs) = await _f.WithDbAsync(async db =>
        {
            var granted = await Seed.SiteAsync(db, "FS05");
            var other = await Seed.SiteAsync(db, "FS06");
            await Seed.ManagerWithGrantsAsync(db, manager, granted);
            return (granted, other);
        });
        var ownFloor = (await WorkplaceSeed.ImportAsync(_f, mine, "Own", WorkplaceSeed.Annotated())).Floor;
        var archived = (await WorkplaceSeed.ImportAsync(_f, mine, "Old", WorkplaceSeed.Annotated())).Floor;
        var otherFloor = (await WorkplaceSeed.ImportAsync(_f, theirs, "Other", WorkplaceSeed.Annotated())).Floor;
        await Do(TestUser.It, s => s.SetActiveAsync(archived.Id, false));

        var list = await As(manager, s => s.ListAsync());
        Assert.Equal(["Own"], list.Select(f => f.Name).ToArray());
        Assert.NotNull(await As(manager, s => s.GetAsync(ownFloor.Id)));
        Assert.Null(await As(manager, s => s.GetAsync(otherFloor.Id)));
        Assert.Null(await As(manager, s => s.GetAsync(archived.Id)));

        // IT sees all of them, archived included, and nobody else sees anything
        var all = (await As(TestUser.It, s => s.ListAsync())).Select(f => f.Name).ToList();
        Assert.Contains("Old", all);
        Assert.Contains("Other", all);
        Assert.Empty(await As(TestUser.Nobody, s => s.ListAsync()));
        Assert.Null(await As(TestUser.Nobody, s => s.GetAsync(ownFloor.Id)));
    }

    [Fact]
    public async Task Replacing_the_drawing_adds_new_seats_switches_missing_ones_off_and_back_on_and_keeps_zones()
    {
        var site = await SiteAsync("FS07");
        var floor = (await WorkplaceSeed.ImportAsync(_f, site, "Replace me", WorkplaceSeed.Annotated(seats: 4))).Floor;
        var spots = (await As(TestUser.It, s => s.GetAsync(floor.Id)))!.Spots;
        var s2 = spots.Single(s => s.ElementId.EndsWith("S02"));
        await _f.RunAsAsync(TestUser.It, async sp =>
        {
            await sp.GetRequiredService<ZoneService>().CreateAsync("Zone A", [s2.Id, spots.Single(s => s.ElementId.EndsWith("S04")).Id]);
        });

        // Revised drawing: seats 1-2 only, plus a new room with 2 seats.
        var revised = WorkplaceSeed.Annotated(seats: 2).Replace("</svg>",
            """<g id="f1-table-R2-T01" data-kind="table" data-id="R2-T01" data-parent="R2"><circle id="f1-seat-R2-T01-S01" data-kind="seat" data-id="R2-T01-S01" cx="5" cy="5" r="2"/><circle id="f1-seat-R2-T01-S02" data-kind="seat" data-id="R2-T01-S02" cx="9" cy="5" r="2"/></g></svg>""");
        var r = await As(TestUser.It, s => s.ReplaceDrawingAsync(floor.Id, revised));
        Assert.Equal(2, r.Added);
        Assert.Equal(2, r.Deactivated); // S03, S04
        Assert.Equal(0, r.Reactivated);

        var after = (await As(TestUser.It, s => s.GetAsync(floor.Id)))!;
        Assert.Equal(2, after.Floor.Version);
        Assert.Equal(6, after.Spots.Count); // nothing deleted
        Assert.Equal(s2.Id, after.Spots.Single(s => s.ZoneId != null && s.IsActive).Id);
        Assert.False(after.Spots.Single(s => s.ElementId.EndsWith("T01-S04") && s.RoomId == "R1").IsActive);

        // The original drawing again: S03 and S04 come back, the R2 seats go off.
        var back = await As(TestUser.It, s => s.ReplaceDrawingAsync(floor.Id, WorkplaceSeed.Annotated(seats: 4)));
        Assert.Equal(2, back.Reactivated);
        Assert.Equal(2, back.Deactivated);
        Assert.True(await _f.WithDbAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "floor.replace" && e.EntityId == floor.Id.ToString())));
    }

    [Fact]
    public async Task Rename_and_archive_are_audited_and_checked()
    {
        var site = await SiteAsync("FS08");
        var a = (await WorkplaceSeed.ImportAsync(_f, site, "Alpha", WorkplaceSeed.Annotated())).Floor;
        await WorkplaceSeed.ImportAsync(_f, site, "Beta", WorkplaceSeed.Annotated());

        await Do(TestUser.It, s => s.RenameAsync(a.Id, "Alpha 2"));
        await Assert.ThrowsAsync<ConflictException>(() => Do(TestUser.It, s => s.RenameAsync(a.Id, "beta")));
        await Do(TestUser.It, s => s.SetActiveAsync(a.Id, false));
        await Do(TestUser.It, s => s.SetActiveAsync(a.Id, true));

        var actions = await _f.WithDbAsync(db => db.AuditEvents.Where(e => e.EntityType == nameof(Floor) && e.EntityId == a.Id.ToString())
            .Select(e => e.Action).ToListAsync());
        Assert.Contains("floor.rename", actions);
        Assert.Contains("floor.archive", actions);
        Assert.Contains("floor.restore", actions);
    }
}
