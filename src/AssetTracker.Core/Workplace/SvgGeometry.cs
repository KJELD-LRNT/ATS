using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AssetTracker.Core.Workplace;

/// <summary>An axis-aligned box in the drawing's own coordinates (the root <c>viewBox</c> space).</summary>
public readonly record struct SvgBox(double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
    public double Cx => (MinX + MaxX) / 2;
    public double Cy => (MinY + MaxY) / 2;
    public SvgBox Union(SvgBox o) => new(Math.Min(MinX, o.MinX), Math.Min(MinY, o.MinY), Math.Max(MaxX, o.MaxX), Math.Max(MaxY, o.MaxY));
    public bool Contains(double x, double y) => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;
}

/// <summary>
/// Where things are in a floor plan drawing, whatever way it is drawn: rectangles, circles, ellipses, lines, polygons,
/// paths and groups of them, under any <c>transform</c> (matrix, translate, scale, rotate, skew) on the element or its
/// ancestors. Boxes are in the root's user space, so markers, outlines and printed areas line up with the drawing.
/// Curves are bounded by their control points (a box that may be a little larger than the curve, never smaller).
/// </summary>
public static partial class SvgGeometry
{
    private static readonly HashSet<string> NotDrawn = new(StringComparer.Ordinal)
        { "defs", "clipPath", "mask", "pattern", "marker", "symbol", "linearGradient", "radialGradient", "filter", "title", "desc", "style", "metadata", "text" };

    [GeneratedRegex(@"(matrix|translate|scale|rotate|skewX|skewY)\s*\(([^)]*)\)")]
    private static partial Regex TransformPart();

    [GeneratedRegex(@"[-+]?(?:\d*\.\d+|\d+\.?)(?:[eE][-+]?\d+)?")]
    private static partial Regex Number();

    [GeneratedRegex(@"[MmZzLlHhVvCcSsQqTtAa]|[-+]?(?:\d*\.\d+|\d+\.?)(?:[eE][-+]?\d+)?")]
    private static partial Regex PathToken();

    /// <summary>
    /// The box of an element (a group: of everything drawn in it) in the root's user space, or null when nothing
    /// measurable is drawn. <paramref name="skip"/> leaves out sub-trees (for example the seats of a table).
    /// </summary>
    public static SvgBox? Bounds(XElement element, Func<XElement, bool>? skip = null) =>
        Measure(element, ToRoot(element.Parent), skip);

    /// <summary>The centre of an element's box.</summary>
    public static (double X, double Y)? Center(XElement element) => Bounds(element) is { } b ? (b.Cx, b.Cy) : null;

    private static SvgBox? Measure(XElement e, Matrix parent, Func<XElement, bool>? skip)
    {
        var name = e.Name.LocalName;
        if (NotDrawn.Contains(name) || (skip is not null && skip(e))) return null;
        var m = e.Parent is null ? parent : parent.Times(Parse((string?)e.Attribute("transform")));
        switch (name)
        {
            case "g" or "svg" or "a" or "switch":
                SvgBox? all = null;
                foreach (var child in e.Elements())
                    if (Measure(child, m, skip) is { } b) all = all is { } a ? a.Union(b) : b;
                return all;
            default:
                return Of(Points(e), m);
        }
    }

    private static SvgBox? Of(IEnumerable<(double X, double Y)> points, Matrix m)
    {
        SvgBox? box = null;
        foreach (var (x, y) in points)
        {
            var (px, py) = m.Apply(x, y);
            if (double.IsNaN(px) || double.IsNaN(py) || double.IsInfinity(px) || double.IsInfinity(py)) continue;
            box = box is { } b ? new SvgBox(Math.Min(b.MinX, px), Math.Min(b.MinY, py), Math.Max(b.MaxX, px), Math.Max(b.MaxY, py))
                : new SvgBox(px, py, px, py);
        }
        return box;
    }

    /// <summary>The points that bound a shape in its own coordinates (corners of its box, a line's ends, polygon points, path points).</summary>
    private static IEnumerable<(double, double)> Points(XElement e)
    {
        switch (e.Name.LocalName)
        {
            case "rect" when D(e, "width") is { } w && D(e, "height") is { } h:
            {
                var x = D(e, "x") ?? 0; var y = D(e, "y") ?? 0;
                return [(x, y), (x + w, y), (x, y + h), (x + w, y + h)];
            }
            case "circle" when D(e, "r") is { } r:
            {
                var cx = D(e, "cx") ?? 0; var cy = D(e, "cy") ?? 0;
                return [(cx - r, cy - r), (cx + r, cy - r), (cx - r, cy + r), (cx + r, cy + r)];
            }
            case "ellipse" when D(e, "rx") is { } rx && D(e, "ry") is { } ry:
            {
                var cx = D(e, "cx") ?? 0; var cy = D(e, "cy") ?? 0;
                return [(cx - rx, cy - ry), (cx + rx, cy - ry), (cx - rx, cy + ry), (cx + rx, cy + ry)];
            }
            case "line":
                return [(D(e, "x1") ?? 0, D(e, "y1") ?? 0), (D(e, "x2") ?? 0, D(e, "y2") ?? 0)];
            case "polygon" or "polyline":
                return Pairs(Number().Matches((string?)e.Attribute("points") ?? "").Select(m => Num(m.Value)).ToList());
            case "path":
                return PathPoints((string?)e.Attribute("d") ?? "");
            default:
                return [];
        }
    }

    private static IEnumerable<(double, double)> Pairs(List<double> v)
    {
        for (var i = 0; i + 1 < v.Count; i += 2) yield return (v[i], v[i + 1]);
    }

    /// <summary>End points and control points of a path; an arc adds its radii around both ends.</summary>
    private static List<(double, double)> PathPoints(string d)
    {
        var points = new List<(double, double)>();
        var tokens = PathToken().Matches(d).Select(m => m.Value).ToList();
        double x = 0, y = 0, startX = 0, startY = 0;
        var cmd = 'M';
        var i = 0;
        bool More() => i < tokens.Count && !char.IsLetter(tokens[i][0]);
        double Next() => Num(tokens[i++]);

        while (i < tokens.Count)
        {
            if (char.IsLetter(tokens[i][0])) cmd = tokens[i++][0];
            var rel = char.IsLower(cmd);
            double ox = rel ? x : 0, oy = rel ? y : 0;
            switch (char.ToUpperInvariant(cmd))
            {
                case 'Z':
                    x = startX; y = startY;
                    if (More()) i++; // a stray number after Z is not valid; skip it rather than loop
                    continue;
                case 'M' or 'L' or 'T':
                    if (!More()) break;
                    x = ox + Next(); y = oy + (More() ? Next() : 0);
                    points.Add((x, y));
                    if (char.ToUpperInvariant(cmd) == 'M') { startX = x; startY = y; cmd = rel ? 'l' : 'L'; }
                    continue;
                case 'H':
                    if (!More()) break;
                    x = ox + Next(); points.Add((x, y));
                    continue;
                case 'V':
                    if (!More()) break;
                    y = oy + Next(); points.Add((x, y));
                    continue;
                case 'C' or 'S' or 'Q':
                {
                    var pairs = char.ToUpperInvariant(cmd) == 'C' ? 3 : 2;
                    for (var p = 0; p < pairs && More(); p++)
                    {
                        var px = ox + Next(); var py = oy + (More() ? Next() : 0);
                        points.Add((px, py));
                        if (p == pairs - 1) { x = px; y = py; }
                    }
                    continue;
                }
                case 'A':
                {
                    if (!More()) break;
                    var rx = Math.Abs(Next()); var ry = More() ? Math.Abs(Next()) : rx;
                    for (var skipFlags = 0; skipFlags < 3 && More(); skipFlags++) Next();
                    points.Add((x - rx, y - ry)); points.Add((x + rx, y + ry));
                    x = ox + (More() ? Next() : 0); y = oy + (More() ? Next() : 0);
                    points.Add((x - rx, y - ry)); points.Add((x + rx, y + ry));
                    continue;
                }
            }
            // A number without a command it can belong to: skip it.
            if (i < tokens.Count && !char.IsLetter(tokens[i][0])) i++;
        }
        return points;
    }

    /// <summary>The transform from an element's own coordinates to the root's user space (the root svg itself excluded).</summary>
    private static Matrix ToRoot(XElement? e)
    {
        var chain = new List<XElement>();
        for (var at = e; at is not null && at.Parent is not null; at = at.Parent) chain.Add(at);
        var m = Matrix.Identity;
        for (var k = chain.Count - 1; k >= 0; k--) m = m.Times(Parse((string?)chain[k].Attribute("transform")));
        return m;
    }

    /// <summary>A <c>transform</c> attribute as one matrix (identity when empty or not understood).</summary>
    internal static Matrix Parse(string? transform)
    {
        var m = Matrix.Identity;
        if (string.IsNullOrWhiteSpace(transform)) return m;
        foreach (Match part in TransformPart().Matches(transform))
        {
            var a = Number().Matches(part.Groups[2].Value).Select(n => Num(n.Value)).ToArray();
            double Arg(int k, double fallback) => k < a.Length ? a[k] : fallback;
            m = m.Times(part.Groups[1].Value switch
            {
                "matrix" when a.Length == 6 => new Matrix(a[0], a[1], a[2], a[3], a[4], a[5]),
                "translate" => new Matrix(1, 0, 0, 1, Arg(0, 0), Arg(1, 0)),
                "scale" => new Matrix(Arg(0, 1), 0, 0, Arg(1, Arg(0, 1)), 0, 0),
                "rotate" => Rotate(Arg(0, 0), Arg(1, 0), Arg(2, 0)),
                "skewX" => new Matrix(1, 0, Math.Tan(Arg(0, 0) * Math.PI / 180), 1, 0, 0),
                "skewY" => new Matrix(1, Math.Tan(Arg(0, 0) * Math.PI / 180), 0, 1, 0, 0),
                _ => Matrix.Identity,
            });
        }
        return m;
    }

    private static Matrix Rotate(double degrees, double cx, double cy)
    {
        var r = degrees * Math.PI / 180;
        var (cos, sin) = (Math.Cos(r), Math.Sin(r));
        return new Matrix(1, 0, 0, 1, cx, cy).Times(new Matrix(cos, sin, -sin, cos, 0, 0)).Times(new Matrix(1, 0, 0, 1, -cx, -cy));
    }

    private static double? D(XElement e, string name) =>
        double.TryParse(((string?)e.Attribute(name))?.Trim().TrimEnd('x', 'p'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static double Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>An SVG transform matrix [a c e; b d f; 0 0 1].</summary>
    internal readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Matrix Identity = new(1, 0, 0, 1, 0, 0);

        public Matrix Times(Matrix o) => new(
            A * o.A + C * o.B, B * o.A + D * o.B,
            A * o.C + C * o.D, B * o.C + D * o.D,
            A * o.E + C * o.F + E, B * o.E + D * o.F + F);

        public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);
    }
}
