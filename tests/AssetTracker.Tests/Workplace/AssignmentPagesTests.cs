using AssetTracker.Core.Security;
using System.Net;
using System.Text.Json;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Workplace;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Workplace;

public class AssignmentPagesTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public AssignmentPagesTests(AppFactory f) => _f = f;

    private sealed record Setup(Site Site, Floor Floor, List<Workspot> Spots);

    private async Task<Setup> NewFloorAsync(string key, int seats = 4)
    {
        var site = await _f.WithDbAsync(db => Seed.SiteAsync(db, key));
        var floor = (await WorkplaceSeed.ImportAsync(_f, site, "Floor", WorkplaceSeed.Annotated(seats, room: "B1", roomName: "Bay"))).Floor;
        var spots = await _f.WithDbAsync(db => db.Workspots.AsNoTracking().Where(w => w.FloorId == floor.Id).OrderBy(w => w.ElementId).ToListAsync());
        return new Setup(site, floor, spots);
    }

    private Task AssignAsync(Func<AssignmentService, Task> action) =>
        _f.RunAsAsync(TestUser.It, sp => action(sp.GetRequiredService<AssignmentService>()));

    [Fact]
    public async Task The_spot_endpoint_returns_what_sits_there_for_readers_within_scope()
    {
        var manager = new TestUser(@"CORP\mgr.ap01", "S-1-5-21-1000-1000-1000-3901", AppRoles.Manager);
        var s = await NewFloorAsync("AP01");
        await _f.WithDbAsync(db => Seed.ManagerWithGrantsAsync(db, manager, s.Site));
        var other = await NewFloorAsync("AP02");
        var anna = await _f.WithDbAsync(db => Seed.PersonAsync(db, "ap01.anna"));
        var asset = await _f.WithDbAsync(db => Seed.AssetAsync(db, "AP01-DT-1", s.Site, BuiltIn.Desktop));
        await AssignAsync(async a => { await a.AssignPersonAsync(s.Spots[0].Id, anna.Id); await a.AssignAssetAsync(s.Spots[0].Id, asset.Id); });
        await WorkplaceSeed.FacilityAsync(_f, TestUser.Facility);

        foreach (var user in new[] { TestUser.It, manager, TestUser.Facility })
        {
            var res = await _f.CreateClientAs(user).GetAsync($"/Workplace/Floor/{s.Floor.Id}?handler=Spot&workspotId={s.Spots[0].Id}");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.Equal("ap01.anna", doc.RootElement.GetProperty("person").GetProperty("username").GetString());
            Assert.Equal("AP01-DT-1", doc.RootElement.GetProperty("desktop").GetProperty("tag").GetString());
            Assert.Equal("Bay", doc.RootElement.GetProperty("place").GetString());
            Assert.False(doc.RootElement.TryGetProperty("label", out _));
        }
        // a seat of another floor is not served under this floor's url, nor to a manager of another site
        Assert.Equal(HttpStatusCode.NotFound, (await _f.CreateClientAs(TestUser.It).GetAsync($"/Workplace/Floor/{s.Floor.Id}?handler=Spot&workspotId={other.Spots[0].Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.CreateClientAs(manager).GetAsync($"/Workplace/Floor/{other.Floor.Id}?handler=Spot&workspotId={other.Spots[0].Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _f.CreateClientAs(TestUser.Nobody).GetAsync($"/Workplace/Floor/{s.Floor.Id}?handler=Spot&workspotId={s.Spots[0].Id}")).StatusCode);
    }

    [Fact]
    public async Task The_place_endpoint_is_for_editors_and_lists_candidates()
    {
        var s = await NewFloorAsync("AP03");
        await _f.WithDbAsync(db => Seed.PersonAsync(db, "ap03.zelda"));
        await WorkplaceSeed.FacilityAsync(_f, TestUser.Facility);

        foreach (var user in new[] { TestUser.It, TestUser.Facility })
        {
            var res = await _f.CreateClientAs(user).GetAsync($"/Workplace/Floor/{s.Floor.Id}?handler=Place&q=ap03.zel");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var row = doc.RootElement.EnumerateArray().Single();
            Assert.Equal("person", row.GetProperty("kind").GetString());
            Assert.Equal("ap03.zelda", row.GetProperty("subtitle").GetString()!.Split(" · ")[0]);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await _f.CreateClientAs(TestUser.Manager).GetAsync($"/Workplace/Floor/{s.Floor.Id}?handler=Place&q=zelda")).StatusCode);
    }

    [Fact]
    public async Task Placing_and_releasing_work_through_the_floor_page_forms()
    {
        var s = await NewFloorAsync("AP04");
        var (anna, bob) = await _f.WithDbAsync(async db => (await Seed.PersonAsync(db, "ap04.anna"), await Seed.PersonAsync(db, "ap04.bob")));
        var desktop = await _f.WithDbAsync(db => Seed.AssetAsync(db, "AP04-DT-1", s.Site, BuiltIn.Desktop));
        var screen = await _f.WithDbAsync(db => Seed.AssetAsync(db, "AP04-SC-1", s.Site, BuiltIn.Screen));
        var client = _f.CreateClientAs(TestUser.It);
        var url = $"/Workplace/Floor/{s.Floor.Id}";

        var place = await FormHelper.PostFormAsync(client, url, url + "?handler=Assign",
            ("workspotId", s.Spots[0].Id.ToString()), ("kind", "person"), ("targetId", anna.Id.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, place.StatusCode);
        Assert.Contains($"focus=ws%3A{s.Spots[0].Id}", place.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await FormHelper.PostFormAsync(client, url, url + "?handler=Assign",
            ("workspotId", s.Spots[0].Id.ToString()), ("kind", "desktop"), ("targetId", desktop.Id.ToString()))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await FormHelper.PostFormAsync(client, url, url + "?handler=Assign",
            ("workspotId", s.Spots[0].Id.ToString()), ("kind", "screen"), ("targetId", screen.Id.ToString()))).StatusCode);

        // the map now shows the person and the desktop tag, and the seat is taken
        var html = await client.GetStringAsync(url);
        Assert.Contains("data-person=\"ap04.anna\"", html);
        Assert.Contains(">AP04-DT-1</text>", html);
        Assert.Contains("fp-occ", html);
        Assert.Contains("desks taken", html);

        // a taken desk is refused with a message and a 409; an unknown kind with a 400
        var taken = await FormHelper.PostFormAsync(client, url, url + "?handler=Assign",
            ("workspotId", s.Spots[0].Id.ToString()), ("kind", "person"), ("targetId", bob.Id.ToString()));
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Contains("taken by", await taken.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await FormHelper.PostFormAsync(client, url, url + "?handler=Assign",
            ("workspotId", s.Spots[0].Id.ToString()), ("kind", "pool"), ("targetId", "1"))).StatusCode);

        var row = await _f.WithDbAsync(db => db.WorkspotAssignments.AsNoTracking().SingleAsync(a => a.PersonId == anna.Id && a.ReleasedAt == null));
        var release = await FormHelper.PostFormAsync(client, url, url + "?handler=Release", ("assignmentId", row.Id.ToString()), ("workspotId", s.Spots[0].Id.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, release.StatusCode);
        Assert.NotNull((await _f.WithDbAsync(db => db.WorkspotAssignments.AsNoTracking().SingleAsync(a => a.Id == row.Id))).ReleasedAt);
    }

    [Fact]
    public async Task A_drop_posted_with_fetch_gets_json_and_the_map_and_status_can_be_fetched_on_their_own()
    {
        var s = await NewFloorAsync("AP08");
        var (anna, bob) = await _f.WithDbAsync(async db => (await Seed.PersonAsync(db, "ap08.anna"), await Seed.PersonAsync(db, "ap08.bob")));
        var client = _f.CreateClientAs(TestUser.It);
        var url = $"/Workplace/Floor/{s.Floor.Id}";
        var token = await FormHelper.GetTokenAsync(client, url);

        async Task<HttpResponseMessage> DropAsync(int personId)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url + "?handler=Assign")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    [FormHelper.TokenField] = token, ["workspotId"] = s.Spots[1].Id.ToString(), ["kind"] = "person", ["targetId"] = personId.ToString(),
                }),
            };
            req.Headers.Accept.ParseAdd("application/json");
            return await client.SendAsync(req);
        }

        var ok = await DropAsync(anna.Id);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using (var doc = JsonDocument.Parse(await ok.Content.ReadAsStringAsync()))
            Assert.Contains("ap08.anna placed at", doc.RootElement.GetProperty("message").GetString());
        var conflict = await DropAsync(bob.Id);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using (var doc = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync()))
            Assert.Contains("taken by", doc.RootElement.GetProperty("message").GetString());

        var map = await client.GetStringAsync(url + "?handler=Map");
        Assert.StartsWith("<svg", map.Trim());
        Assert.Contains("data-person=\"ap08.anna\"", map);
        Assert.DoesNotContain("<html", map);
        var status = await client.GetStringAsync(url + "?handler=Status");
        Assert.Contains("At a glance", status);
        Assert.Contains("ap08.anna has no desktop and no screen.", System.Net.WebUtility.HtmlDecode(status));
        // flags and free desks say where (the room), never the drawing's seat id
        Assert.Contains("wp-flag-place\">Bay<", status);
        Assert.Matches($"data-show-seats=\"{s.Spots[0].Id},{s.Spots[2].Id},{s.Spots[3].Id}\"[^>]*>Bay <span class=\"wp-chip-n\">3<", status);
        Assert.DoesNotContain(">B1-T01-S0", status);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.CreateClientAs(TestUser.It).GetAsync($"/Workplace/Floor/987654?handler=Map")).StatusCode);
    }

    [Fact]
    public async Task The_palette_endpoint_lists_what_can_be_dragged_for_editors_only()
    {
        var s = await NewFloorAsync("AP09");
        await _f.WithDbAsync(db => Seed.PersonAsync(db, "ap09.zelda", s.Site));
        await _f.WithDbAsync(db => Seed.AssetAsync(db, "AP09-SC-1", s.Site, BuiltIn.Screen));
        await WorkplaceSeed.FacilityAsync(_f, TestUser.Facility);
        var url = $"/Workplace/Floor/{s.Floor.Id}";

        foreach (var user in new[] { TestUser.It, TestUser.Facility })
        {
            var client = _f.CreateClientAs(user);
            using var people = JsonDocument.Parse(await client.GetStringAsync(url + "?handler=Palette&kind=person"));
            Assert.Contains(people.RootElement.EnumerateArray(), r => r.GetProperty("title").GetString() == "ap09.zelda");
            using var screens = JsonDocument.Parse(await client.GetStringAsync(url + "?handler=Palette&kind=screen"));
            Assert.Equal("AP09-SC-1", screens.RootElement.EnumerateArray().Single().GetProperty("title").GetString());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await _f.CreateClientAs(TestUser.Manager).GetAsync(url + "?handler=Palette&kind=person")).StatusCode);
    }

    [Fact]
    public async Task The_palette_gives_the_job_title_as_the_hover_line_and_the_site_separately()
    {
        var s = await NewFloorAsync("AP31");
        await _f.WithDbAsync(async db =>
        {
            var p = await Seed.PersonAsync(db, "ap31.yara", s.Site);
            p.JobTitle = "Accountant";
            await db.SaveChangesAsync();
        });
        var client = _f.CreateClientAs(TestUser.It);
        using var people = JsonDocument.Parse(await client.GetStringAsync($"/Workplace/Floor/{s.Floor.Id}?handler=Palette&kind=person"));
        var row = people.RootElement.EnumerateArray().Single(r => r.GetProperty("title").GetString() == "ap31.yara");
        Assert.Equal("Accountant", row.GetProperty("subtitle").GetString());
        Assert.Equal(s.Site.Key, row.GetProperty("siteKey").GetString());
    }

    [Fact]
    public async Task Editors_make_seats_desks_or_not_through_the_page()
    {
        var s = await NewFloorAsync("AP10");
        var client = _f.CreateClientAs(TestUser.It);
        var url = $"/Workplace/Floor/{s.Floor.Id}";
        var res = await FormHelper.PostFormAsync(client, url, url + "?handler=Desk", ("ids", $"{s.Spots[0].Id},{s.Spots[1].Id}"), ("desk", "false"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var spots = await _f.WithDbAsync(db => db.Workspots.AsNoTracking().Where(w => w.FloorId == s.Floor.Id).OrderBy(w => w.ElementId).ToListAsync());
        Assert.Equal([false, false, true, true], spots.Select(w => w.IsDesk).ToArray());
        var html = await client.GetStringAsync(url);
        Assert.Contains("fp-nodesk", html);
        Assert.Contains("0<span class=\"wp-stat-of\">/2</span>", html); // 0 of 2 desks taken
    }

    [Fact]
    public async Task Managers_see_no_forms_to_place_things_and_cannot_post()
    {
        var manager = new TestUser(@"CORP\mgr.ap05", "S-1-5-21-1000-1000-1000-3905", AppRoles.Manager);
        var s = await NewFloorAsync("AP05");
        await _f.WithDbAsync(db => Seed.ManagerWithGrantsAsync(db, manager, s.Site));
        var html = await _f.CreateClientAs(manager).GetStringAsync($"/Workplace/Floor/{s.Floor.Id}");
        Assert.DoesNotContain("wp-assign-form", html);
        Assert.DoesNotContain("wp-release-form", html);
        Assert.DoesNotContain("data-palette", html);
        Assert.Contains("data-spot-url", html); // they can look
        Assert.Contains("At a glance", html);
        var editor = await _f.CreateClientAs(TestUser.It).GetStringAsync($"/Workplace/Floor/{s.Floor.Id}");
        Assert.Contains("wp-assign-form", editor);
        Assert.Contains("data-palette", editor);
    }

    [Fact]
    public async Task The_asset_page_links_to_where_a_desktop_is_placed()
    {
        var s = await NewFloorAsync("AP06");
        var asset = await _f.WithDbAsync(db => Seed.AssetAsync(db, "AP06-DT-1", s.Site, BuiltIn.Desktop));
        var lonely = await _f.WithDbAsync(db => Seed.AssetAsync(db, "AP06-DT-2", s.Site, BuiltIn.Desktop));
        await AssignAsync(a => a.AssignAssetAsync(s.Spots[1].Id, asset.Id));
        var client = _f.CreateClientAs(TestUser.It);

        var assetHtml = await client.GetStringAsync($"/Assets/Details/{asset.Id}");
        Assert.Contains("On the floor plan", assetHtml);
        Assert.Matches($"""href="/Workplace/Floor/{s.Floor.Id}\?focus=ws%3A{s.Spots[1].Id}"[^>]*>Floor · Bay<""", assetHtml);
        Assert.Contains("Not placed", await client.GetStringAsync($"/Assets/Details/{lonely.Id}"));
    }

    [Fact]
    public async Task The_print_page_shows_the_whole_floor_or_an_area_with_its_desks()
    {
        var s = await NewFloorAsync("AP11", seats: 4);
        var anna = await _f.WithDbAsync(db => Seed.PersonAsync(db, "ap11.anna"));
        await AssignAsync(a => a.AssignPersonAsync(s.Spots[0].Id, anna.Id));
        var client = _f.CreateClientAs(TestUser.It);

        var whole = await client.GetStringAsync($"/Workplace/Print/{s.Floor.Id}");
        Assert.Contains("Whole floor", whole);
        Assert.Contains("data-print-map", whole);
        Assert.Contains("ap11.anna", whole);
        // the list groups by zone and room; the three free desks are one line
        Assert.Contains("<h3>No zone</h3>", whole);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(whole, "<tr class=\"print-room-h\"><th scope=\"rowgroup\" colspan=\"4\">Bay</th></tr>"));
        Assert.Contains("<tr class=\"print-free\"><td colspan=\"4\">Bay: 3 free</td></tr>", whole);
        Assert.DoesNotContain(">B1-T01-S0", whole); // the drawing's seat ids are never shown as text
        Assert.DoesNotContain("class=\"sidebar\"", whole); // the print layout has no menu
        Assert.DoesNotMatch("""\sstyle\s*=""", whole);

        // seats sit at x = 70, 90, 110, 130 (y 84): an area around the first two lists those two only
        var part = await client.GetStringAsync($"/Workplace/Print/{s.Floor.Id}?area=60,70,40,30&what=Window+side");
        Assert.Contains("Window side", part);
        Assert.Contains("viewBox=\"60 70 40 30\"", part);
        Assert.Contains("ap11.anna", part);
        Assert.Contains("Bay: 1 free", part);
        // a bad area prints the whole floor
        Assert.Contains("Whole floor", await client.GetStringAsync($"/Workplace/Print/{s.Floor.Id}?area=nonsense"));

        var manager = new TestUser(@"CORP\mgr.ap11", "S-1-5-21-1000-1000-1000-3911", AppRoles.Manager);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.CreateClientAs(manager).GetAsync($"/Workplace/Print/{s.Floor.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _f.CreateClientAs(TestUser.Nobody).GetAsync($"/Workplace/Print/{s.Floor.Id}")).StatusCode);
    }

    [Fact]
    public async Task The_print_page_is_A4_by_default_and_switches_to_A3()
    {
        var s = await NewFloorAsync("AP12");
        var client = _f.CreateClientAs(TestUser.It);

        var a4 = await client.GetStringAsync($"/Workplace/Print/{s.Floor.Id}?area=60,70,40,30&what=Window+side&auto=true");
        Assert.Contains("class=\"print-sheet\" data-print-sheet data-paper=\"a4\"", a4);
        Assert.Contains("<strong>A4 landscape</strong>", a4);
        Assert.DoesNotContain("Safari", System.Text.RegularExpressions.Regex.Match(a4, "data-paper-hint>.*?</span>").Value);
        // the switch keeps the printed part, does not print by itself, and is also offered by the hint (shown by site.js
        // when names only fit as numbers on A4)
        var toA3 = $"data-paper-switch=\"a3\" href=\"/Workplace/Print/{s.Floor.Id}?area=60,70,40,30&amp;what=Window%20side&amp;paper=a3\"";
        Assert.Contains(toA3, a4);
        Assert.Contains("aria-current=\"true\" data-paper-switch=\"a4\"", a4);
        Assert.Matches("data-a3-hint hidden>[\\s\\S]*?This floor is easier to read on A3[\\s\\S]*?Switch to A3", a4);
        Assert.Contains("data-tag-prefix=\"AP12-\"", a4);

        var a3 = await client.GetStringAsync($"/Workplace/Print/{s.Floor.Id}?paper=a3");
        Assert.Contains("class=\"print-sheet print-sheet--a3\" data-print-sheet data-paper=\"a3\"", a3);
        Assert.Contains("<strong>A3 landscape</strong>", a3);
        Assert.Contains("Safari does not take the paper size from the page: choose A3 in its print dialog yourself.", a3);
        Assert.Contains($"data-paper-switch=\"a4\" href=\"/Workplace/Print/{s.Floor.Id}\"", a3);
        Assert.Contains("aria-current=\"true\" data-paper-switch=\"a3\"", a3);
        Assert.Contains(" · A3 · printed ", a3);
        // the whole floor prints the plan, not the paper around it: the drawing's 400 × 200 viewBox is cropped
        Assert.DoesNotContain("viewBox=\"0 0 400 200\"", a3);
        Assert.Contains("viewBox=\"3 3 294 194\"", a3);
    }

    [Fact]
    public async Task The_print_list_is_grouped_by_zone_and_room_with_free_desks_on_one_line()
    {
        var s = await NewFloorAsync("AP13", seats: 6);
        await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<ZoneService>().CreateAsync("Alpha", [s.Spots[0].Id, s.Spots[1].Id, s.Spots[2].Id]));
        var anna = await _f.WithDbAsync(db => Seed.PersonAsync(db, "ap13.anna"));
        var bo = await _f.WithDbAsync(db => Seed.PersonAsync(db, "ap13.bo"));
        var desktop = await _f.WithDbAsync(db => Seed.AssetAsync(db, "AP13-DT-1", s.Site, BuiltIn.Desktop));
        await AssignAsync(async a =>
        {
            await a.AssignPersonAsync(s.Spots[0].Id, anna.Id);
            await a.AssignPersonAsync(s.Spots[4].Id, bo.Id);
            await a.AssignAssetAsync(s.Spots[5].Id, desktop.Id); // a free desk with a desktop is listed, not counted
        });

        var html = await _f.CreateClientAs(TestUser.It).GetStringAsync($"/Workplace/Print/{s.Floor.Id}");
        var alpha = html.IndexOf("<h3>Zone Alpha</h3>", StringComparison.Ordinal);
        var none = html.IndexOf("<h3>No zone</h3>", StringComparison.Ordinal);
        Assert.True(alpha > 0 && none > alpha, "zones in order, desks in no zone last");
        var alphaPart = html[alpha..none];
        var nonePart = html[none..];
        Assert.Contains("ap13.anna", alphaPart);
        Assert.Contains("<td colspan=\"4\">Bay: 2 free</td>", alphaPart);
        Assert.Contains("ap13.bo", nonePart);
        Assert.Contains("AP13-DT-1", nonePart);
        Assert.Contains("<td colspan=\"4\">Bay: 1 free</td>", nonePart);
        // plain free desks are never a row of their own; only the free desk with a desktop is
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<td>Free</td>"));
        Assert.Contains("2 taken, 4 free", html);
    }

    [Fact]
    public async Task The_map_with_names_and_tooltips_has_no_inline_style_or_script()
    {
        var s = await NewFloorAsync("AP07");
        var anna = await _f.WithDbAsync(db => Seed.PersonAsync(db, "ap07.<b>anna</b>"));
        await AssignAsync(a => a.AssignPersonAsync(s.Spots[0].Id, anna.Id));
        var html = await _f.CreateClientAs(TestUser.It).GetStringAsync($"/Workplace/Floor/{s.Floor.Id}");
        Assert.DoesNotMatch("""\sstyle\s*=""", html);
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<b>anna</b>", html); // names are encoded in the tooltips and attributes
    }
}
