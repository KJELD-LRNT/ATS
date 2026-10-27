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
            RoomType = s.RoomType, TableId = s.TableId, IsActive = true,
        }).ToList();
        return (floor, spots);
    }

    [Fact]
    public void Seats_get_their_workspot_id_label_and_room()
    {
        var (floor, spots) = Build();
        var svg = FloorplanRenderer.Render(floor, spots, []);

        Assert.Contains("data-ws=\"10\"", svg);
        Assert.Contains("data-ws=\"12\"", svg);
        Assert.Contains("data-label=\"R7-T01-S01\"", svg);
        Assert.Contains("data-room=\"R7\"", svg);
        Assert.Contains("data-room-name=\"Fox room\"", svg);
        Assert.Contains("class=\"fp-seat fp-ws\"", svg);
        Assert.DoesNotContain("fp-teamlayer", svg);
    }

    [Fact]
    public void Team_members_are_coloured_and_the_team_is_outlined_and_labelled_and_names_are_escaped()
    {
        var (floor, spots) = Build();
        var team = new FloorTeam { Id = 5, FloorId = 1, Name = "Fox & <b>Co</b>", ColorIndex = 3 };
        spots[0].TeamId = 5;
        spots[1].TeamId = 5;
        var svg = FloorplanRenderer.Render(floor, spots, [team]);

        Assert.Contains("fp-ws fp-team-3", svg);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(svg, "<circle[^>]*data-team=\"5\"").Count);
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
}
