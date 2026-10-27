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
        var manager = new TestUser(@"CORP\mgr.ap01", "S-1-5-21-1000-1000-1000-3901", TestGroups.Manager);
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
            Assert.Equal("AP01-DT-1", doc.RootElement.GetProperty("assets")[0].GetProperty("tag").GetString());
            Assert.Equal(s.Spots[0].Label, doc.RootElement.GetProperty("label").GetString());
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
    public async Task Placing_moving_and_releasing_work_through_the_floor_page_forms()
    {
        var s = await NewFloorAsync("AP04");
        var (anna, bob) = await _f.WithDbAsync(async db => (await Seed.PersonAsync(db, "ap04.anna"), await Seed.PersonAsync(db, "ap04.bob")));
        var asset = await _f.WithDbAsync(db => Seed.AssetAsync(db, "AP04-DT-1", s.Site, BuiltIn.Desktop));
        var pool = await _f.WithDbAsync(db => Seed.PoolAsync(db, "HS-AP04", s.Site, 6));
        var client = _f.CreateClientAs(TestUser.It);
        var url = $"/Workplace/Floor/{s.Floor.Id}";

        var place = await FormHelper.PostFormAsync(client, url, url + "?handler=Assign",
            ("workspotId", s.Spots[0].Id.ToString()), ("kind", "person"), ("targetId", anna.Id.ToString()), ("quantity", "1"));
        Assert.Equal(HttpStatusCode.Redirect, place.StatusCode);
        Assert.Contains($"focus=ws%3A{s.Spots[0].Id}", place.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await FormHelper.PostFormAsync(client, url, url + "?handler=Assign",
            ("workspotId", s.Spots[0].Id.ToString()), ("kind", "asset"), ("targetId", asset.Id.ToString()))).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await FormHelper.PostFormAsync(client, url, url + "?handler=Assign",
            ("workspotId", s.Spots[0].Id.ToString()), ("kind", "pool"), ("targetId", pool.Id.ToString()), ("quantity", "4"))).StatusCode);

        // the page now reports it (summary and tooltips)
        var html = await client.GetStringAsync(url);
        Assert.Contains("Seated", html);
        Assert.Contains("ap04.anna", html);
        Assert.Contains("fp-occ", html);

        // a taken seat is refused with a message and a 409
        var taken = await FormHelper.PostFormAsync(client, url, url + "?handler=Assign",
            ("workspotId", s.Spots[0].Id.ToString()), ("kind", "person"), ("targetId", bob.Id.ToString()));
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Contains("taken by", await taken.Content.ReadAsStringAsync());
        var unknownKind = await FormHelper.PostFormAsync(client, url, url + "?handler=Assign",
            ("workspotId", s.Spots[0].Id.ToString()), ("kind", "chair"), ("targetId", "1"));
        Assert.Equal(HttpStatusCode.BadRequest, unknownKind.StatusCode);

        var row = await _f.WithDbAsync(db => db.WorkspotAssignments.AsNoTracking().SingleAsync(a => a.PersonId == anna.Id && a.ReleasedAt == null));
        var release = await FormHelper.PostFormAsync(client, url, url + "?handler=Release", ("assignmentId", row.Id.ToString()), ("workspotId", s.Spots[0].Id.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, release.StatusCode);
        Assert.NotNull((await _f.WithDbAsync(db => db.WorkspotAssignments.AsNoTracking().SingleAsync(a => a.Id == row.Id))).ReleasedAt);
    }

    [Fact]
    public async Task Managers_see_no_forms_to_place_things_and_cannot_post()
    {
        var manager = new TestUser(@"CORP\mgr.ap05", "S-1-5-21-1000-1000-1000-3905", TestGroups.Manager);
        var s = await NewFloorAsync("AP05");
        await _f.WithDbAsync(db => Seed.ManagerWithGrantsAsync(db, manager, s.Site));
        var html = await _f.CreateClientAs(manager).GetStringAsync($"/Workplace/Floor/{s.Floor.Id}");
        Assert.DoesNotContain("wp-assign-form", html);
        Assert.DoesNotContain("wp-release-form", html);
        Assert.DoesNotContain("Place a person, asset or pool here", html);
        Assert.Contains("data-spot-url", html); // they can look
        var editor = await _f.CreateClientAs(TestUser.It).GetStringAsync($"/Workplace/Floor/{s.Floor.Id}");
        Assert.Contains("wp-assign-form", editor);
    }

    [Fact]
    public async Task Asset_and_pool_pages_link_to_where_they_are_placed()
    {
        var s = await NewFloorAsync("AP06");
        var asset = await _f.WithDbAsync(db => Seed.AssetAsync(db, "AP06-DT-1", s.Site, BuiltIn.Desktop));
        var lonely = await _f.WithDbAsync(db => Seed.AssetAsync(db, "AP06-DT-2", s.Site, BuiltIn.Desktop));
        var pool = await _f.WithDbAsync(db => Seed.PoolAsync(db, "HS-AP06", s.Site, 5));
        await AssignAsync(async a => { await a.AssignAssetAsync(s.Spots[1].Id, asset.Id); await a.AssignPoolAsync(s.Spots[2].Id, pool.Id, 3); });
        var client = _f.CreateClientAs(TestUser.It);

        var assetHtml = await client.GetStringAsync($"/Assets/Details/{asset.Id}");
        Assert.Contains("On the floor plan", assetHtml);
        Assert.Matches($"""href="/Workplace/Floor/{s.Floor.Id}\?focus=ws%3A{s.Spots[1].Id}"[^>]*>Floor · {System.Text.RegularExpressions.Regex.Escape(s.Spots[1].Label)}""", assetHtml);
        Assert.Contains("Not placed", await client.GetStringAsync($"/Assets/Details/{lonely.Id}"));

        var poolHtml = await client.GetStringAsync($"/Pools/Details/{pool.Id}");
        Assert.Contains("On the floor plans", poolHtml);
        Assert.Contains(s.Spots[2].Label, poolHtml);
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
