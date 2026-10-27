using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Inventory;
using AssetTracker.Core.Workplace;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Workplace;

public class AssignmentTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public AssignmentTests(AppFactory f) => _f = f;

    private Task<T> As<T>(TestUser user, Func<AssignmentService, Task<T>> action) =>
        _f.RunAsAsync(user, sp => action(sp.GetRequiredService<AssignmentService>()));

    private Task<T> AsIt<T>(Func<AssignmentService, Task<T>> action) => As(TestUser.It, action);

    private sealed record Setup(Site Site, Floor Floor, List<Workspot> Spots);

    private async Task<Setup> NewFloorAsync(string key, int seats = 4, string type = "office")
    {
        var site = await _f.WithDbAsync(db => Seed.SiteAsync(db, key));
        var floor = (await WorkplaceSeed.ImportAsync(_f, site, "Floor", WorkplaceSeed.Annotated(seats, room: "A1", roomName: "Annex", type: type))).Floor;
        return new Setup(site, floor, await SpotsAsync(floor.Id));
    }

    private Task<List<Workspot>> SpotsAsync(int floorId) =>
        _f.WithDbAsync(db => db.Workspots.AsNoTracking().Where(w => w.FloorId == floorId).OrderBy(w => w.ElementId).ToListAsync());

    private Task<List<WorkspotAssignment>> RowsAsync(Func<WorkspotAssignment, bool> where) =>
        _f.WithDbAsync(async db => (await db.WorkspotAssignments.AsNoTracking().ToListAsync()).Where(where).ToList());

    private Task<IndividualAsset> DesktopAsync(Site site, string tag) => _f.WithDbAsync(db => Seed.AssetAsync(db, tag, site, BuiltIn.Desktop));
    private Task<IndividualAsset> ScreenAsync(Site site, string tag) => _f.WithDbAsync(db => Seed.AssetAsync(db, tag, site, BuiltIn.Screen));

    private async Task PutAsync(int workspotId, Task<IndividualAsset> asset)
    {
        var a = await asset;
        await AsIt(s => s.AssignAssetAsync(workspotId, a.Id));
    }

    // ---- people ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_person_sits_at_one_desk_and_a_desk_holds_one_person()
    {
        var s = await NewFloorAsync("AS01");
        var (anna, bob) = await _f.WithDbAsync(async db => (await Seed.PersonAsync(db, "as01.anna"), await Seed.PersonAsync(db, "as01.bob")));

        // messages name the desk by its room (and zone), never by the drawing's seat id
        Assert.Equal("as01.anna placed at a desk in Annex.", await AsIt(a => a.AssignPersonAsync(s.Spots[0].Id, anna.Id)));
        Assert.Equal("as01.anna already sits at this desk.", await AsIt(a => a.AssignPersonAsync(s.Spots[0].Id, anna.Id)));
        var taken = await Assert.ThrowsAsync<ConflictException>(() => AsIt(a => a.AssignPersonAsync(s.Spots[0].Id, bob.Id)));
        Assert.Equal("This desk is taken by as01.anna. Release them first.", taken.Message);
        var row = Assert.Single(await RowsAsync(r => r.WorkspotId == s.Spots[0].Id && r.ReleasedAt == null));
        Assert.Equal(PlacementKind.Person, row.Kind);
        Assert.True(await _f.WithDbAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "assignment.assign" && e.DetailsJson!.Contains("as01.anna"))));
    }

    [Fact]
    public async Task Placing_a_person_elsewhere_moves_them_and_keeps_the_old_row_as_history()
    {
        var s = await NewFloorAsync("AS02");
        var anna = await _f.WithDbAsync(db => Seed.PersonAsync(db, "as02.anna"));
        await AsIt(a => a.AssignPersonAsync(s.Spots[0].Id, anna.Id));
        await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<ZoneService>().CreateAsync("Window", [s.Spots[1].Id]));
        Assert.Equal("as02.anna moved from Floor · Annex to a desk in Window · Annex.", await AsIt(a => a.AssignPersonAsync(s.Spots[1].Id, anna.Id)));

        var rows = await RowsAsync(r => r.PersonId == anna.Id);
        Assert.Equal(2, rows.Count);
        Assert.NotNull(Assert.Single(rows, r => r.WorkspotId == s.Spots[0].Id).ReleasedAt);
        Assert.Null(Assert.Single(rows, r => r.WorkspotId == s.Spots[1].Id).ReleasedAt);
    }

    [Fact]
    public async Task Inactive_people_unknown_ids_switched_off_seats_and_seats_that_are_not_desks_are_refused()
    {
        var s = await NewFloorAsync("AS03");
        var gone = await _f.WithDbAsync(db => Seed.PersonAsync(db, "as03.gone", active: false));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.AssignPersonAsync(s.Spots[0].Id, gone.Id)));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.AssignPersonAsync(s.Spots[0].Id, 987654)));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.AssignPersonAsync(987654, gone.Id)));

        var anna = await _f.WithDbAsync(db => Seed.PersonAsync(db, "as03.anna"));
        await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<WorkspotService>().SetActiveAsync([s.Spots[1].Id], false));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.AssignPersonAsync(s.Spots[1].Id, anna.Id)));
        await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<WorkspotService>().SetDeskAsync([s.Spots[2].Id], false));
        var ex = await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.AssignPersonAsync(s.Spots[2].Id, anna.Id)));
        Assert.Contains("not a desk", ex.Message);
    }

    // ---- desktops and screens ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_desk_has_one_desktop_and_any_number_of_screens()
    {
        var s = await NewFloorAsync("AS04");
        var (d1, d2) = (await DesktopAsync(s.Site, "AS04-DT-1"), await DesktopAsync(s.Site, "AS04-DT-2"));
        var (s1, s2) = (await ScreenAsync(s.Site, "AS04-SC-1"), await ScreenAsync(s.Site, "AS04-SC-2"));

        await AsIt(a => a.AssignAssetAsync(s.Spots[0].Id, d1.Id));
        await AsIt(a => a.AssignAssetAsync(s.Spots[0].Id, s1.Id));
        await AsIt(a => a.AssignAssetAsync(s.Spots[0].Id, s2.Id));
        var ex = await Assert.ThrowsAsync<ConflictException>(() => AsIt(a => a.AssignAssetAsync(s.Spots[0].Id, d2.Id)));
        Assert.Contains("AS04-DT-1", ex.Message);

        var detail = (await AsIt(a => a.GetSpotAsync(s.Spots[0].Id)))!;
        Assert.Equal("AS04-DT-1", detail.Desktop!.Tag);
        Assert.Equal(["AS04-SC-1", "AS04-SC-2"], detail.Screens.Select(x => x.Tag).Order().ToArray());
        Assert.Equal([PlacementKind.Desktop, PlacementKind.Screen, PlacementKind.Screen],
            (await RowsAsync(r => r.WorkspotId == s.Spots[0].Id)).OrderBy(r => r.Id).Select(r => r.Kind).ToArray());
    }

    [Fact]
    public async Task A_desktop_or_screen_is_in_one_place_and_moves_when_placed_elsewhere()
    {
        var s = await NewFloorAsync("AS05");
        var desktop = await DesktopAsync(s.Site, "AS05-DT-1");
        await AsIt(a => a.AssignAssetAsync(s.Spots[0].Id, desktop.Id));
        Assert.Contains("moved from", await AsIt(a => a.AssignAssetAsync(s.Spots[2].Id, desktop.Id)));
        Assert.Contains("already at", await AsIt(a => a.AssignAssetAsync(s.Spots[2].Id, desktop.Id)));
        Assert.Equal(s.Spots[2].Id, Assert.Single(await RowsAsync(r => r.AssetId == desktop.Id && r.ReleasedAt == null)).WorkspotId);
    }

    [Fact]
    public async Task Only_desktops_and_screens_of_the_floors_site_that_are_not_retired_are_placed()
    {
        var s = await NewFloorAsync("AS06");
        var other = await _f.WithDbAsync(db => Seed.SiteAsync(db, "AS07"));
        var laptop = await _f.WithDbAsync(db => Seed.AssetAsync(db, "AS06-LT-1", s.Site, BuiltIn.Laptop));
        var foreign = await DesktopAsync(other, "AS07-DT-1");
        var retired = await _f.WithDbAsync(async db =>
        {
            var a = await Seed.AssetAsync(db, "AS06-OLD-1", s.Site, BuiltIn.Screen);
            a.RetiredAt = DateTimeOffset.UtcNow;
            a.State = AssetState.Retired;
            await db.SaveChangesAsync();
            return a;
        });
        var typeEx = await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.AssignAssetAsync(s.Spots[0].Id, laptop.Id)));
        Assert.Contains("only desktops and screens", typeEx.Message);
        Assert.Contains("AS07", (await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.AssignAssetAsync(s.Spots[0].Id, foreign.Id)))).Message);
        await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.AssignAssetAsync(s.Spots[0].Id, retired.Id)));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.AssignAssetAsync(s.Spots[0].Id, 987654)));
    }

    [Fact]
    public async Task Retiring_or_moving_a_desktop_to_another_site_takes_it_off_its_desk()
    {
        var s = await NewFloorAsync("AS08");
        var other = await _f.WithDbAsync(db => Seed.SiteAsync(db, "AS09"));
        var (retire, move) = (await DesktopAsync(s.Site, "AS08-DT-1"), await ScreenAsync(s.Site, "AS08-SC-1"));
        await AsIt(a => a.AssignAssetAsync(s.Spots[0].Id, retire.Id));
        await AsIt(a => a.AssignAssetAsync(s.Spots[1].Id, move.Id));

        await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<AssetService>().RetireAsync(retire.Id, "end of life"));
        await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<AssetService>().TransferSiteAsync(move.Id, other.Id));

        Assert.Empty(await RowsAsync(r => (r.AssetId == retire.Id || r.AssetId == move.Id) && r.ReleasedAt == null));
        var audit = await _f.WithDbAsync(db => db.AuditEvents.Where(e => e.Action == "assignment.release").Select(e => e.Reason).ToListAsync());
        Assert.Contains("Asset retired", audit);
        Assert.Contains("Asset moved to another site", audit);
    }

    // ---- releasing -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Releasing_closes_the_row_audits_and_frees_the_thing_to_be_placed_elsewhere()
    {
        var s = await NewFloorAsync("AS10");
        var desktop = await DesktopAsync(s.Site, "AS10-DT-1");
        await AsIt(a => a.AssignAssetAsync(s.Spots[0].Id, desktop.Id));
        var row = (await RowsAsync(r => r.AssetId == desktop.Id)).Single();

        Assert.Contains("released", await AsIt(a => a.ReleaseAsync(row.Id)));
        Assert.NotNull((await RowsAsync(r => r.Id == row.Id)).Single().ReleasedAt);
        await Assert.ThrowsAsync<ConflictException>(() => AsIt(a => a.ReleaseAsync(row.Id)));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.ReleaseAsync(987654)));
        Assert.True(await _f.WithDbAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "assignment.release" && e.EntityId == row.Id.ToString())));
        Assert.Contains("placed at", await AsIt(a => a.AssignAssetAsync(s.Spots[1].Id, desktop.Id)));
    }

    [Fact]
    public async Task A_seat_with_something_placed_cannot_be_switched_off_or_stop_being_a_desk()
    {
        var s = await NewFloorAsync("AS11");
        var anna = await _f.WithDbAsync(db => Seed.PersonAsync(db, "as11.anna"));
        await AsIt(a => a.AssignPersonAsync(s.Spots[0].Id, anna.Id));
        await Assert.ThrowsAsync<DomainException>(() =>
            _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<WorkspotService>().SetActiveAsync([s.Spots[0].Id], false)));
        await Assert.ThrowsAsync<DomainException>(() =>
            _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<WorkspotService>().SetDeskAsync([s.Spots[0].Id], false)));
        var row = (await RowsAsync(r => r.PersonId == anna.Id)).Single();
        await AsIt(a => a.ReleaseAsync(row.Id));
        Assert.Equal(1, await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<WorkspotService>().SetDeskAsync([s.Spots[0].Id], false)));
        Assert.Equal(1, await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<WorkspotService>().SetDeskAsync([s.Spots[0].Id], true)));
        Assert.True(await _f.WithDbAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "workspot.not-desk")));
    }

    [Fact]
    public async Task Seats_in_canteens_and_meeting_rooms_are_not_desks_after_import()
    {
        var canteen = await NewFloorAsync("AS12", seats: 2, type: "canteen");
        Assert.All(canteen.Spots, w => Assert.False(w.IsDesk));
        var office = await NewFloorAsync("AS13", seats: 2);
        Assert.All(office.Spots, w => Assert.True(w.IsDesk));
        var plain = await _f.WithDbAsync(db => Seed.SiteAsync(db, "AS14"));
        var f = (await WorkplaceSeed.ImportAsync(_f, plain, "Plain", WorkplaceSeed.Plain())).Floor;
        await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<WorkspotService>().MarkAsync(f.Id, ["fp-e2"]));
        Assert.True(Assert.Single(await SpotsAsync(f.Id)).IsDesk);
    }

    // ---- access ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_it_and_facility_change_placements_and_managers_read_their_sites_only()
    {
        var manager = new TestUser(@"CORP\mgr.as15", "S-1-5-21-1000-1000-1000-3815", TestGroups.Manager);
        var (mine, theirs) = await _f.WithDbAsync(async db =>
        {
            var a = await Seed.SiteAsync(db, "AS15");
            var b = await Seed.SiteAsync(db, "AS16");
            await Seed.ManagerWithGrantsAsync(db, manager, a);
            return (a, b);
        });
        var own = (await WorkplaceSeed.ImportAsync(_f, mine, "Own", WorkplaceSeed.Annotated(2))).Floor;
        var foreign = (await WorkplaceSeed.ImportAsync(_f, theirs, "Foreign", WorkplaceSeed.Annotated(2))).Floor;
        var ownSpot = (await SpotsAsync(own.Id))[0];
        var foreignSpot = (await SpotsAsync(foreign.Id))[0];
        var anna = await _f.WithDbAsync(db => Seed.PersonAsync(db, "as15.anna"));
        await WorkplaceSeed.FacilityAsync(_f, TestUser.Facility);

        foreach (var user in new[] { manager, TestUser.Admin, TestUser.Nobody })
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => As(user, a => a.AssignPersonAsync(ownSpot.Id, anna.Id)));
            await Assert.ThrowsAsync<AccessDeniedException>(() => As(user, a => a.SearchAsync(own.Id, "anna")));
            await Assert.ThrowsAsync<AccessDeniedException>(() => As(user, a => a.PaletteAsync(own.Id, "person", null)));
        }
        await As(TestUser.Facility, a => a.AssignPersonAsync(ownSpot.Id, anna.Id));

        Assert.Equal("as15.anna", (await As(manager, a => a.GetSpotAsync(ownSpot.Id)))!.Person!.Username);
        Assert.Null(await As(manager, a => a.GetSpotAsync(foreignSpot.Id)));
        Assert.Null(await As(TestUser.Nobody, a => a.GetSpotAsync(ownSpot.Id)));
    }

    // ---- what can be placed ----------------------------------------------------------------------------------

    [Fact]
    public async Task The_palette_lists_what_is_not_placed_yet_of_the_floors_site_or_matches_a_search()
    {
        var s = await NewFloorAsync("AS17");
        var other = await _f.WithDbAsync(db => Seed.SiteAsync(db, "AS18"));
        var (here, placed, elsewhere, gone) = await _f.WithDbAsync(async db => (
            await Seed.PersonAsync(db, "pq17.here", s.Site), await Seed.PersonAsync(db, "pq17.placed", s.Site),
            await Seed.PersonAsync(db, "pq17.elsewhere", other), await Seed.PersonAsync(db, "pq17.gone", s.Site, active: false)));
        var (d1, d2, sc) = (await DesktopAsync(s.Site, "PQ17-DT-1"), await DesktopAsync(s.Site, "PQ17-DT-2"), await ScreenAsync(s.Site, "PQ17-SC-1"));
        await _f.WithDbAsync(db => Seed.AssetAsync(db, "PQ17-LT-1", s.Site, BuiltIn.Laptop));
        await AsIt(a => a.AssignPersonAsync(s.Spots[0].Id, placed.Id));
        await AsIt(a => a.AssignAssetAsync(s.Spots[0].Id, d2.Id));

        var people = await AsIt(a => a.PaletteAsync(s.Floor.Id, "person", null));
        Assert.Contains(people, c => c.Id == here.Id);
        Assert.DoesNotContain(people, c => c.Id == placed.Id || c.Id == elsewhere.Id || c.Id == gone.Id);
        // a search finds people of every site
        Assert.Contains(await AsIt(a => a.PaletteAsync(s.Floor.Id, "person", "pq17.else")), c => c.Id == elsewhere.Id);

        var desktops = await AsIt(a => a.PaletteAsync(s.Floor.Id, "desktop", null));
        Assert.Equal(["PQ17-DT-1"], desktops.Select(c => c.Title).ToArray());
        Assert.Equal(["PQ17-SC-1"], (await AsIt(a => a.PaletteAsync(s.Floor.Id, "screen", null))).Select(c => c.Title).ToArray());
        Assert.Equal("desktop", desktops[0].Kind);
        await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.PaletteAsync(s.Floor.Id, "pool", null)));
        _ = (d1, sc);
    }

    [Fact]
    public async Task The_search_box_finds_people_desktops_and_screens_with_where_they_are_and_why_not_here()
    {
        var s = await NewFloorAsync("AS19");
        var other = await _f.WithDbAsync(db => Seed.SiteAsync(db, "AS20"));
        var (anna, left) = await _f.WithDbAsync(async db => (await Seed.PersonAsync(db, "zq19.anna"), await Seed.PersonAsync(db, "zq19.gone", active: false)));
        var mine = await DesktopAsync(s.Site, "ZQ19-DT-1");
        var foreign = await ScreenAsync(other, "ZQ19-SC-2");
        await _f.WithDbAsync(db => Seed.AssetAsync(db, "ZQ19-LT-3", s.Site, BuiltIn.Laptop));
        await AsIt(a => a.AssignPersonAsync(s.Spots[0].Id, anna.Id));
        await AsIt(a => a.AssignAssetAsync(s.Spots[1].Id, mine.Id));

        var found = await AsIt(a => a.SearchAsync(s.Floor.Id, "zq19"));
        Assert.Equal("Floor · Annex", Assert.Single(found, c => c.Kind == "person" && c.Id == anna.Id).Place);
        Assert.Equal("No longer in the directory", Assert.Single(found, c => c.Id == left.Id && c.Kind == "person").Reason);
        Assert.Equal("Floor · Annex", Assert.Single(found, c => c.Kind == "desktop").Place);
        Assert.Contains("AS20", Assert.Single(found, c => c.Kind == "screen" && c.Id == foreign.Id).Reason);
        Assert.DoesNotContain(found, c => c.Title == "ZQ19-LT-3"); // laptops are not placed at desks
        Assert.Empty(await AsIt(a => a.SearchAsync(s.Floor.Id, "z")));
    }

    [Fact]
    public async Task Search_never_finds_an_asset_by_a_sensitive_field_value()
    {
        var s = await NewFloorAsync("AS21");
        // A screen type with a sensitive field, as an admin could configure it.
        var field = await _f.WithDbAsync(async db =>
        {
            var f = new AssetFieldDefinition { TypeId = BuiltIn.Screen, Name = "Secret AS21", Kind = FieldKind.Text, SortOrder = 99, IsSensitive = true };
            db.FieldDefinitions.Add(f);
            await db.SaveChangesAsync();
            return f;
        });
        var screen = await ScreenAsync(s.Site, "AS21-SC-1");
        await _f.WithDbAsync(db => Seed.ValueAsync(db, screen, field.Id, "SECRET-9931"));
        Assert.Empty(await AsIt(a => a.SearchAsync(s.Floor.Id, "SECRET-9931")));
        Assert.Empty(await AsIt(a => a.PaletteAsync(s.Floor.Id, "screen", "SECRET-9931")));
        Assert.Contains(await AsIt(a => a.SearchAsync(s.Floor.Id, "AS21-SC")), c => c.Id == screen.Id);
        await _f.WithDbAsync(async db => { (await db.FieldDefinitions.SingleAsync(x => x.Id == field.Id)).IsActive = false; await db.SaveChangesAsync(); });
    }

    // ---- flags, free desks and what the map draws ------------------------------------------------------------

    [Fact]
    public async Task Flags_a_person_who_left_a_person_on_a_disabled_seat_and_a_person_without_desktop_or_screen()
    {
        var s = await NewFloorAsync("AS22", seats: 6);
        var (complete, noScreen, nothing, leaver, onGone) = await _f.WithDbAsync(async db => (
            await Seed.PersonAsync(db, "as22.complete"), await Seed.PersonAsync(db, "as22.noscreen"), await Seed.PersonAsync(db, "as22.nothing"),
            await Seed.PersonAsync(db, "as22.leaver"), await Seed.PersonAsync(db, "as22.ongone")));
        await AsIt(a => a.AssignPersonAsync(s.Spots[0].Id, complete.Id));
        await PutAsync(s.Spots[0].Id, DesktopAsync(s.Site, "AS22-DT-1"));
        await PutAsync(s.Spots[0].Id, ScreenAsync(s.Site, "AS22-SC-1"));
        await AsIt(a => a.AssignPersonAsync(s.Spots[1].Id, noScreen.Id));
        await PutAsync(s.Spots[1].Id, DesktopAsync(s.Site, "AS22-DT-2"));
        await AsIt(a => a.AssignPersonAsync(s.Spots[2].Id, nothing.Id));
        await AsIt(a => a.AssignPersonAsync(s.Spots[3].Id, leaver.Id));
        await AsIt(a => a.AssignPersonAsync(s.Spots[5].Id, onGone.Id));
        await PutAsync(s.Spots[4].Id, ScreenAsync(s.Site, "AS22-SC-2")); // a screen at a free desk: no flag
        await _f.WithDbAsync(async db => { (await db.People.SingleAsync(p => p.Id == leaver.Id)).IsActive = false; await db.SaveChangesAsync(); });
        // a revised drawing without the sixth seat: switched off, but the person is still placed there
        await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<FloorService>().ReplaceDrawingAsync(s.Floor.Id,
            WorkplaceSeed.Annotated(5, room: "A1", roomName: "Annex")));
        var spots = await SpotsAsync(s.Floor.Id);

        var status = await AsIt(a => a.StatusAsync(spots));
        string[] FlagsOf(int i) => status.Flags.Where(f => f.WorkspotId == s.Spots[i].Id).Select(f => f.Kind).Order().ToArray();
        Assert.Empty(FlagsOf(0));
        Assert.Equal(["missing"], FlagsOf(1));
        Assert.Contains("no screen", status.Flags.Single(f => f.WorkspotId == s.Spots[1].Id).Text);
        Assert.Contains("no desktop and no screen", status.Flags.Single(f => f.WorkspotId == s.Spots[2].Id).Text);
        Assert.Equal(["missing", "person-left"], FlagsOf(3));
        Assert.Equal(["missing", "seat-disabled"], FlagsOf(5));
        Assert.Empty(FlagsOf(4));
        Assert.Equal("person-left", status.Flags[0].Kind); // most urgent first

        // desks: five active seats, four taken, one free (the one with only a screen)
        Assert.Equal((5, 4), (status.Desks, status.Seated));
        Assert.Equal([s.Spots[4].Id], status.FreeDesks.ToArray());
        Assert.Equal((2, 2), (status.Desktops, status.Screens));

        var detail = (await AsIt(a => a.GetSpotAsync(s.Spots[3].Id)))!;
        Assert.False(detail.Person!.IsActive);
        Assert.Contains(detail.Flags, f => f.Contains("no longer works here"));
    }

    [Fact]
    public async Task States_tell_the_map_who_sits_where_the_desktop_tag_and_how_many_screens()
    {
        var s = await NewFloorAsync("AS23");
        var anna = await _f.WithDbAsync(db => Seed.PersonAsync(db, "as23.anna"));
        await AsIt(a => a.AssignPersonAsync(s.Spots[0].Id, anna.Id));
        await PutAsync(s.Spots[0].Id, DesktopAsync(s.Site, "AS23-DT-1"));
        await PutAsync(s.Spots[0].Id, ScreenAsync(s.Site, "AS23-SC-1"));
        await PutAsync(s.Spots[0].Id, ScreenAsync(s.Site, "AS23-SC-2"));
        await PutAsync(s.Spots[1].Id, ScreenAsync(s.Site, "AS23-SC-3"));

        var states = await AsIt(a => a.StatesAsync(s.Spots));
        Assert.Equal(new SpotState("as23.anna", false, "AS23-DT-1", 2, false), states[s.Spots[0].Id]);
        Assert.Equal(new SpotState(null, false, null, 1, false), states[s.Spots[1].Id]);
        Assert.False(states.ContainsKey(s.Spots[2].Id));
    }

    [Fact]
    public async Task Placed_people_and_desktops_are_found_on_the_map_search_within_scope()
    {
        var s = await NewFloorAsync("AS24");
        var anna = await _f.WithDbAsync(db => Seed.PersonAsync(db, "yx24.anna"));
        var desktop = await DesktopAsync(s.Site, "YX24-DT-1");
        await _f.WithDbAsync(db => Seed.PersonAsync(db, "yx24.unplaced"));
        await AsIt(a => a.AssignPersonAsync(s.Spots[0].Id, anna.Id));
        await AsIt(a => a.AssignAssetAsync(s.Spots[1].Id, desktop.Id));

        var hits = await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<WorkspotService>().FindAsync("yx24"));
        Assert.Contains(hits, h => h.Kind == "person" && h.Title == "yx24.anna" && h.Focus == $"ws:{s.Spots[0].Id}");
        Assert.Contains(hits, h => h.Kind == "asset" && h.Title == "YX24-DT-1" && h.Focus == $"ws:{s.Spots[1].Id}");
        Assert.DoesNotContain(hits, h => h.Title == "yx24.unplaced");
        Assert.Empty(await _f.RunAsAsync(TestUser.Nobody, sp => sp.GetRequiredService<WorkspotService>().FindAsync("yx24")));
    }

    [Fact]
    public async Task The_location_of_an_asset_follows_the_scope()
    {
        var manager = new TestUser(@"CORP\mgr.as25", "S-1-5-21-1000-1000-1000-3825", TestGroups.Manager);
        var (mine, theirs) = await _f.WithDbAsync(async db =>
        {
            var a = await Seed.SiteAsync(db, "AS25");
            var b = await Seed.SiteAsync(db, "AS26");
            await Seed.ManagerWithGrantsAsync(db, manager, a);
            return (a, b);
        });
        var f1 = (await WorkplaceSeed.ImportAsync(_f, mine, "Own", WorkplaceSeed.Annotated(2))).Floor;
        var f2 = (await WorkplaceSeed.ImportAsync(_f, theirs, "Foreign", WorkplaceSeed.Annotated(2))).Floor;
        var spot1 = (await SpotsAsync(f1.Id))[0];
        var spot2 = (await SpotsAsync(f2.Id))[0];
        var (a1, a2) = (await DesktopAsync(mine, "AS25-DT-1"), await DesktopAsync(theirs, "AS26-DT-1"));
        await AsIt(a => a.AssignAssetAsync(spot1.Id, a1.Id));
        await AsIt(a => a.AssignAssetAsync(spot2.Id, a2.Id));

        var place = await As(manager, a => a.LocationOfAssetAsync(a1.Id));
        Assert.Equal((f1.Id, spot1.Id), (place!.FloorId, place.WorkspotId));
        Assert.Null(await As(manager, a => a.LocationOfAssetAsync(a2.Id)));
        Assert.NotNull(await AsIt(a => a.LocationOfAssetAsync(a2.Id)));
        Assert.Null(await AsIt(a => a.LocationOfAssetAsync(987654)));
    }
}
