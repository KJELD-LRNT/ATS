using System.Xml.Linq;
using AssetTracker.Core.Workplace;

namespace AssetTracker.Tests.Workplace;

public class SvgGeometryTests
{
    private static XElement Find(XElement root, string id) => root.Descendants().Single(e => (string?)e.Attribute("id") == id);

    private static XElement Svg(string inner) => XElement.Parse($"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100">{inner}</svg>""");

    private static void Near(double expected, double actual) => Assert.InRange(actual, expected - 0.01, expected + 0.01);

    [Fact]
    public void Basic_shapes_are_measured_in_their_own_coordinates()
    {
        var root = Svg("""<rect id="r" x="10" y="20" width="30" height="5"/><circle id="c" cx="50" cy="50" r="4"/><ellipse id="e" cx="5" cy="5" rx="2" ry="1"/><line id="l" x1="1" y1="9" x2="7" y2="3"/><polygon id="p" points="0,0 10,0 5,8"/>""");
        Assert.Equal(new SvgBox(10, 20, 40, 25), SvgGeometry.Bounds(Find(root, "r")));
        Assert.Equal(new SvgBox(46, 46, 54, 54), SvgGeometry.Bounds(Find(root, "c")));
        Assert.Equal(new SvgBox(3, 4, 7, 6), SvgGeometry.Bounds(Find(root, "e")));
        Assert.Equal(new SvgBox(1, 3, 7, 9), SvgGeometry.Bounds(Find(root, "l")));
        Assert.Equal(new SvgBox(0, 0, 10, 8), SvgGeometry.Bounds(Find(root, "p")));
    }

    [Fact]
    public void Transforms_on_the_element_and_its_groups_are_applied()
    {
        var root = Svg("""<g transform="translate(100 50)"><g transform="scale(2)"><circle id="c" cx="5" cy="5" r="1"/></g><rect id="r" x="0" y="0" width="10" height="4" transform="rotate(90)"/></g><rect id="m" width="2" height="2" transform="matrix(1 0 0 1 7 8)"/>""");
        var c = SvgGeometry.Bounds(Find(root, "c"))!.Value;
        Assert.Equal((110.0, 60.0), (c.Cx, c.Cy));
        Assert.Equal(4, c.Width);
        var r = SvgGeometry.Bounds(Find(root, "r"))!.Value; // rotated a quarter turn around the group origin
        Near(96, r.MinX); Near(100, r.MaxX); Near(50, r.MinY); Near(60, r.MaxY);
        Assert.Equal(new SvgBox(7, 8, 9, 10), SvgGeometry.Bounds(Find(root, "m")));
    }

    [Fact]
    public void Paths_with_absolute_and_relative_commands_curves_and_arcs_are_bounded()
    {
        var root = Svg("""<path id="a" d="M10 10 h20 v5 H10 z"/><path id="b" d="m5,5 l10,0 c0,5 5,5 5,10 s-5,5-5,5 q-2,2-4,0 t-6,0 z"/><path id="c" d="M50 50 a5 5 0 0 1 10 0"/>""");
        Assert.Equal(new SvgBox(10, 10, 30, 15), SvgGeometry.Bounds(Find(root, "a")));
        var b = SvgGeometry.Bounds(Find(root, "b"))!.Value;
        Assert.Equal(5, b.MinX);
        Assert.Equal(5, b.MinY);
        Assert.Equal(20, b.MaxX);
        Assert.Equal(22, b.MaxY); // the control point of the quadratic curve
        var arc = SvgGeometry.Bounds(Find(root, "c"))!.Value; // the half circle above 50,50 - 60,50 fits in the box
        Assert.True(arc.MinY <= 45 && arc.MinX <= 50 && arc.MaxX >= 60);
    }

    [Fact]
    public void A_group_is_everything_drawn_in_it_without_defs_text_or_skipped_parts()
    {
        var root = Svg("""<g id="t" data-kind="table"><defs><rect width="999" height="999"/></defs><text x="500" y="500">Name</text><rect x="10" y="10" width="20" height="4"/><circle data-kind="seat" cx="15" cy="30" r="2"/></g><g id="empty"><title>x</title></g>""");
        var table = Find(root, "t");
        Assert.Equal(new SvgBox(10, 10, 30, 32), SvgGeometry.Bounds(table));
        Assert.Equal(new SvgBox(10, 10, 30, 14), SvgGeometry.Bounds(table, e => (string?)e.Attribute("data-kind") == "seat"));
        Assert.Null(SvgGeometry.Bounds(Find(root, "empty")));
        Assert.Equal((20.0, 21.0), SvgGeometry.Center(table));
    }

    [Fact]
    public void Broken_numbers_and_stray_tokens_do_not_hang_or_throw()
    {
        var root = Svg("""<path id="a" d="M 1 1 Z 5 5 L 3"/><polygon id="b" points="1,2,3"/><rect id="c" width="nope" height="4"/><rect id="d" x="1px" y="2" width="3px" height="4"/>""");
        Assert.NotNull(SvgGeometry.Bounds(Find(root, "a")));
        Assert.Equal(new SvgBox(1, 2, 1, 2), SvgGeometry.Bounds(Find(root, "b")));
        Assert.Null(SvgGeometry.Bounds(Find(root, "c")));
        Assert.Equal(new SvgBox(1, 2, 4, 6), SvgGeometry.Bounds(Find(root, "d")));
    }
}
