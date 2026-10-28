using System.Net;
using System.Text.Json;
using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Workplace;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Workplace;

/// <summary>The desk view: the per-slot search (<see cref="AssignmentService.DeskSearchAsync"/>), Replace and its endpoints.</summary>
public class DeskViewTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public DeskViewTests(AppFactory f) => _f = f;

    private Task<T> As<T>(TestUser user, Func<AssignmentService, Task<T>> action) =>
        _f.RunAsAsync(user, sp => action(sp.GetRequiredService<AssignmentService>()));

    private Task<T> AsIt<T>(Func<AssignmentService, Task<T>> action) => As(TestUser.It, action);

    private sealed record Setup(Site Site, Floor Floor, List<Workspot> Spots);

    private async Task<Setup> NewFloorAsync(string key, int seats = 4)
    {
        var site = await _f.WithDbAsync(db => Seed.SiteAsync(db, key));
        var floor = (await WorkplaceSeed.ImportAsync(_f, site, "Floor", WorkplaceSeed.Annotated(seats, room: "D1", roomName: "Deck"))).Floor;
        var spots = await _f.WithDbAsync(db => db.Workspots.AsNoTracking().Where(w => w.FloorId == floor.Id).OrderBy(w => w.ElementId).ToListAsync());
        return new Setup(site, floor, spots);
    }

    private Task<IReadOnlyList<DeskCandidate>> SearchAsync(Setup s, int spot, string kind, string? q) =>
        AsIt(a => a.DeskSearchAsync(s.Floor.Id, s.Spots[spot].Id, kind, q));

    private Task<List<WorkspotAssignment>> OpenRowsAsync(int workspotId) =>
        _f.WithDbAsync(db => db.WorkspotAssignments.AsNoTracking().Where(a => a.WorkspotId == workspotId && a.ReleasedAt == null).ToListAsync());

    // ---- search ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_empty_box_lists_what_is_not_placed_yet_at_this_site_for_that_slot_only()
    {
        var s = await NewFloorAsync("DV01");
        var other = await _f.WithDbAsync(db => Seed.SiteAsync(db, "DV02"));
        var (free, seated, visitor, gone) = await _f.WithDbAsync(async db => (
            await Seed.PersonAsync(db, "dv01.free", s.Site), await Seed.PersonAsync(db, "dv01.seated", s.Site),
            await Seed.PersonAsync(db, "dv01.visitor", other), await Seed.PersonAsync(db, "dv01.gone", s.Site, active: false)));
        var (d1, d2) = await _f.WithDbAsync(async db => (await Seed.AssetAsync(db, "DV01-DT-1", s.Site, BuiltIn.Desktop), await Seed.AssetAsync(db, "DV01-DT-2", s.Site, BuiltIn.Desktop)));
        var sc = await _f.WithDbAsync(db => Seed.AssetAsync(db, "DV01-SC-1", s.Site, BuiltIn.Screen));
        await _f.WithDbAsync(db => Seed.AssetAsync(db, "DV01-LT-1", s.Site, BuiltIn.Laptop));
        await _f.WithDbAsync(db => Seed.AssetAsync(db, "DV02-DT-9", other, BuiltIn.Desktop));
        await AsIt(a => a.AssignPersonAsync(s.Spots[1].Id, seated.Id));
        await AsIt(a => a.AssignAssetAsync(s.Spots[1].Id, d2.Id));

        var people = await SearchAsync(s, 0, "person", null);
        Assert.Equal(["dv01.free"], people.Select(c => c.Title).ToArray());
        Assert.All(people, c => Assert.Equal(DeskGroups.Free, c.Group));
        Assert.All(people, c => Assert.True(c.CanPick));
        _ = (free, visitor, gone);

        // the kind filter: only desktops in the desktop slot, only screens in the screen slot, never laptops
        Assert.Equal(["DV01-DT-1"], (await SearchAsync(s, 0, "desktop", "")).Select(c => c.Title).ToArray());
        Assert.Equal(["DV01-SC-1"], (await SearchAsync(s, 0, "screen", "  ")).Select(c => c.Title).ToArray());
        Assert.Equal("desktop", (await SearchAsync(s, 0, "desktop", null)).Single().Kind);
        Assert.DoesNotContain(await SearchAsync(s, 0, "desktop", "DV01"), c => c.Title == "DV01-LT-1" || c.Title == "DV01-SC-1");
        Assert.DoesNotContain(await SearchAsync(s, 0, "screen", "DV01"), c => c.Title.Contains("-DT-"));
        await Assert.ThrowsAsync<DomainException>(() => SearchAsync(s, 0, "pool", null));
        _ = (d1, sc);
    }

    [Fact]
    public async Task Search_results_are_ranked_free_first_then_elsewhere_then_here_then_what_cannot_be_placed()
    {
        var s = await NewFloorAsync("DV03");
        var other = await _f.WithDbAsync(db => Seed.SiteAsync(db, "DV04"));
        var otherFloor = (await WorkplaceSeed.ImportAsync(_f, s.Site, "Upstairs", WorkplaceSeed.Annotated(2, room: "U1", roomName: "Loft"))).Floor;
        var upstairs = await _f.WithDbAsync(db => db.Workspots.AsNoTracking().Where(w => w.FloorId == otherFloor.Id).OrderBy(w => w.ElementId).FirstAsync());
        var (aHere, bElsewhere, cFree, dVisitor, eGone) = await _f.WithDbAsync(async db => (
            await Seed.PersonAsync(db, "rk03.a-here", s.Site), await Seed.PersonAsync(db, "rk03.b-elsewhere", s.Site),
            await Seed.PersonAsync(db, "rk03.c-free", s.Site), await Seed.PersonAsync(db, "rk03.d-visitor", other),
            await Seed.PersonAsync(db, "rk03.e-gone", s.Site, active: false)));
        await AsIt(a => a.AssignPersonAsync(s.Spots[0].Id, aHere.Id));
        await AsIt(a => a.AssignPersonAsync(upstairs.Id, bElsewhere.Id));

        var found = await SearchAsync(s, 0, "person", "rk03");
        Assert.Equal(["rk03.c-free", "rk03.d-visitor", "rk03.b-elsewhere", "rk03.a-here", "rk03.e-gone"], found.Select(c => c.Title).ToArray());
        Assert.Equal([DeskGroups.Free, DeskGroups.Free, DeskGroups.Elsewhere, DeskGroups.Here, DeskGroups.Blocked], found.Select(c => c.Group).ToArray());
        Assert.Equal([true, true, true, false, false], found.Select(c => c.CanPick).ToArray());
        // placed elsewhere says where; the person at this desk is marked "here", not "move here"
        Assert.Equal("Upstairs · Loft", found.Single(c => c.Id == bElsewhere.Id).Place);
        Assert.Null(found.Single(c => c.Id == aHere.Id).Place);
        // people of another site can be placed (they visit); inactive people are marked and cannot be picked
        Assert.Equal("DV04", found.Single(c => c.Id == dVisitor.Id).SiteKey);
        var goneRow = found.Single(c => c.Id == eGone.Id);
        Assert.True(goneRow.Inactive);
        Assert.Equal("No longer in the directory", goneRow.Reason);
        _ = cFree;
    }

    [Fact]
    public async Task Assets_of_another_site_are_listed_but_cannot_be_picked_and_assets_at_this_desk_are_here()
    {
        var s = await NewFloorAsync("DV05");
        var other = await _f.WithDbAsync(db => Seed.SiteAsync(db, "DV06"));
        var mine = await _f.WithDbAsync(db => Seed.AssetAsync(db, "QX05-SC-1", s.Site, BuiltIn.Screen));
        var placed = await _f.WithDbAsync(db => Seed.AssetAsync(db, "QX05-SC-2", s.Site, BuiltIn.Screen));
        var here = await _f.WithDbAsync(db => Seed.AssetAsync(db, "QX05-SC-3", s.Site, BuiltIn.Screen));
        var foreign = await _f.WithDbAsync(db => Seed.AssetAsync(db, "QX05-SC-0", other, BuiltIn.Screen));
        await AsIt(a => a.AssignAssetAsync(s.Spots[2].Id, placed.Id));
        await AsIt(a => a.AssignAssetAsync(s.Spots[0].Id, here.Id));

        var found = await SearchAsync(s, 0, "screen", "QX05");
        Assert.Equal(["QX05-SC-1", "QX05-SC-2", "QX05-SC-3", "QX05-SC-0"], found.Select(c => c.Title).ToArray());
        var f = found.Single(c => c.Id == foreign.Id);
        Assert.Equal(DeskGroups.Blocked, f.Group);
        Assert.False(f.CanPick);
        Assert.Contains("DV06", f.Reason);
        Assert.Equal(DeskGroups.Here, found.Single(c => c.Id == here.Id).Group);
        Assert.Equal("Floor · Deck", found.Single(c => c.Id == placed.Id).Place);
        Assert.Equal(DeskGroups.Free, found.Single(c => c.Id == mine.Id).Group);
        // the service refuses the foreign one too
        await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.AssignAssetAsync(s.Spots[0].Id, foreign.Id)));
    }

    [Fact]
    public async Task Picking_something_placed_elsewhere_moves_it_here()
    {
        var s = await NewFloorAsync("DV07");
        var anna = await _f.WithDbAsync(db => Seed.PersonAsync(db, "dv07.anna", s.Site));
        await AsIt(a => a.AssignPersonAsync(s.Spots[3].Id, anna.Id));
        var row = (await SearchAsync(s, 0, "person", "dv07.anna")).Single();
        Assert.Equal(DeskGroups.Elsewhere, row.Group);

        Assert.Contains("moved from", await AsIt(a => a.AssignPersonAsync(s.Spots[0].Id, row.Id)));
        Assert.Single(await OpenRowsAsync(s.Spots[0].Id));
        Assert.Empty(await OpenRowsAsync(s.Spots[3].Id));
        Assert.Equal(DeskGroups.Here, (await SearchAsync(s, 0, "person", "dv07.anna")).Single().Group);
    }

    [Fact]
    public async Task Search_is_for_editors_within_the_floor_and_never_finds_by_a_sensitive_field()
    {
        var manager = new TestUser(@"CORP\mgr.dv08", "S-1-5-21-1000-1000-1000-3951", TestGroups.Manager);
        var s = await NewFloorAsync("DV08");
        var other = await NewFloorAsync("DV09");
        await _f.WithDbAsync(db => Seed.ManagerWithGrantsAsync(db, manager, s.Site));
        await WorkplaceSeed.FacilityAsync(_f, TestUser.Facility);
        var screen = await _f.WithDbAsync(db => Seed.AssetAsync(db, "DV08-SC-1", s.Site, BuiltIn.Screen));
        var field = await _f.WithDbAsync(async db =>
        {
            var def = new AssetFieldDefinition { TypeId = BuiltIn.Screen, Name = "Secret DV08", Kind = FieldKind.Text, SortOrder = 98, IsSensitive = true };
            db.FieldDefinitions.Add(def);
            await db.SaveChangesAsync();
            return def;
        });
        await _f.WithDbAsync(db => Seed.ValueAsync(db, screen, field.Id, "HUSH-7741"));

        foreach (var user in new[] { manager, TestUser.Admin, TestUser.Nobody })
            await Assert.ThrowsAsync<AccessDeniedException>(() => As(user, a => a.DeskSearchAsync(s.Floor.Id, s.Spots[0].Id, "person", null)));
        Assert.NotNull(await As(TestUser.Facility, a => a.DeskSearchAsync(s.Floor.Id, s.Spots[0].Id, "screen", null)));
        // a seat of another floor is not searched under this floor
        await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.DeskSearchAsync(s.Floor.Id, other.Spots[0].Id, "person", null)));
        Assert.Empty(await SearchAsync(s, 0, "screen", "HUSH-7741"));
        Assert.Single(await SearchAsync(s, 0, "screen", "DV08-SC"));
        await _f.WithDbAsync(async db => { (await db.FieldDefinitions.SingleAsync(x => x.Id == field.Id)).IsActive = false; await db.SaveChangesAsync(); });
    }

    // ---- replace ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Replace_swaps_the_person_in_one_step_and_audits_a_release_then_an_assign()
    {
        var s = await NewFloorAsync("DV10");
        var (anna, bob) = await _f.WithDbAsync(async db => (await Seed.PersonAsync(db, "dv10.anna", s.Site), await Seed.PersonAsync(db, "dv10.bob", s.Site)));
        await AsIt(a => a.AssignPersonAsync(s.Spots[0].Id, anna.Id));
        await AsIt(a => a.AssignPersonAsync(s.Spots[2].Id, bob.Id)); // bob sits elsewhere: he is moved
        var before = await _f.WithDbAsync(db => db.AuditEvents.MaxAsync(e => (long?)e.Id)) ?? 0;

        var message = await AsIt(a => a.ReplaceAsync(s.Spots[0].Id, "person", bob.Id));
        Assert.StartsWith("Released dv10.anna; dv10.bob moved from Floor · Deck to a desk in Deck", message);

        var open = await OpenRowsAsync(s.Spots[0].Id);
        Assert.Equal(bob.Id, Assert.Single(open).PersonId);
        Assert.Empty(await OpenRowsAsync(s.Spots[2].Id));
        Assert.Equal(3, await _f.WithDbAsync(db => db.WorkspotAssignments.CountAsync(a => (a.PersonId == anna.Id || a.PersonId == bob.Id)))); // history kept
        var audit = await _f.WithDbAsync(db => db.AuditEvents.AsNoTracking().Where(e => e.Id > before).OrderBy(e => e.Id).ToListAsync());
        Assert.Equal(["assignment.release", "assignment.assign"], audit.Select(e => e.Action).ToArray());
        Assert.Contains("dv10.anna", audit[0].DetailsJson);
        Assert.Contains("ReplacedBy", audit[0].DetailsJson);
        Assert.Contains("MovedFrom", audit[1].DetailsJson);
        Assert.Contains("Deck", audit[1].DetailsJson);
        Assert.Equal(open[0].Id.ToString(), audit[1].EntityId);

        // replacing with the one who is already there changes nothing
        Assert.Contains("already at this desk", await AsIt(a => a.ReplaceAsync(s.Spots[0].Id, "person", bob.Id)));
        Assert.Equal(open[0].Id, Assert.Single(await OpenRowsAsync(s.Spots[0].Id)).Id);
    }

    [Fact]
    public async Task Replace_swaps_the_desktop_and_refuses_screens_foreign_retired_and_inactive()
    {
        var s = await NewFloorAsync("DV11");
        var other = await _f.WithDbAsync(db => Seed.SiteAsync(db, "DV12"));
        var (d1, d2) = await _f.WithDbAsync(async db => (await Seed.AssetAsync(db, "DV11-DT-1", s.Site, BuiltIn.Desktop), await Seed.AssetAsync(db, "DV11-DT-2", s.Site, BuiltIn.Desktop)));
        var foreign = await _f.WithDbAsync(db => Seed.AssetAsync(db, "DV12-DT-1", other, BuiltIn.Desktop));
        var screen = await _f.WithDbAsync(db => Seed.AssetAsync(db, "DV11-SC-1", s.Site, BuiltIn.Screen));
        var gone = await _f.WithDbAsync(db => Seed.PersonAsync(db, "dv11.gone", s.Site, active: false));
        await AsIt(a => a.AssignAssetAsync(s.Spots[0].Id, d1.Id));
        await AsIt(a => a.AssignAssetAsync(s.Spots[0].Id, screen.Id));

        Assert.StartsWith("Released DV11-DT-1; DV11-DT-2 placed at", await AsIt(a => a.ReplaceAsync(s.Spots[0].Id, "desktop", d2.Id)));
        var open = await OpenRowsAsync(s.Spots[0].Id);
        Assert.Equal(d2.Id, open.Single(r => r.Kind == PlacementKind.Desktop).AssetId);
        Assert.Contains(open, r => r.AssetId == screen.Id); // screens are untouched
        Assert.True(await _f.WithDbAsync(db => db.WorkspotAssignments.AnyAsync(r => r.AssetId == d1.Id && r.ReleasedAt != null)));

        await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.ReplaceAsync(s.Spots[0].Id, "screen", screen.Id)));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.ReplaceAsync(s.Spots[0].Id, "desktop", screen.Id)));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.ReplaceAsync(s.Spots[0].Id, "desktop", foreign.Id)));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.ReplaceAsync(s.Spots[0].Id, "person", gone.Id)));
        // nothing changed by the refused ones
        Assert.Equal(d2.Id, (await OpenRowsAsync(s.Spots[0].Id)).Single(r => r.Kind == PlacementKind.Desktop).AssetId);

        // a desk with no desktop: Replace simply places it (one row, an assign without a release)
        var before = await _f.WithDbAsync(db => db.AuditEvents.MaxAsync(e => (long?)e.Id)) ?? 0;
        Assert.StartsWith("DV11-DT-1 placed at", await AsIt(a => a.ReplaceAsync(s.Spots[1].Id, "desktop", d1.Id)));
        Assert.Equal(["assignment.assign"], await _f.WithDbAsync(db => db.AuditEvents.Where(e => e.Id > before).Select(e => e.Action).ToArrayAsync()));
    }

    [Fact]
    public async Task Replace_moves_the_desktop_of_another_desk_without_breaking_the_one_place_rules()
    {
        // desk 0 has DT-1, desk 1 has DT-2: replacing desk 0's desktop with DT-2 closes three rows' worth of rules in one
        // transaction (desk 0's desktop, DT-2's old place) before inserting, so no filtered unique index is hit.
        var s = await NewFloorAsync("DV13");
        var (d1, d2) = await _f.WithDbAsync(async db => (await Seed.AssetAsync(db, "DV13-DT-1", s.Site, BuiltIn.Desktop), await Seed.AssetAsync(db, "DV13-DT-2", s.Site, BuiltIn.Desktop)));
        await AsIt(a => a.AssignAssetAsync(s.Spots[0].Id, d1.Id));
        await AsIt(a => a.AssignAssetAsync(s.Spots[1].Id, d2.Id));

        await AsIt(a => a.ReplaceAsync(s.Spots[0].Id, "desktop", d2.Id));
        Assert.Equal(d2.Id, Assert.Single(await OpenRowsAsync(s.Spots[0].Id)).AssetId);
        Assert.Empty(await OpenRowsAsync(s.Spots[1].Id));
        Assert.False(await _f.WithDbAsync(db => db.WorkspotAssignments.AnyAsync(r => r.AssetId == d1.Id && r.ReleasedAt == null)));
        // and back: the released one can be placed again at once
        Assert.Contains("placed at", await AsIt(a => a.AssignAssetAsync(s.Spots[1].Id, d1.Id)));
    }

    [Fact]
    public async Task Only_it_and_facility_replace_and_inactive_or_not_desk_seats_are_refused()
    {
        var manager = new TestUser(@"CORP\mgr.dv14", "S-1-5-21-1000-1000-1000-3954", TestGroups.Manager);
        var s = await NewFloorAsync("DV14");
        await _f.WithDbAsync(db => Seed.ManagerWithGrantsAsync(db, manager, s.Site));
        await WorkplaceSeed.FacilityAsync(_f, TestUser.Facility);
        var (anna, bob) = await _f.WithDbAsync(async db => (await Seed.PersonAsync(db, "dv14.anna", s.Site), await Seed.PersonAsync(db, "dv14.bob", s.Site)));
        await AsIt(a => a.AssignPersonAsync(s.Spots[0].Id, anna.Id));

        foreach (var user in new[] { manager, TestUser.Admin, TestUser.Nobody })
            await Assert.ThrowsAsync<AccessDeniedException>(() => As(user, a => a.ReplaceAsync(s.Spots[0].Id, "person", bob.Id)));
        Assert.Contains("dv14.bob", await As(TestUser.Facility, a => a.ReplaceAsync(s.Spots[0].Id, "person", bob.Id)));

        await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<WorkspotService>().SetDeskAsync([s.Spots[3].Id], false));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(a => a.ReplaceAsync(s.Spots[3].Id, "person", anna.Id)));
    }

    // ---- pages -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_desk_search_endpoint_serves_editors_and_hides_from_managers()
    {
        var manager = new TestUser(@"CORP\mgr.dv15", "S-1-5-21-1000-1000-1000-3955", TestGroups.Manager);
        var s = await NewFloorAsync("DV15");
        await _f.WithDbAsync(db => Seed.ManagerWithGrantsAsync(db, manager, s.Site));
        await WorkplaceSeed.FacilityAsync(_f, TestUser.Facility);
        var other = await _f.WithDbAsync(db => Seed.SiteAsync(db, "DV16"));
        await _f.WithDbAsync(db => Seed.PersonAsync(db, "dv15.zora", s.Site));
        await _f.WithDbAsync(db => Seed.AssetAsync(db, "DV16-DT-4", other, BuiltIn.Desktop));
        var url = $"/Workplace/Floor/{s.Floor.Id}?handler=DeskSearch&workspotId={s.Spots[0].Id}";

        foreach (var user in new[] { TestUser.It, TestUser.Facility })
        {
            var client = _f.CreateClientAs(user);
            using var people = JsonDocument.Parse(await client.GetStringAsync(url + "&kind=person"));
            var zora = Assert.Single(people.RootElement.EnumerateArray(), r => r.GetProperty("title").GetString() == "dv15.zora");
            Assert.Equal("free", zora.GetProperty("group").GetString());
            Assert.True(zora.GetProperty("canPick").GetBoolean());
            using var foreign = JsonDocument.Parse(await client.GetStringAsync(url + "&kind=desktop&q=DV16-DT"));
            var row = foreign.RootElement.EnumerateArray().Single();
            Assert.False(row.GetProperty("canPick").GetBoolean());
            Assert.Contains("DV16", row.GetProperty("reason").GetString());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await _f.CreateClientAs(manager).GetAsync(url + "&kind=person")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _f.CreateClientAs(TestUser.Nobody).GetAsync(url + "&kind=person")).StatusCode);
    }

    [Fact]
    public async Task Replace_and_release_post_from_the_desk_view_and_managers_cannot()
    {
        var manager = new TestUser(@"CORP\mgr.dv17", "S-1-5-21-1000-1000-1000-3957", TestGroups.Manager);
        var s = await NewFloorAsync("DV17");
        await _f.WithDbAsync(db => Seed.ManagerWithGrantsAsync(db, manager, s.Site));
        var (anna, bob) = await _f.WithDbAsync(async db => (await Seed.PersonAsync(db, "dv17.anna", s.Site), await Seed.PersonAsync(db, "dv17.bob", s.Site)));
        var screen = await _f.WithDbAsync(db => Seed.AssetAsync(db, "DV17-SC-1", s.Site, BuiltIn.Screen));
        await AsIt(a => a.AssignPersonAsync(s.Spots[0].Id, anna.Id));
        await AsIt(a => a.AssignAssetAsync(s.Spots[0].Id, screen.Id));
        var url = $"/Workplace/Floor/{s.Floor.Id}";
        var mgrClient = _f.CreateClientAs(manager);

        async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string handler, params (string, string)[] fields)
        {
            var form = fields.ToDictionary(f => f.Item1, f => f.Item2);
            // A manager's page has no form, so no antiforgery token: their posts are refused before the handler too.
            if (client != mgrClient) form[FormHelper.TokenField] = await FormHelper.GetTokenAsync(client, url);
            using var req = new HttpRequestMessage(HttpMethod.Post, url + "?handler=" + handler) { Content = new FormUrlEncodedContent(form) };
            req.Headers.Accept.ParseAdd("application/json");
            return await client.SendAsync(req);
        }

        var it = _f.CreateClientAs(TestUser.It);
        var replaced = await PostJsonAsync(it, "Replace", ("workspotId", s.Spots[0].Id.ToString()), ("kind", "person"), ("targetId", bob.Id.ToString()));
        Assert.Equal(HttpStatusCode.OK, replaced.StatusCode);
        using (var doc = JsonDocument.Parse(await replaced.Content.ReadAsStringAsync()))
            Assert.StartsWith("Released dv17.anna; dv17.bob placed at", doc.RootElement.GetProperty("message").GetString());
        var bad = await PostJsonAsync(it, "Replace", ("workspotId", s.Spots[0].Id.ToString()), ("kind", "screen"), ("targetId", screen.Id.ToString()));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // release (the existing endpoint) of the screen from the desk view
        var screenRow = (await OpenRowsAsync(s.Spots[0].Id)).Single(r => r.AssetId == screen.Id);
        var released = await PostJsonAsync(it, "Release", ("assignmentId", screenRow.Id.ToString()), ("workspotId", s.Spots[0].Id.ToString()));
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        Assert.DoesNotContain(await OpenRowsAsync(s.Spots[0].Id), r => r.AssetId == screen.Id);

        // a manager reads the desk view but every change is refused (no token on their page: 400; RunAsync: 404)
        var mgr = mgrClient;
        var row = (await OpenRowsAsync(s.Spots[0].Id)).Single();
        foreach (var res in new[]
                 {
                     await PostJsonAsync(mgr, "Replace", ("workspotId", s.Spots[0].Id.ToString()), ("kind", "person"), ("targetId", anna.Id.ToString())),
                     await PostJsonAsync(mgr, "Release", ("assignmentId", row.Id.ToString()), ("workspotId", s.Spots[0].Id.ToString())),
                     await PostJsonAsync(mgr, "Assign", ("workspotId", s.Spots[1].Id.ToString()), ("kind", "person"), ("targetId", anna.Id.ToString())),
                 })
            Assert.Contains(res.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.NotFound });
        Assert.Empty(await OpenRowsAsync(s.Spots[1].Id));
        Assert.Equal(bob.Id, (await OpenRowsAsync(s.Spots[0].Id)).Single().PersonId);
    }
}
