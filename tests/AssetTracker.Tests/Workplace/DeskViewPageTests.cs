using AssetTracker.Core.Domain;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Tests.Workplace;

/// <summary>The desk view and the list on the floor page (markup; the behaviour is in site.js).</summary>
public class DeskViewPageTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public DeskViewPageTests(AppFactory f) => _f = f;

    private sealed record Setup(Site Site, Floor Floor, List<Workspot> Spots);

    private async Task<Setup> NewFloorAsync(string key, int seats = 4)
    {
        var site = await _f.WithDbAsync(db => Seed.SiteAsync(db, key));
        var floor = (await WorkplaceSeed.ImportAsync(_f, site, "Floor", WorkplaceSeed.Annotated(seats, room: "D1", roomName: "Deck"))).Floor;
        var spots = await _f.WithDbAsync(db => db.Workspots.AsNoTracking().Where(w => w.FloorId == floor.Id).OrderBy(w => w.ElementId).ToListAsync());
        return new Setup(site, floor, spots);
    }

    [Fact]
    public async Task The_floor_page_has_the_desk_view_with_three_slots_and_the_list_collapsed_below_it()
    {
        var s = await NewFloorAsync("DV18");
        var html = await _f.CreateClientAs(TestUser.It).GetStringAsync($"/Workplace/Floor/{s.Floor.Id}");
        var desk = html.IndexOf("data-desk-view", StringComparison.Ordinal);
        var list = html.IndexOf("Place from a list", StringComparison.Ordinal);
        Assert.True(desk > 0 && list > desk, "the list comes after the desk view");
        foreach (var slot in new[] { "person", "desktop", "screen" })
            Assert.Contains($"data-slot=\"{slot}\"", html);
        Assert.Contains("data-search-url=\"/Workplace/Floor/" + s.Floor.Id + "?handler=DeskSearch\"", html);
        Assert.Matches("<details class=\"card wp-box wp-palette\" data-palette ", html); // collapsed: no "open"
        Assert.Contains("Next free desk", html);
        Assert.Contains("Previous desk", html);
        Assert.DoesNotMatch("""\sstyle\s*=""", html);
    }

    [Fact]
    public async Task Managers_get_the_desk_view_without_search_boxes_or_forms()
    {
        var manager = new TestUser(@"CORP\mgr.dv19", "S-1-5-21-1000-1000-1000-3959", TestGroups.Manager);
        var s = await NewFloorAsync("DV19");
        await _f.WithDbAsync(db => Seed.ManagerWithGrantsAsync(db, manager, s.Site));
        var html = await _f.CreateClientAs(manager).GetStringAsync($"/Workplace/Floor/{s.Floor.Id}");
        Assert.Contains("data-desk-view", html);
        Assert.Contains("data-spot-url", html);
        Assert.DoesNotContain("data-slot-input", html);
        Assert.DoesNotContain("wp-replace-form", html);
        Assert.DoesNotContain("Place from a list", html);
    }
}
