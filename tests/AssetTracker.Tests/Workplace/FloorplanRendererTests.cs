using System.Text.RegularExpressions;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Workplace;

namespace AssetTracker.Tests.Workplace;

public class FloorplanRendererTests
{
    private static (Floor Floor, List<Workspot> Spots) Build()
    {
        var parsed = FloorplanImporter.Parse(WorkplaceSeed.Annotated(seats: 3, room: "R7", roomName: "Fox room"));
        var floor = new Floor { Id = 1, Svg = parsed.Svg, Css = parsed.Css, ViewBox = parsed.ViewBox };
        var spots = parsed.Spots.Select((s, i) => new Workspot
        {
            Id = 10 + i, FloorId = 1, ElementId = s.ElementId, Label = s.Label, RoomId = s.RoomId, RoomName = s.RoomName,
            RoomType = s.RoomType, TableId = s.TableId, IsActive = true, IsDesk = true,
        }).ToList();
        return (floor, spots);
    }

    [Fact]
    public void Seats_get_their_workspot_id_label_room_and_state()
    {
        var (floor, spots) = Build();
        spots[2].IsDesk = false;
        var svg = FloorplanRenderer.Render(floor, spots, []);

        Assert.Contains("data-ws=\"10\"", svg);
        Assert.Contains("data-label=\"R7-T01-S01\"", svg);
        Assert.Contains("data-room=\"R7\"", svg);
        Assert.Contains("data-room-name=\"Fox room\"", svg);
        Assert.Contains("class=\"fp-seat fp-ws fp-free\"", svg);   // an empty desk is free
        Assert.Contains("class=\"fp-seat fp-ws fp-nodesk\"", svg); // not a desk: never free
        Assert.Contains("<title>R7-T01-S01 · free desk</title>", svg);
        Assert.DoesNotContain("fp-teamlayer", svg);
        // the icon shapes for the markers are in the drawing, and nothing is drawn for empty seats
        Assert.Contains("<symbol id=\"fp-i-person\"", svg);
        Assert.DoesNotContain("data-mark=", svg);
    }

    [Fact]
    public void A_taken_desk_gets_a_person_icon_desktop_and_screen_badges_and_a_label_with_name_and_tag()
    {
        var (floor, spots) = Build();
        var states = new Dictionary<int, SpotState>
        {
            [10] = new("Fox <Jr>", false, "DT-001", 2, false),
            [11] = new("Gone Person", true, null, 0, true),
            [12] = new(null, false, null, 1, false),
        };
        var svg = FloorplanRenderer.Render(floor, spots, [], states);

        Assert.Contains("class=\"fp-seat fp-ws fp-occ fp-has-desktop fp-has-screen\"", svg);
        Assert.Contains("class=\"fp-seat fp-ws fp-occ fp-left fp-flag\"", svg);
        Assert.Contains("class=\"fp-seat fp-ws fp-free fp-has-screen\"", svg); // a screen without a person: still free
        Assert.Contains("data-person=\"Fox &lt;Jr&gt;\"", svg);
        Assert.Contains("data-desktop=\"DT-001\"", svg);
        Assert.Contains("<title>R7-T01-S01 · Fox &lt;Jr&gt; · desktop DT-001 · 2 screens</title>", svg);
        Assert.Contains("(no longer works here)", svg);

        var mark10 = Regex.Match(svg, "<g class=\"fp-mark\" data-mark=\"10\">.*?</g>").Value;
        Assert.Contains("href=\"#fp-i-person\"", mark10);
        Assert.Contains("fp-badge-desktop", mark10);
        Assert.Contains("fp-badge-screen", mark10);
        Assert.Contains("fp-mark fp-mark-flag", svg);
        Assert.Contains("fp-flag-ring", svg);

        // the seats sit above their table, so the label goes up: name and desktop tag
        var label10 = Regex.Match(svg, "<g class=\"fp-label fp-label-up\"[^>]*data-label-for=\"10\"[^>]*>.*?</g>").Value;
        Assert.Contains(">Fox &lt;Jr&gt;</text>", label10);
        Assert.Contains("class=\"fp-label-tag\"", label10);
        Assert.Contains(">DT-001</text>", label10);
        Assert.Contains("fp-label-left-person", svg);
        Assert.DoesNotContain("data-label-for=\"12\"", svg); // nothing to name at a seat with only a screen
    }

    [Fact]
    public void Long_names_are_shortened_on_the_map_but_not_in_the_tooltip()
    {
        var (floor, spots) = Build();
        var name = "Bartholomew Cornelius Ravenswood-Smythe";
        var svg = FloorplanRenderer.Render(floor, spots, [], new Dictionary<int, SpotState> { [10] = new(name, false, null, 0, false) });
        Assert.Contains($"<title>R7-T01-S01 · {name}</title>", svg);
        Assert.Contains(">" + name[..(FloorplanRenderer.NameMax - 1)].TrimEnd() + "…</text>", svg);
    }

    [Fact]
    public void Team_members_are_coloured_and_the_team_is_outlined_and_labelled_and_names_are_escaped()
    {
        var (floor, spots) = Build();
        var team = new FloorTeam { Id = 5, FloorId = 1, Name = "Fox & <b>Co</b>", ColorIndex = 3 };
        spots[0].TeamId = 5;
        spots[1].TeamId = 5;
        var svg = FloorplanRenderer.Render(floor, spots, [team]);

        Assert.Contains("fp-free fp-team-3", svg);
        Assert.Equal(2, Regex.Matches(svg, "<circle[^>]*data-team=\"5\"").Count);
        Assert.Contains("class=\"fp-team-outline fp-team-3\"", svg);
        Assert.Contains("Fox &amp; &lt;b&gt;Co&lt;/b&gt;", svg);
        Assert.DoesNotContain("<b>", svg);
    }

    [Fact]
    public void A_switched_off_seat_is_marked_and_is_not_coloured_by_its_team()
    {
        var (floor, spots) = Build();
        var team = new FloorTeam { Id = 5, FloorId = 1, Name = "T", ColorIndex = 1 };
        spots[0].TeamId = 5;
        spots[0].IsActive = false;
        var svg = FloorplanRenderer.Render(floor, spots, [team]);

        Assert.Contains("fp-ws-off", svg);
        Assert.DoesNotContain("fp-team-1", svg);
    }

    [Fact]
    public void A_print_area_replaces_the_view_box_and_centres_find_the_seats_in_it()
    {
        var (floor, spots) = Build();
        var area = Area.Parse("60,70,40,30")!.Value;
        Assert.Contains("viewBox=\"60 70 40 30\"", FloorplanRenderer.Render(floor, spots, [], null, area));
        var centers = FloorplanRenderer.Centers(floor, spots);
        Assert.Equal([10, 11], spots.Where(s => area.Contains(centers[s.Id].X, centers[s.Id].Y)).Select(s => s.Id).ToArray());
        Assert.Null(Area.Parse("1,2,3"));
        Assert.Null(Area.Parse("1,2,0,4"));
        Assert.Null(Area.Parse("a,b,c,d"));
        Assert.Null(Area.Parse(null));
    }
}
