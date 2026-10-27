using AssetTracker.Core.Common;
using AssetTracker.Core.Workplace;

namespace AssetTracker.Tests.Workplace;

/// <summary>The import is a security boundary: the drawing is embedded in a page with a strict CSP.</summary>
public class FloorplanImporterTests
{
    private const string Hostile = """
        <?xml version="1.0"?>
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 100 50" onload="alert(1)">
          <script>alert(2)</script>
          <style>@import url(http://evil.example/x.css); .a{fill:url(http://evil.example/p); behavior:url(x.htc); stroke:#111}
                 @media print{.a{fill:red}} .b{background:url(javascript:alert(3))}</style>
          <rect id="r1" class="a" x="1" y="1" width="10" height="10" onclick="alert(4)" style="fill:red;stroke-width:2px;cursor:url(x.png)"/>
          <image href="http://evil.example/x.png" x="0" y="0" width="5" height="5"/>
          <a xlink:href="javascript:alert(5)"><rect id="r2" x="20" y="1" width="5" height="5" fill="url(http://evil.example/p)"/></a>
          <use href="#r1"/>
          <foreignObject width="10" height="10"><div xmlns="http://www.w3.org/1999/xhtml">hi</div></foreignObject>
          <animate attributeName="x" from="0" to="10" dur="1s"/>
        </svg>
        """;

    [Fact]
    public void Hostile_content_is_removed()
    {
        var result = FloorplanImporter.Parse(Hostile);
        var svg = result.Svg;

        foreach (var bad in new[]
                 {
                     "<script", "onload", "onclick", "<image", "<use", "foreignObject", "<animate", "<a ", "<style", "evil.example",
                     "javascript:", "xlink", "href", "cursor", "style=",
                 })
            Assert.DoesNotContain(bad, svg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil.example", result.Css);
        Assert.DoesNotContain("@import", result.Css);
        Assert.DoesNotContain("javascript", result.Css);
        Assert.DoesNotContain("behavior", result.Css);
        Assert.DoesNotContain("@media", result.Css);
    }

    [Fact]
    public void Style_attributes_become_presentation_attributes_and_classes_are_prefixed_and_scoped()
    {
        var result = FloorplanImporter.Parse(Hostile);

        // style="fill:red;stroke-width:2px" → attributes; the cursor declaration is dropped
        Assert.Contains("stroke-width=\"2px\"", result.Svg);
        Assert.Contains("fill=\"red\"", result.Svg);
        Assert.Contains("class=\"fp-a\"", result.Svg);
        // the one allowed declaration of the rule survives, scoped to the drawing
        Assert.Contains(".floorplan .fp-a{stroke:#111}", result.Css);
        // the root carries the unprefixed scope class
        Assert.StartsWith("<svg class=\"floorplan\"", result.Svg);
        Assert.Contains("xmlns=\"http://www.w3.org/2000/svg\"", result.Svg);
    }

    [Fact]
    public void A_pattern_fill_rule_becomes_an_attribute_because_a_separate_stylesheet_cannot_refer_to_it()
    {
        var result = FloorplanImporter.Parse(WorkplaceSeed.Annotated());

        Assert.DoesNotContain("url(", result.Css);
        Assert.Contains(".floorplan .fp-seat{fill:#2b6cb0}", result.Css);
        Assert.Contains(".floorplan .fp-room.fp-office{fill:#fdebcf;stroke:#cf9437}", result.Css);
        Assert.Contains("class=\"fp-nf\"", result.Svg);
        var nf = System.Text.RegularExpressions.Regex.Match(result.Svg, "<rect[^>]*class=\"fp-nf\"[^>]*>").Value;
        Assert.Contains("fill=\"url(#hatch)\"", nf);
    }

    [Fact]
    public void Declared_seats_become_spots_with_label_room_and_table()
    {
        var result = FloorplanImporter.Parse(WorkplaceSeed.Annotated(seats: 3, room: "6.010", roomName: "Aquarium", type: "meeting"));

        Assert.Equal("0 0 400 200", result.ViewBox);
        Assert.False(result.HasAutoIds);
        Assert.Equal(3, result.Spots.Count);
        var first = result.Spots[0];
        Assert.Equal("f1-seat-6.010-T01-S01", first.ElementId);
        Assert.Equal("6.010-T01-S01", first.Label);
        Assert.Equal("6.010", first.RoomId);
        Assert.Equal("Aquarium", first.RoomName);
        Assert.Equal("meeting", first.RoomType);
        Assert.Equal("6.010-T01", first.TableId);
        // data attributes survive (the map is built on them)
        Assert.Contains("data-kind=\"seat\"", result.Svg);
    }

    [Fact]
    public void A_plain_drawing_has_no_spots_and_its_shapes_get_ids_so_seats_can_be_marked()
    {
        var result = FloorplanImporter.Parse(WorkplaceSeed.Plain());

        Assert.Empty(result.Spots);
        Assert.True(result.HasAutoIds);
        Assert.Equal("0 0 300 100", result.ViewBox); // from width and height
        Assert.Contains("id=\"fp-e1\"", result.Svg);
        Assert.Contains("id=\"fp-e4\"", result.Svg);
    }

    [Fact]
    public void Duplicate_ids_are_dropped_after_the_first()
    {
        var result = FloorplanImporter.Parse(
            """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><rect id="a" width="1" height="1"/><rect id="a" width="2" height="2"/></svg>""");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result.Svg, "id=\"a\""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not xml at all")]
    [InlineData("<html><body>hi</body></html>")]
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg"><rect width="1" height="1"/></svg>""")] // no viewBox, width or height
    [InlineData("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 0 0"/>""")]
    [InlineData("""<svg viewBox="0 0 10 10"/>""")] // no SVG namespace
    public void Unusable_files_are_rejected_with_a_message(string xml) =>
        Assert.IsAssignableFrom<DomainException>(Record.Exception(() => FloorplanImporter.Parse(xml)));

    [Fact]
    public void External_entities_are_never_resolved()
    {
        const string xxe = """
            <?xml version="1.0"?>
            <!DOCTYPE svg [<!ENTITY secret SYSTEM "file:///etc/passwd">]>
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><text x="1" y="1">&secret;</text></svg>
            """;
        var ex = Record.Exception(() => FloorplanImporter.Parse(xxe));
        // either rejected, or parsed without the file's content
        if (ex is null) Assert.DoesNotContain("root:", FloorplanImporter.Parse(xxe).Svg);
        else Assert.IsAssignableFrom<DomainException>(ex);
    }

    [Fact]
    public void An_entity_expansion_bomb_is_rejected()
    {
        const string bomb = """
            <?xml version="1.0"?>
            <!DOCTYPE svg [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;"><!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;">]>
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><text x="1" y="1">&c;</text></svg>
            """;
        Assert.IsAssignableFrom<DomainException>(Record.Exception(() => FloorplanImporter.Parse(bomb)));
    }

    [Fact]
    public void A_drawing_over_the_size_limit_is_rejected() =>
        Assert.IsAssignableFrom<DomainException>(Record.Exception(() =>
            FloorplanImporter.Parse("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 1 1\">" + new string(' ', FloorplanImporter.MaxBytes) + "</svg>")));
}
