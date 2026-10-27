using System.Text.RegularExpressions;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Workplace;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Workplace;

/// <summary>
/// The drawings are not final: a revised drawing (other element ids, renumbered seats, a different scale, regrouped
/// elements) must keep the floor's seats, with their zones and placements, and the map must follow it.
/// </summary>
public class DrawingRevisionTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public DrawingRevisionTests(AppFactory f) => _f = f;

    private sealed record Setup(Floor Floor, List<Workspot> Spots, Person Anna, FloorZone Zone);

    /// <summary>A floor of four seats; Anna sits at seat 2, seats 1-2 are zone "Window".</summary>
    private async Task<Setup> NewFloorAsync(string key, string? svg = null)
    {
        var site = await _f.WithDbAsync(db => Seed.SiteAsync(db, key));
        var floor = (await WorkplaceSeed.ImportAsync(_f, site, "Floor", svg ?? WorkplaceSeed.Annotated(4))).Floor;
        var spots = await SpotsAsync(floor.Id);
        var anna = await _f.WithDbAsync(db => Seed.PersonAsync(db, key.ToLowerInvariant() + ".anna"));
        var zone = await _f.RunAsAsync(TestUser.It, async sp =>
        {
            await sp.GetRequiredService<AssignmentService>().AssignPersonAsync(spots[1].Id, anna.Id);
            return await sp.GetRequiredService<ZoneService>().CreateAsync("Window", [spots[0].Id, spots[1].Id]);
        });
        return new Setup(floor, spots, anna, zone);
    }

    private Task<List<Workspot>> SpotsAsync(int floorId) =>
        _f.WithDbAsync(db => db.Workspots.AsNoTracking().Where(w => w.FloorId == floorId).OrderBy(w => w.Id).ToListAsync());

    private Task<ImportResult> ReplaceAsync(int floorId, string svg) =>
        _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<FloorService>().ReplaceDrawingAsync(floorId, svg));

    private async Task AssertKeptAsync(Setup s)
    {
        var after = await SpotsAsync(s.Floor.Id);
        Assert.Equal(s.Spots.Select(w => w.Id), after.Where(w => w.IsActive).Select(w => w.Id));
        Assert.Equal([s.Zone.Id, s.Zone.Id, null, null], after.Select(w => w.ZoneId).ToArray());
        Assert.True(await _f.WithDbAsync(db => db.WorkspotAssignments.AnyAsync(a => a.WorkspotId == s.Spots[1].Id && a.PersonId == s.Anna.Id && a.ReleasedAt == null)));
    }

    [Fact]
    public async Task A_re_made_drawing_with_other_element_ids_keeps_seats_zones_and_placements_by_seat_id()
    {
        var s = await NewFloorAsync("DR01");
        var r = await ReplaceAsync(s.Floor.Id, WorkplaceSeed.Annotated(4).Replace("id=\"f1-", "id=\"floor6-"));
        Assert.Equal((0, 0, 4), (r.Added, r.Deactivated, r.Moved));
        await AssertKeptAsync(s);
        Assert.All(await SpotsAsync(s.Floor.Id), w => Assert.StartsWith("floor6-", w.ElementId));
        // and the map draws Anna at her seat in the new drawing
        var svg = await _f.CreateClientAs(TestUser.It).GetStringAsync($"/Workplace/Floor/{s.Floor.Id}?handler=Map");
        Assert.Matches("id=\"floor6-seat-R1-T01-S02\"[^>]*data-person=\"dr01.anna\"", svg);
    }

    [Fact]
    public async Task Renumbered_seats_in_the_same_place_are_found_by_position()
    {
        var s = await NewFloorAsync("DR02");
        var renumbered = WorkplaceSeed.Annotated(4).Replace("R1-T01-S0", "R1-T07-X0");
        var r = await ReplaceAsync(s.Floor.Id, renumbered);
        Assert.Equal((0, 0, 4), (r.Added, r.Deactivated, r.Moved));
        await AssertKeptAsync(s);
        Assert.Equal("R1-T07-X02", (await SpotsAsync(s.Floor.Id))[1].Label);
    }

    [Fact]
    public async Task A_drawing_at_another_scale_inside_a_transformed_group_is_found_relative_to_its_view()
    {
        var s = await NewFloorAsync("DR03");
        var original = WorkplaceSeed.Annotated(4);
        // twice as large, everything in a scaled group, other ids and seat numbers: only the position is left
        var scaled = original.Replace("viewBox=\"0 0 400 200\"", "viewBox=\"0 0 800 400\"")
            .Replace("<rect class=\"bg\"", "<g transform=\"scale(2)\"><rect class=\"bg\"").Replace("<text", "</g><text")
            .Replace("id=\"f1-", "id=\"v2-").Replace("R1-T01-S0", "Z-")
            .Replace("data-id=\"R1\"", "data-id=\"Z\"").Replace("data-parent=\"R1\"", "data-parent=\"Z\"");
        var r = await ReplaceAsync(s.Floor.Id, scaled);
        Assert.Equal((0, 0, 4), (r.Added, r.Deactivated, r.Moved));
        await AssertKeptAsync(s);
        // the marker follows the transform: Anna's seat is at (90, 84) in the original, (180, 168) now
        var svg = await _f.CreateClientAs(TestUser.It).GetStringAsync($"/Workplace/Floor/{s.Floor.Id}?handler=Map");
        var icon = Regex.Match(svg, "<use href=\"#fp-i-person\"[^>]*x=\"([0-9.]+)\" y=\"([0-9.]+)\" width=\"([0-9.]+)\"");
        var (x, y, w) = (double.Parse(icon.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(icon.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(icon.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.InRange(x + w / 2, 179.9, 180.1);
        Assert.InRange(y + w / 2, 167.9, 168.1);
    }

    [Fact]
    public async Task A_removed_seat_with_someone_on_it_is_switched_off_and_flagged_and_comes_back_with_its_zone()
    {
        var s = await NewFloorAsync("DR04");
        // seat 2 (Anna's) is gone, the others stay
        var without = Regex.Replace(WorkplaceSeed.Annotated(4), "<circle[^>]*S02\"[^>]*/>", "");
        var r = await ReplaceAsync(s.Floor.Id, without);
        Assert.Equal((0, 1, 1), (r.Added, r.Deactivated, r.Orphaned));
        var status = await _f.CreateClientAs(TestUser.It).GetStringAsync($"/Workplace/Floor/{s.Floor.Id}?handler=Status");
        Assert.Contains("dr04.anna is at a seat that is switched off or no longer in the drawing.", System.Net.WebUtility.HtmlDecode(status));

        var back = await ReplaceAsync(s.Floor.Id, WorkplaceSeed.Annotated(4));
        Assert.Equal(1, back.Reactivated);
        await AssertKeptAsync(s);
    }

    [Fact]
    public async Task A_room_that_changes_type_changes_its_empty_seats_but_not_the_ones_in_use()
    {
        var s = await NewFloorAsync("DR05");
        await ReplaceAsync(s.Floor.Id, WorkplaceSeed.Annotated(4, type: "canteen"));
        var after = await SpotsAsync(s.Floor.Id);
        Assert.Equal([false, true, false, false], after.Select(w => w.IsDesk).ToArray()); // Anna's seat stays a desk
        Assert.All(after, w => Assert.Equal("canteen", w.RoomType));
    }

    [Fact]
    public async Task Seats_get_their_table_and_room_from_data_parent_or_from_where_they_are_drawn()
    {
        var svg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 100">
              <g id="rooms">
                <rect id="room-a" data-kind="room" data-id="A" data-name="Studio" data-type="office" x="0" y="0" width="100" height="100"/>
                <rect id="room-b" data-kind="room" data-id="B" data-name="Lab" data-type="office" x="100" y="0" width="100" height="100"/>
              </g>
              <g id="tables"><rect id="t1" data-kind="table" data-id="A-T1" data-parent="A" x="20" y="40" width="40" height="10"/></g>
              <g id="seats" transform="translate(0 0)">
                <circle id="s1" data-kind="seat" data-id="S1" data-parent="A-T1" cx="30" cy="35" r="3"/>
                <path id="s2" data-kind="seat" data-id="S2" d="M147 47 h6 v6 h-6 z"/>
              </g>
            </svg>
            """;
        var parsed = FloorplanImporter.Parse(svg);
        var s1 = parsed.Spots.Single(p => p.Label == "S1");
        Assert.Equal(("A-T1", "A", "Studio"), (s1.TableId, s1.RoomId, s1.RoomName));
        var s2 = parsed.Spots.Single(p => p.Label == "S2");
        Assert.Equal((null, "B", "Lab"), (s2.TableId, s2.RoomId, s2.RoomName));

        // the renderer puts the badges of S1 above it (away from its table, which is not its parent element)
        var floor = new Floor { Id = 1, Svg = parsed.Svg, Css = parsed.Css, ViewBox = parsed.ViewBox };
        var spots = parsed.Spots.Select((p, i) => new Workspot { Id = i + 1, ElementId = p.ElementId, Label = p.Label, TableId = p.TableId, RoomId = p.RoomId, IsActive = true, IsDesk = true }).ToList();
        var map = FloorplanRenderer.Render(floor, spots, [], new Dictionary<int, SpotState> { [1] = new("Fox", false, "DT-1", 0, false) });
        Assert.Contains("fp-label fp-label-up", map);
    }

    [Fact]
    public async Task A_seat_marked_on_a_plain_drawing_follows_its_shape_when_automatic_ids_shift()
    {
        var site = await _f.WithDbAsync(db => Seed.SiteAsync(db, "DR07"));
        var floor = (await WorkplaceSeed.ImportAsync(_f, site, "Plain", WorkplaceSeed.Plain())).Floor;
        await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<WorkspotService>().MarkAsync(floor.Id, ["fp-e3"])); // the desk at x 40
        // a revised export with one more shape first: every automatic id moves up by one
        var revised = WorkplaceSeed.Plain().Replace("<rect x=\"1\"", "<circle cx=\"290\" cy=\"90\" r=\"2\"/><rect x=\"1\"");
        var r = await ReplaceAsync(floor.Id, revised);
        Assert.Equal((0, 0, 1), (r.Added, r.Deactivated, r.Moved));
        Assert.Equal("fp-e4", Assert.Single(await SpotsAsync(floor.Id)).ElementId);
    }
}
