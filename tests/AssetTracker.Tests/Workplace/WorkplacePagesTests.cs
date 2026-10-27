using System.Net;
using System.Text;
using System.Text.Json;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Workplace;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Workplace;

public class WorkplacePagesTests : IClassFixture<AppFactory>
{
    private const string Hostile = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 50" onload="alert(1)">
          <script>alert(2)</script><style>.x{fill:red}</style>
          <rect id="a" class="x" x="1" y="1" width="9" height="9" style="fill:blue" onclick="alert(3)"/>
        </svg>
        """;

    private readonly AppFactory _f;
    public WorkplacePagesTests(AppFactory f) => _f = f;

    private async Task<(Site Site, Floor Floor, List<Workspot> Spots)> SeedFloorAsync(string siteKey, string name = "Page floor", int seats = 4)
    {
        var site = await _f.WithDbAsync(db => Seed.SiteAsync(db, siteKey));
        var floor = (await WorkplaceSeed.ImportAsync(_f, site, name, WorkplaceSeed.Annotated(seats, room: "P1", roomName: "Page room"))).Floor;
        var spots = await _f.WithDbAsync(db => db.Workspots.AsNoTracking().Where(w => w.FloorId == floor.Id).OrderBy(w => w.ElementId).ToListAsync());
        return (site, floor, spots);
    }

    private static MultipartFormDataContent Upload(string token, string svg, params (string Name, string Value)[] fields)
    {
        var content = new MultipartFormDataContent { { new StringContent(token), FormHelper.TokenField } };
        foreach (var (n, v) in fields) content.Add(new StringContent(v), n);
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(svg)), "Drawing", "floor.svg");
        return content;
    }

    [Theory]
    [InlineData("it")]
    [InlineData("manager")]
    [InlineData("facility")]
    public async Task Readers_open_the_workplace_and_others_do_not(string who)
    {
        await WorkplaceSeed.FacilityAsync(_f, TestUser.Facility);
        var user = who switch { "manager" => TestUser.Manager, "facility" => TestUser.Facility, _ => TestUser.It };
        Assert.Equal(HttpStatusCode.OK, (await _f.CreateClientAs(user).GetAsync("/Workplace")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _f.CreateClientAs(TestUser.Admin).GetAsync("/Workplace")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _f.CreateClientAs(TestUser.Nobody).GetAsync("/Workplace")).StatusCode);
    }

    [Fact]
    public async Task The_floor_page_shows_the_map_with_workspots_and_a_scoped_stylesheet()
    {
        var (_, floor, spots) = await SeedFloorAsync("WP01");
        var client = _f.CreateClientAs(TestUser.It);
        var html = await client.GetStringAsync($"/Workplace/Floor/{floor.Id}");

        Assert.Contains("data-floor-map", html);
        Assert.Contains($"data-ws=\"{spots[0].Id}\"", html);
        Assert.Contains("Page room", html);
        Assert.Matches($"""href="/Workplace/Floor/{floor.Id}\?[^"]*handler=Css""", html);

        var css = await client.GetAsync($"/Workplace/Floor/{floor.Id}?handler=Css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal("text/css", css.Content.Headers.ContentType?.MediaType);
        Assert.Contains(".floorplan .fp-seat{fill:#2b6cb0}", await css.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_hostile_drawing_cannot_put_script_or_inline_style_on_the_page()
    {
        var site = await _f.WithDbAsync(db => Seed.SiteAsync(db, "WP02"));
        var floor = (await WorkplaceSeed.ImportAsync(_f, site, "Hostile", Hostile)).Floor;
        var html = await _f.CreateClientAs(TestUser.It).GetStringAsync($"/Workplace/Floor/{floor.Id}");

        Assert.DoesNotContain("alert(", html);
        Assert.DoesNotContain("onload", html);
        Assert.DoesNotContain("onclick", html);
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch("""\sstyle\s*=""", html);
        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public async Task Managers_see_only_their_sites_floors_and_cannot_edit()
    {
        var manager = new TestUser(@"CORP\mgr.wp03", "S-1-5-21-1000-1000-1000-3703", TestGroups.Manager);
        var (mine, theirs) = await _f.WithDbAsync(async db =>
        {
            var a = await Seed.SiteAsync(db, "WP03");
            var b = await Seed.SiteAsync(db, "WP04");
            await Seed.ManagerWithGrantsAsync(db, manager, a);
            return (a, b);
        });
        var own = (await WorkplaceSeed.ImportAsync(_f, mine, "Own floor", WorkplaceSeed.Annotated())).Floor;
        var foreign = (await WorkplaceSeed.ImportAsync(_f, theirs, "Foreign floor", WorkplaceSeed.Annotated())).Floor;
        var client = _f.CreateClientAs(manager);

        var index = await client.GetStringAsync("/Workplace");
        Assert.Contains("Own floor", index);
        Assert.DoesNotContain("Foreign floor", index);
        Assert.DoesNotContain("Import floor plan", index);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/Workplace/Floor/{own.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Workplace/Floor/{foreign.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Workplace/Floor/{foreign.Id}?handler=Css")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/Workplace/Import")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/Workplace/Replace/{own.Id}")).StatusCode);

        var ownHtml = await client.GetStringAsync($"/Workplace/Floor/{own.Id}");
        Assert.DoesNotContain("Set zone", ownHtml);
        Assert.DoesNotContain("Replace drawing", ownHtml);

        // a manager has no form on the page (so no antiforgery token); a forged post is refused and changes nothing
        var forged = await client.PostAsync($"/Workplace/Floor/{own.Id}?handler=SetZone", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["zone"] = "new", ["name"] = "Sneaky", ["ids"] = "1" }));
        Assert.True(forged.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Forbidden);
        Assert.False(await _f.WithDbAsync(db => db.FloorZones.AnyAsync(t => t.Name == "Sneaky")));
    }

    [Fact]
    public async Task Find_returns_json_hits_for_readers_only()
    {
        var (_, floor, _) = await SeedFloorAsync("WP05");
        var res = await _f.CreateClientAs(TestUser.It).GetAsync("/Workplace?handler=Find&q=page%20room");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var hit = doc.RootElement.EnumerateArray().First(r => r.GetProperty("floorId").GetInt32() == floor.Id);
        Assert.Equal("room", hit.GetProperty("kind").GetString());
        Assert.Equal("room:P1", hit.GetProperty("focus").GetString());
        Assert.Equal(4, hit.GetProperty("seats").GetInt32());

        Assert.Equal(HttpStatusCode.Forbidden, (await _f.CreateClientAs(TestUser.Nobody).GetAsync("/Workplace?handler=Find&q=page")).StatusCode);
        Assert.Equal("[]", await _f.CreateClientAs(TestUser.It).GetStringAsync("/Workplace?handler=Find&q=p"));
    }

    [Fact]
    public async Task A_focus_link_puts_the_element_ids_on_the_map()
    {
        var (_, floor, spots) = await SeedFloorAsync("WP06");
        var html = await _f.CreateClientAs(TestUser.It).GetStringAsync($"/Workplace/Floor/{floor.Id}?focus=ws:{spots[1].Id}");
        Assert.Contains($"data-focus=\"{spots[1].ElementId}\"", html);
    }

    [Fact]
    public async Task Zones_are_found_shown_in_the_panel_and_old_team_focus_links_still_work()
    {
        var (_, floor, spots) = await SeedFloorAsync("WPZ1");
        var zone = await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<ZoneService>()
            .CreateAsync("Zonefinder", [spots[0].Id, spots[1].Id]));
        var client = _f.CreateClientAs(TestUser.It);

        // the search result is a zone with a zone: focus
        using (var doc = JsonDocument.Parse(await client.GetStringAsync("/Workplace?handler=Find&q=zonefinder")))
        {
            var hit = doc.RootElement.EnumerateArray().Single(r => r.GetProperty("floorId").GetInt32() == floor.Id);
            Assert.Equal("zone", hit.GetProperty("kind").GetString());
            Assert.Equal($"zone:{zone.Id}", hit.GetProperty("focus").GetString());
        }

        // zone: and the old team: focus select the same seats
        var expected = $"data-focus=\"{spots[0].ElementId},{spots[1].ElementId}\"";
        Assert.Contains(expected, await client.GetStringAsync($"/Workplace/Floor/{floor.Id}?focus=zone:{zone.Id}"));
        Assert.Contains(expected, await client.GetStringAsync($"/Workplace/Floor/{floor.Id}?focus=team:{zone.Id}"));

        // the side panel and the map use the zone classes, never the old team ones
        var html = await client.GetStringAsync($"/Workplace/Floor/{floor.Id}");
        Assert.Contains("class=\"wp-zones\"", html);
        Assert.Contains("class=\"wp-zone-show\"", html);
        Assert.Contains("class=\"wp-zone-name\"", html);
        Assert.Contains("class=\"wp-swatch zc-0\"", html);
        Assert.Contains("class=\"zone-layer\"", html);
        Assert.Contains("<h2 id=\"wp-zones-h\">Zones</h2>", html);
        Assert.DoesNotContain("team", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task It_imports_a_floor_through_the_form_and_bad_files_show_a_message()
    {
        var site = await _f.WithDbAsync(db => Seed.SiteAsync(db, "WP07"));
        var client = _f.CreateClientAs(TestUser.It);
        var token = await FormHelper.GetTokenAsync(client, "/Workplace/Import");

        var ok = await client.PostAsync("/Workplace/Import", Upload(token, WorkplaceSeed.Annotated(3), ("SiteId", site.Id.ToString()), ("Name", "Uploaded")));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        var floor = await _f.WithDbAsync(db => db.Floors.AsNoTracking().SingleAsync(f => f.SiteId == site.Id && f.Name == "Uploaded"));
        Assert.Equal($"/Workplace/Floor/{floor.Id}", ok.Headers.Location?.OriginalString);
        Assert.Equal(3, await _f.WithDbAsync(db => db.Workspots.CountAsync(w => w.FloorId == floor.Id)));

        var bad = await client.PostAsync("/Workplace/Import", Upload(token, "this is not svg", ("SiteId", site.Id.ToString()), ("Name", "Broken")));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("not valid SVG", await bad.Content.ReadAsStringAsync());

        var dup = await client.PostAsync("/Workplace/Import", Upload(token, WorkplaceSeed.Annotated(3), ("SiteId", site.Id.ToString()), ("Name", "uploaded")));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        var noFile = await client.PostAsync("/Workplace/Import", new MultipartFormDataContent
            { { new StringContent(token), FormHelper.TokenField }, { new StringContent(site.Id.ToString()), "SiteId" }, { new StringContent("Nothing"), "Name" } });
        Assert.Equal(HttpStatusCode.BadRequest, noFile.StatusCode);
    }

    [Fact]
    public async Task The_floor_page_gives_a_table_a_zone_splits_it_between_zones_and_archives()
    {
        var (_, floor, spots) = await SeedFloorAsync("WP08", seats: 4);
        var client = _f.CreateClientAs(TestUser.It);
        var url = $"/Workplace/Floor/{floor.Id}";
        var table = string.Join(',', spots.Select(s => s.Id));

        // the whole table becomes a new zone; the redirect selects those seats again
        var create = await FormHelper.PostFormAsync(client, url, url + "?handler=SetZone", ("zone", "new"), ("name", "Page zone"), ("ids", table));
        Assert.Equal(HttpStatusCode.Redirect, create.StatusCode);
        Assert.Contains("focus=ws%3A" + table, create.Headers.Location?.OriginalString);
        var zone = await _f.WithDbAsync(db => db.FloorZones.AsNoTracking().SingleAsync(t => t.FloorId == floor.Id));
        var html = await client.GetStringAsync(url);
        Assert.Contains("Page zone", html);
        Assert.Contains("Set zone", html);
        Assert.Contains($"data-focus=\"{string.Join(',', spots.Select(s => s.ElementId))}\"", await client.GetStringAsync(url + "?focus=ws:" + table));

        // split the table: two of its seats go to a second zone, one gets no zone
        Assert.Equal(HttpStatusCode.Redirect, (await FormHelper.PostFormAsync(client, url, url + "?handler=SetZone",
            ("zone", "new"), ("name", "Second zone"), ("ids", $"{spots[2].Id},{spots[3].Id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await FormHelper.PostFormAsync(client, url, url + "?handler=SetZone",
            ("zone", "none"), ("ids", spots[0].Id.ToString()))).StatusCode);
        var second = await _f.WithDbAsync(db => db.FloorZones.AsNoTracking().SingleAsync(t => t.Name == "Second zone"));
        // and one seat back to the first zone
        Assert.Equal(HttpStatusCode.Redirect, (await FormHelper.PostFormAsync(client, url, url + "?handler=SetZone",
            ("zone", zone.Id.ToString()), ("ids", spots[3].Id.ToString()))).StatusCode);

        Assert.Equal(HttpStatusCode.Redirect, (await FormHelper.PostFormAsync(client, url, url + "?handler=ZoneRename",
            ("zoneId", zone.Id.ToString()), ("name", "Renamed zone"))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await FormHelper.PostFormAsync(client, url, url + "?handler=ZoneColor",
            ("zoneId", zone.Id.ToString()), ("color", "4"))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await FormHelper.PostFormAsync(client, url, url + "?handler=Retire",
            ("ids", spots[0].Id.ToString()))).StatusCode);

        var after = await _f.WithDbAsync(db => db.Workspots.AsNoTracking().Where(w => w.FloorId == floor.Id).OrderBy(w => w.ElementId).ToListAsync());
        Assert.Equal([null, zone.Id, second.Id, zone.Id], after.Select(w => w.ZoneId).ToArray());
        Assert.False(after[0].IsActive);
        var stored = await _f.WithDbAsync(db => db.FloorZones.AsNoTracking().SingleAsync(t => t.Id == zone.Id));
        Assert.Equal(("Renamed zone", 4), (stored.Name, stored.ColorIndex));

        // the split table is drawn with an outline for each zone
        var map = await client.GetStringAsync(url + "?handler=Map");
        Assert.Contains($"class=\"zone-outline zc-4\"", map);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(map, "class=\"zone-outline ").Count);

        // a rule violation re-renders the page with the message and a 4xx status; so does no choice
        var dup = await FormHelper.PostFormAsync(client, url, url + "?handler=SetZone", ("zone", "new"), ("name", "renamed zone"), ("ids", spots[1].Id.ToString()));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Contains("already has a zone", await dup.Content.ReadAsStringAsync());
        var none = await FormHelper.PostFormAsync(client, url, url + "?handler=SetZone", ("ids", spots[1].Id.ToString()));
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);

        Assert.Equal(HttpStatusCode.Redirect, (await FormHelper.PostFormAsync(client, url, url + "?handler=ZoneDelete", ("zoneId", zone.Id.ToString()))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await FormHelper.PostFormAsync(client, url, url + "?handler=Archive")).StatusCode);
        Assert.False((await _f.WithDbAsync(db => db.Floors.AsNoTracking().SingleAsync(f => f.Id == floor.Id))).IsActive);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode); // editors still see it
    }

    [Fact]
    public async Task Marking_shapes_through_the_page_works_for_a_plain_drawing()
    {
        var site = await _f.WithDbAsync(db => Seed.SiteAsync(db, "WP09"));
        var floor = (await WorkplaceSeed.ImportAsync(_f, site, "Plain", WorkplaceSeed.Plain())).Floor;
        var client = _f.CreateClientAs(TestUser.It);
        var url = $"/Workplace/Floor/{floor.Id}";
        Assert.Contains("does not declare its seats", await client.GetStringAsync(url));

        var res = await FormHelper.PostFormAsync(client, url, url + "?handler=Mark", ("ids", "fp-e2,fp-e3"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal(2, await _f.WithDbAsync(db => db.Workspots.CountAsync(w => w.FloorId == floor.Id)));
        var html = await client.GetStringAsync(url);
        Assert.DoesNotContain("does not declare its seats", html);
        Assert.Contains("fp-ws-manual", html);
    }


    [Fact]
    public async Task Replacing_the_drawing_through_the_page_reports_what_changed()
    {
        var (_, floor, _) = await SeedFloorAsync("WP10", seats: 4);
        var client = _f.CreateClientAs(TestUser.It);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/Workplace/Replace/{floor.Id}")).StatusCode);

        var token = await FormHelper.GetTokenAsync(client, $"/Workplace/Replace/{floor.Id}");
        var res = await client.PostAsync($"/Workplace/Replace/{floor.Id}", Upload(token, WorkplaceSeed.Annotated(2, room: "P1", roomName: "Page room")));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal(2, await _f.WithDbAsync(db => db.Workspots.CountAsync(w => w.FloorId == floor.Id && !w.IsActive)));
        Assert.Equal(2, (await _f.WithDbAsync(db => db.Floors.AsNoTracking().SingleAsync(f => f.Id == floor.Id))).Version);
    }
}
