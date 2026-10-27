using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Workplace;
using AssetTracker.Tests.Infrastructure;
using AssetTracker.Tests.Reporting;
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

    private async Task<Setup> NewFloorAsync(string key, int seats = 4)
    {
        var site = await _f.WithDbAsync(db => Seed.SiteAsync(db, key));
        var floor = (await WorkplaceSeed.ImportAsync(_f, site, "Floor", WorkplaceSeed.Annotated(seats, room: "A1", roomName: "Annex"))).Floor;
        var spots = await _f.WithDbAsync(db => db.Workspots.AsNoTracking().Where(w => w.FloorId == floor.Id).OrderBy(w => w.ElementId).ToListAsync());
        return new Setup(site, floor, spots);
    }

    private Task<List<WorkspotAssignment>> RowsAsync(Func<WorkspotAssignment, bool> where) =>
        _f.WithDbAsync(async db => (await db.WorkspotAssignments.AsNoTracking().ToListAsync()).Where(where).ToList());

    // ---- people ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_person_sits_at_one_seat_and_a_seat_holds_one_person()
    {
        var s = await NewFloorAsync("AS01");
        var (anna, bob) = await _f.WithDbAsync(async db => (await Seed.PersonAsync(db, "as01.anna"), await Seed.PersonAsync(db, "as01.bob")));

        var msg = await AsIt(a => a.AssignPersonAsync(s.Spots[0].Id, anna.Id));
        Assert.Contains("placed at", msg);
        Assert.Contains("already sits", await AsIt(a => a.AssignPersonAsync(s.Spots[0].Id, anna.Id)));
        var taken = await Assert.ThrowsAsync<ConflictException>(() => AsIt(a => a.AssignPersonAsync(s.Spots[0].Id, bob.Id)));
        Assert.Contains("as01.anna", taken.Message);
        Assert.Single(await RowsAsync(r => r.WorkspotId == s.Spots[0].Id && r.ReleasedAt == null));
        Assert.True(await _f.WithDbAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "assignment.assign" && e.DetailsJson!.Contains("as01.anna"))));
    }

    [Fact]
    public async Task Placing_a_person_elsewhere_moves_them_and_keeps_the_old_row_as_history()
    {
        var s = await NewFloorAsync("AS02");
        var anna = await _f.WithDbAsync(db => Seed.PersonAsync(db, "as02.anna"));
        await AsIt(a => a.AssignPersonAsync(s.Spots[0].Id, anna.Id));
        var msg = await AsIt(a => a.AssignPersonAsync(s.Spots[1].Id, anna.Id));

        Assert.Contains("moved from", msg);
        var rows = await RowsAsync(r => r.PersonId == anna.Id);
        Assert.Equal(2, rows.Count);
        var old = Assert.Single(rows, r => r.WorkspotId == s.Spots[0].Id);
        Assert.NotNull(old.ReleasedAt);
        Assert.NotNull(old.ReleasedBy);
        Assert.Null(Assert.Single(rows, r => r.WorkspotId == s.Spots[1].Id).ReleasedAt);
    }

    [Fact]
    public async Task Inactive_people_unknown_people_and_switched_off_seats_are_refused()
    {
        var s = await NewFloorAsync("AS03");
        var gone = await _f.WithDbAsync(db => Seed.PersonAsync(db, "as03.gone", active: false));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.AssignPersonAsync(s.Spots[0].Id, gone.Id)));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.AssignPersonAsync(s.Spots[0].Id, 987654)));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.AssignPersonAsync(987654, gone.Id)));

        var anna = await _f.WithDbAsync(db => Seed.PersonAsync(db, "as03.anna"));
        await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<WorkspotService>().SetActiveAsync([s.Spots[1].Id], false));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.AssignPersonAsync(s.Spots[1].Id, anna.Id)));
    }

    // ---- assets ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_asset_is_in_one_place_and_moves_when_placed_elsewhere()
    {
        var s = await NewFloorAsync("AS04");
        var asset = await _f.WithDbAsync(db => Seed.AssetAsync(db, "AS04-DT-1", s.Site, BuiltIn.Desktop));

        await AsIt(a => a.AssignAssetAsync(s.Spots[0].Id, asset.Id));
        await AsIt(a => a.AssignAssetAsync(s.Spots[2].Id, asset.Id));
        Assert.Contains("already at", await AsIt(a => a.AssignAssetAsync(s.Spots[2].Id, asset.Id)));

        var rows = await RowsAsync(r => r.AssetId == asset.Id);
        Assert.Equal(2, rows.Count);
        Assert.Equal(s.Spots[2].Id, Assert.Single(rows, r => r.ReleasedAt == null).WorkspotId);

        // several assets can share a seat
        var second = await _f.WithDbAsync(db => Seed.AssetAsync(db, "AS04-SCR-1", s.Site, BuiltIn.Peripheral));
        await AsIt(a => a.AssignAssetAsync(s.Spots[2].Id, second.Id));
        var detail = (await AsIt(a => a.GetSpotAsync(s.Spots[2].Id)))!;
        Assert.Equal(["AS04-DT-1", "AS04-SCR-1"], detail.Assets.Select(x => x.Tag).Order().ToArray());
    }

    [Fact]
    public async Task Assets_of_another_site_and_retired_assets_cannot_be_placed()
    {
        var s = await NewFloorAsync("AS05");
        var other = await _f.WithDbAsync(db => Seed.SiteAsync(db, "AS06"));
        var foreign = await _f.WithDbAsync(db => Seed.AssetAsync(db, "AS06-DT-1", other, BuiltIn.Desktop));
        var retired = await _f.WithDbAsync(async db =>
        {
            var a = await Seed.AssetAsync(db, "AS05-OLD-1", s.Site, BuiltIn.Desktop);
            a.RetiredAt = DateTimeOffset.UtcNow;
            a.State = AssetState.Retired;
            await db.SaveChangesAsync();
            return a;
        });
        var ex = await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.AssignAssetAsync(s.Spots[0].Id, foreign.Id)));
        Assert.Contains("AS06", ex.Message);
        await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.AssignAssetAsync(s.Spots[0].Id, retired.Id)));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.AssignAssetAsync(s.Spots[0].Id, 987654)));
    }

    // ---- pools -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Pool_units_are_placed_per_seat_replace_the_number_and_cannot_exceed_what_exists()
    {
        var s = await NewFloorAsync("AS07");
        var pool = await _f.WithDbAsync(db => Seed.PoolAsync(db, "HS-AS07", s.Site, 5));

        await AsIt(a => a.AssignPoolAsync(s.Spots[0].Id, pool.Id, 2));
        await AsIt(a => a.AssignPoolAsync(s.Spots[1].Id, pool.Id, 3));
        var ex = await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.AssignPoolAsync(s.Spots[2].Id, pool.Id, 1)));
        Assert.Contains("5 unit", ex.Message);
        // changing the number at a seat replaces it (3 → 1 frees units for another seat)
        await AsIt(a => a.AssignPoolAsync(s.Spots[1].Id, pool.Id, 1));
        await AsIt(a => a.AssignPoolAsync(s.Spots[2].Id, pool.Id, 2));
        Assert.Equal(5, (await RowsAsync(r => r.PoolId == pool.Id && r.ReleasedAt == null)).Sum(r => r.Quantity));

        foreach (var bad in new[] { 0, -1, AssignmentService.QuantityMax + 1 })
            await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.AssignPoolAsync(s.Spots[3].Id, pool.Id, bad)));
        // placing units never changes stock
        Assert.Equal(5, (await _f.WithDbAsync(db => db.Pools.AsNoTracking().SingleAsync(p => p.Id == pool.Id))).Available);
        Assert.Empty(await _f.WithDbAsync(db => db.StockEvents.Where(e => e.PoolId == pool.Id).ToListAsync()));

        var other = await _f.WithDbAsync(async db => await Seed.PoolAsync(db, "HS-AS07-X", await Seed.SiteAsync(db, "AS08"), 5));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.AssignPoolAsync(s.Spots[3].Id, other.Id, 1)));
    }

    // ---- releasing -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Releasing_closes_the_row_audits_and_frees_the_thing_to_be_placed_elsewhere()
    {
        var s = await NewFloorAsync("AS09");
        var asset = await _f.WithDbAsync(db => Seed.AssetAsync(db, "AS09-DT-1", s.Site, BuiltIn.Desktop));
        await AsIt(a => a.AssignAssetAsync(s.Spots[0].Id, asset.Id));
        var row = (await RowsAsync(r => r.AssetId == asset.Id)).Single();

        Assert.Contains("released", await AsIt(a => a.ReleaseAsync(row.Id)));
        Assert.NotNull((await RowsAsync(r => r.Id == row.Id)).Single().ReleasedAt);
        await Assert.ThrowsAsync<ConflictException>(() => AsIt(a => a.ReleaseAsync(row.Id)));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.ReleaseAsync(987654)));
        Assert.True(await _f.WithDbAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "assignment.release" && e.EntityId == row.Id.ToString())));

        // placing it again is a plain placement, not a move
        Assert.Contains("placed at", await AsIt(a => a.AssignAssetAsync(s.Spots[1].Id, asset.Id)));
        Assert.Null(await AsIt(a => a.LocationOfAssetAsync(987654)));
    }

    [Fact]
    public async Task A_seat_with_people_or_items_cannot_be_switched_off()
    {
        var s = await NewFloorAsync("AS10");
        var anna = await _f.WithDbAsync(db => Seed.PersonAsync(db, "as10.anna"));
        await AsIt(a => a.AssignPersonAsync(s.Spots[0].Id, anna.Id));
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<WorkspotService>().SetActiveAsync([s.Spots[0].Id], false)));
        Assert.Contains("Release", ex.Message);
        var row = (await RowsAsync(r => r.PersonId == anna.Id)).Single();
        await AsIt(a => a.ReleaseAsync(row.Id));
        Assert.Equal(1, await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<WorkspotService>().SetActiveAsync([s.Spots[0].Id], false)));
    }

    // ---- access ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_it_and_facility_change_assignments_and_managers_read_their_sites_only()
    {
        var manager = new TestUser(@"CORP\mgr.as11", "S-1-5-21-1000-1000-1000-3811", TestGroups.Manager);
        var (mine, theirs) = await _f.WithDbAsync(async db =>
        {
            var a = await Seed.SiteAsync(db, "AS11");
            var b = await Seed.SiteAsync(db, "AS12");
            await Seed.ManagerWithGrantsAsync(db, manager, a);
            return (a, b);
        });
        var own = (await WorkplaceSeed.ImportAsync(_f, mine, "Own", WorkplaceSeed.Annotated(2))).Floor;
        var foreign = (await WorkplaceSeed.ImportAsync(_f, theirs, "Foreign", WorkplaceSeed.Annotated(2))).Floor;
        var ownSpot = await _f.WithDbAsync(db => db.Workspots.AsNoTracking().FirstAsync(w => w.FloorId == own.Id));
        var foreignSpot = await _f.WithDbAsync(db => db.Workspots.AsNoTracking().FirstAsync(w => w.FloorId == foreign.Id));
        var anna = await _f.WithDbAsync(db => Seed.PersonAsync(db, "as11.anna"));
        await WorkplaceSeed.FacilityAsync(_f, TestUser.Facility);

        foreach (var user in new[] { manager, TestUser.Admin, TestUser.Nobody })
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => As(user, a => a.AssignPersonAsync(ownSpot.Id, anna.Id)));
            await Assert.ThrowsAsync<AccessDeniedException>(() => As(user, a => a.SearchAsync(own.Id, "anna")));
        }
        await As(TestUser.Facility, a => a.AssignPersonAsync(ownSpot.Id, anna.Id));

        Assert.NotNull(await As(manager, a => a.GetSpotAsync(ownSpot.Id)));
        Assert.Null(await As(manager, a => a.GetSpotAsync(foreignSpot.Id)));
        Assert.Null(await As(TestUser.Nobody, a => a.GetSpotAsync(ownSpot.Id)));
        Assert.Equal("as11.anna", (await As(manager, a => a.GetSpotAsync(ownSpot.Id)))!.Person!.Username);
    }

    // ---- search ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Search_lists_people_assets_and_pools_with_where_they_are_and_why_not_here()
    {
        var s = await NewFloorAsync("AS13");
        var other = await _f.WithDbAsync(db => Seed.SiteAsync(db, "AS14"));
        var (anna, left) = await _f.WithDbAsync(async db => (await Seed.PersonAsync(db, "zq13.anna"), await Seed.PersonAsync(db, "zq13.gone", active: false)));
        var mine = await _f.WithDbAsync(db => Seed.AssetAsync(db, "ZQ13-DT-1", s.Site, BuiltIn.Desktop));
        var foreign = await _f.WithDbAsync(db => Seed.AssetAsync(db, "ZQ13-DT-2", other, BuiltIn.Desktop));
        var pool = await _f.WithDbAsync(db => Seed.PoolAsync(db, "ZQ13-HS", s.Site, 4));
        await AsIt(a => a.AssignPersonAsync(s.Spots[0].Id, anna.Id));
        await AsIt(a => a.AssignAssetAsync(s.Spots[1].Id, mine.Id));
        await AsIt(a => a.AssignPoolAsync(s.Spots[1].Id, pool.Id, 2));

        var found = await AsIt(a => a.SearchAsync(s.Floor.Id, "zq13"));
        var annaRow = Assert.Single(found, c => c.Kind == "person" && c.Id == anna.Id);
        Assert.EndsWith(s.Spots[0].Label, annaRow.Place);
        Assert.Null(annaRow.Reason);
        Assert.Equal("Inactive in the directory", Assert.Single(found, c => c.Id == left.Id && c.Kind == "person").Reason);
        Assert.EndsWith(s.Spots[1].Label, Assert.Single(found, c => c.Kind == "asset" && c.Id == mine.Id).Place);
        Assert.Contains("AS14", Assert.Single(found, c => c.Kind == "asset" && c.Id == foreign.Id).Reason);
        Assert.StartsWith("2 at", Assert.Single(found, c => c.Kind == "pool").Place);
        Assert.Empty(await AsIt(a => a.SearchAsync(s.Floor.Id, "z")));
    }

    [Fact]
    public async Task Search_never_finds_an_asset_by_a_sensitive_field_value()
    {
        var s = await NewFloorAsync("AS15");
        var line = await _f.WithDbAsync(async db =>
        {
            var a = await Seed.AssetAsync(db, "AS15-SIM-1", s.Site, BuiltIn.PhoneNumber);
            await Seed.ValueAsync(db, a, BuiltIn.LinePucField, "PUC-SECRET-9931");
            return a;
        });
        Assert.Empty(await AsIt(a => a.SearchAsync(s.Floor.Id, "SECRET-9931")));
        Assert.Contains(await AsIt(a => a.SearchAsync(s.Floor.Id, "AS15-SIM")), c => c.Id == line.Id);
    }

    // ---- hints and occupancy ---------------------------------------------------------------------------------

    [Fact]
    public async Task Hints_flag_a_loan_to_someone_else_a_person_who_left_and_items_that_are_retired_or_unavailable()
    {
        var s = await NewFloorAsync("AS16", seats: 6);
        await WorkplaceSeed.FacilityAsync(_f, TestUser.Facility);
        var (anna, bob, leaver) = await _f.WithDbAsync(async db =>
            (await Seed.PersonAsync(db, "as16.anna"), await Seed.PersonAsync(db, "as16.bob"), await Seed.PersonAsync(db, "as16.leaver")));
        var (lent, own, unavailable, retired) = await _f.WithDbAsync(async db =>
        {
            var l = await Seed.AssetAsync(db, "AS16-LT-1", s.Site, BuiltIn.Laptop, AssetState.CheckedOut);
            var o = await Seed.AssetAsync(db, "AS16-LT-2", s.Site, BuiltIn.Laptop, AssetState.CheckedOut);
            var u = await Seed.AssetAsync(db, "AS16-DT-1", s.Site, BuiltIn.Desktop, AssetState.Unavailable);
            var r = await Seed.AssetAsync(db, "AS16-DT-2", s.Site, BuiltIn.Desktop);
            return (l, o, u, r);
        });
        var holder = await _f.WithDbAsync(async db =>
        {
            var h = await ReportSeed.HolderAsync(db, "as16.holder", "Holder AS16");
            await ReportSeed.LoanAsync(db, h, asset: lent);
            await ReportSeed.LoanAsync(db, bob, asset: own);
            return h;
        });

        await AsIt(a => a.AssignPersonAsync(s.Spots[0].Id, anna.Id));
        await AsIt(a => a.AssignAssetAsync(s.Spots[0].Id, lent.Id));      // on loan to the holder, anna sits here: hint
        await AsIt(a => a.AssignPersonAsync(s.Spots[1].Id, bob.Id));
        await AsIt(a => a.AssignAssetAsync(s.Spots[1].Id, own.Id));       // on loan to bob, bob sits here: fine
        await AsIt(a => a.AssignPersonAsync(s.Spots[2].Id, leaver.Id));
        await AsIt(a => a.AssignAssetAsync(s.Spots[3].Id, unavailable.Id));
        await AsIt(a => a.AssignAssetAsync(s.Spots[3].Id, retired.Id));
        await _f.WithDbAsync(async db =>
        {
            (await db.People.SingleAsync(p => p.Id == leaver.Id)).IsActive = false;
            var r = await db.Assets.SingleAsync(a => a.Id == retired.Id);
            r.RetiredAt = DateTimeOffset.UtcNow;
            r.State = AssetState.Retired;
            await db.SaveChangesAsync();
        });

        var hints = await AsIt(a => a.HintsAsync(s.Floor.Id));
        Assert.Contains(hints, h => h.Kind == "loan-mismatch" && h.WorkspotId == s.Spots[0].Id && h.Text.Contains("Holder AS16") && h.Text.Contains("as16.anna"));
        Assert.DoesNotContain(hints, h => h.Kind == "loan-mismatch" && h.WorkspotId == s.Spots[1].Id);
        Assert.Contains(hints, h => h.Kind == "person-left" && h.WorkspotId == s.Spots[2].Id);
        Assert.Contains(hints, h => h.Kind == "asset-state" && h.Text.Contains("AS16-DT-1") && h.Text.Contains("unavailable"));
        Assert.Contains(hints, h => h.Kind == "asset-state" && h.Text.Contains("AS16-DT-2") && h.Text.Contains("retired"));

        // facility has no loan access: the holder is not named
        var asFacility = await As(TestUser.Facility, a => a.GetSpotAsync(s.Spots[0].Id));
        Assert.Contains(asFacility!.Hints, h => h.Contains("someone else") && !h.Contains("Holder AS16"));
        var asIt = await AsIt(a => a.GetSpotAsync(s.Spots[0].Id));
        Assert.Contains(asIt!.Hints, h => h.Contains("Holder AS16"));
        _ = holder;
    }

    [Fact]
    public async Task A_seat_that_left_the_drawing_but_still_holds_something_is_flagged()
    {
        var s = await NewFloorAsync("AS17", seats: 3);
        var anna = await _f.WithDbAsync(db => Seed.PersonAsync(db, "as17.anna"));
        await AsIt(a => a.AssignPersonAsync(s.Spots[2].Id, anna.Id));
        // revised drawing without the third seat: it is switched off but keeps what was placed there
        await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<FloorService>().ReplaceDrawingAsync(s.Floor.Id, WorkplaceSeed.Annotated(2, room: "A1", roomName: "Annex")));
        var hints = await AsIt(a => a.HintsAsync(s.Floor.Id));
        Assert.Contains(hints, h => h.Kind == "seat-gone" && h.WorkspotId == s.Spots[2].Id);
    }

    [Fact]
    public async Task Occupancy_counts_who_sits_where_and_how_many_items_are_placed()
    {
        var s = await NewFloorAsync("AS18");
        var anna = await _f.WithDbAsync(db => Seed.PersonAsync(db, "as18.anna"));
        var asset = await _f.WithDbAsync(db => Seed.AssetAsync(db, "AS18-DT-1", s.Site, BuiltIn.Desktop));
        var pool = await _f.WithDbAsync(db => Seed.PoolAsync(db, "HS-AS18", s.Site, 3));
        await AsIt(a => a.AssignPersonAsync(s.Spots[0].Id, anna.Id));
        await AsIt(a => a.AssignAssetAsync(s.Spots[0].Id, asset.Id));
        await AsIt(a => a.AssignPoolAsync(s.Spots[1].Id, pool.Id, 1));

        var occ = await AsIt(a => a.OccupancyAsync(s.Floor.Id));
        Assert.Equal(new SpotOccupancy("as18.anna", false, 1), occ[s.Spots[0].Id]);
        Assert.Equal(new SpotOccupancy(null, false, 1), occ[s.Spots[1].Id]);
        Assert.False(occ.ContainsKey(s.Spots[2].Id));
    }

    [Fact]
    public async Task Placed_people_assets_and_pools_are_found_on_the_map_search_within_scope()
    {
        var s = await NewFloorAsync("AS19");
        var anna = await _f.WithDbAsync(db => Seed.PersonAsync(db, "yx19.anna"));
        var asset = await _f.WithDbAsync(db => Seed.AssetAsync(db, "YX19-DT-1", s.Site, BuiltIn.Desktop));
        var pool = await _f.WithDbAsync(db => Seed.PoolAsync(db, "YX19-HS", s.Site, 3));
        var unplaced = await _f.WithDbAsync(db => Seed.PersonAsync(db, "yx19.unplaced"));
        await AsIt(a => a.AssignPersonAsync(s.Spots[0].Id, anna.Id));
        await AsIt(a => a.AssignAssetAsync(s.Spots[1].Id, asset.Id));
        await AsIt(a => a.AssignPoolAsync(s.Spots[2].Id, pool.Id, 2));

        var hits = await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<WorkspotService>().FindAsync("yx19"));
        Assert.Contains(hits, h => h.Kind == "person" && h.Title == "yx19.anna" && h.Focus == $"ws:{s.Spots[0].Id}");
        Assert.Contains(hits, h => h.Kind == "asset" && h.Title == "YX19-DT-1" && h.Focus == $"ws:{s.Spots[1].Id}");
        Assert.Contains(hits, h => h.Kind == "pool" && h.Title == "YX19-HS" && h.Subtitle!.StartsWith("2 at"));
        Assert.DoesNotContain(hits, h => h.Title == "yx19.unplaced"); // not placed: nothing to show
        Assert.Empty(await _f.RunAsAsync(TestUser.Nobody, sp => sp.GetRequiredService<WorkspotService>().FindAsync("yx19")));
        _ = unplaced;
    }

    [Fact]
    public async Task Locations_of_assets_and_pools_follow_the_scope()
    {
        var manager = new TestUser(@"CORP\mgr.as20", "S-1-5-21-1000-1000-1000-3820", TestGroups.Manager);
        var (mine, theirs) = await _f.WithDbAsync(async db =>
        {
            var a = await Seed.SiteAsync(db, "AS20");
            var b = await Seed.SiteAsync(db, "AS21");
            await Seed.ManagerWithGrantsAsync(db, manager, a);
            return (a, b);
        });
        var f1 = (await WorkplaceSeed.ImportAsync(_f, mine, "Own", WorkplaceSeed.Annotated(2))).Floor;
        var f2 = (await WorkplaceSeed.ImportAsync(_f, theirs, "Foreign", WorkplaceSeed.Annotated(2))).Floor;
        var spot1 = await _f.WithDbAsync(db => db.Workspots.AsNoTracking().FirstAsync(w => w.FloorId == f1.Id));
        var spot2 = await _f.WithDbAsync(db => db.Workspots.AsNoTracking().FirstAsync(w => w.FloorId == f2.Id));
        var (a1, a2, pool) = await _f.WithDbAsync(async db =>
            (await Seed.AssetAsync(db, "AS20-DT-1", mine, BuiltIn.Desktop), await Seed.AssetAsync(db, "AS21-DT-1", theirs, BuiltIn.Desktop), await Seed.PoolAsync(db, "HS-AS20", mine, 4)));
        await AsIt(a => a.AssignAssetAsync(spot1.Id, a1.Id));
        await AsIt(a => a.AssignAssetAsync(spot2.Id, a2.Id));
        await AsIt(a => a.AssignPoolAsync(spot1.Id, pool.Id, 3));

        var place = await As(manager, a => a.LocationOfAssetAsync(a1.Id));
        Assert.Equal((f1.Id, spot1.Id), (place!.FloorId, place.WorkspotId));
        Assert.Null(await As(manager, a => a.LocationOfAssetAsync(a2.Id)));
        Assert.NotNull(await AsIt(a => a.LocationOfAssetAsync(a2.Id)));
        var places = await As(manager, a => a.LocationsOfPoolAsync(pool.Id));
        Assert.Equal(3, Assert.Single(places).Quantity);
        Assert.Empty(await As(TestUser.Nobody, a => a.LocationsOfPoolAsync(pool.Id)));
    }
}
