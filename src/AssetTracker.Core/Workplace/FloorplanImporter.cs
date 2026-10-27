using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AssetTracker.Core.Common;

namespace AssetTracker.Core.Workplace;

/// <summary>A seat found in a drawing (a <c>data-kind="seat"</c> element).</summary>
public sealed record ParsedSpot(string ElementId, string Label, string? RoomId, string? RoomName, string? RoomType, string? TableId);

/// <param name="Svg">The sanitised drawing, ready to be embedded in a page.</param>
/// <param name="Css">Its stylesheet, scoped (see <see cref="FloorplanCss"/>).</param>
/// <param name="ViewBox">"minX minY width height".</param>
/// <param name="Spots">The seats the drawing itself declares (none for a plain drawing: seats are then marked by hand).</param>
/// <param name="HasAutoIds">Whether shapes without an <c>id</c> were given one (<c>fp-e1</c>, <c>fp-e2</c>, ... in document order).</param>
public sealed record ParsedFloorplan(string Svg, string Css, string ViewBox, IReadOnlyList<ParsedSpot> Spots, bool HasAutoIds);

/// <summary>
/// Reads an uploaded SVG floor plan and returns a version that is safe to embed in a page with a strict CSP: only
/// drawing elements and presentation attributes survive (no scripts, links, images, <c>foreignObject</c>, event
/// handlers or external references), <c>style</c> attributes become presentation attributes, the stylesheet is scoped
/// (<see cref="FloorplanCss"/>) and class names are prefixed. Attributes <c>data-*</c> are kept: the drawing's
/// <c>data-kind</c>, <c>data-id</c>, <c>data-name</c>, <c>data-type</c> and <c>data-parent</c> declare rooms, tables
/// and seats (the format of the floor plan renderer used by the facility team).
/// </summary>
public static partial class FloorplanImporter
{
    public const int MaxBytes = 5 * 1024 * 1024;
    private const int MaxElements = 60_000;
    private const int MaxDepth = 64;

    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    private static readonly HashSet<string> Elements = new(StringComparer.Ordinal)
    {
        "svg", "g", "defs", "title", "desc", "rect", "circle", "ellipse", "line", "polyline", "polygon", "path", "text",
        "tspan", "pattern", "linearGradient", "radialGradient", "stop", "clipPath",
    };

    private static readonly HashSet<string> Shapes = new(StringComparer.Ordinal)
        { "rect", "circle", "ellipse", "polygon", "path", "polyline", "line" };

    private static readonly HashSet<string> Colors = new(StringComparer.Ordinal) { "fill", "stroke", "stop-color" };

    private static readonly HashSet<string> Plain = new(StringComparer.Ordinal)
    {
        "x", "y", "x1", "y1", "x2", "y2", "cx", "cy", "r", "rx", "ry", "width", "height", "points", "d", "transform",
        "stroke-width", "stroke-dasharray", "stroke-dashoffset", "stroke-linejoin", "stroke-linecap", "opacity",
        "fill-opacity", "stroke-opacity", "fill-rule", "clip-rule", "font-size", "font-family", "font-weight", "font-style",
        "text-anchor", "dominant-baseline", "letter-spacing", "pointer-events", "visibility", "display", "patternUnits",
        "patternContentUnits", "patternTransform", "gradientUnits", "gradientTransform", "offset", "dx", "dy",
        "clip-path", "textLength", "lengthAdjust", "stroke-miterlimit",
    };

    [GeneratedRegex(@"^[A-Za-z0-9_.:\-]{1,200}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,80}$")]
    private static partial Regex ClassPattern();

    [GeneratedRegex(@"^data-[a-z0-9\-]{1,40}$")]
    private static partial Regex DataAttribute();

    [GeneratedRegex(@"^(none|currentColor|transparent|inherit|#[0-9A-Fa-f]{3,8}|(rgb|rgba|hsl|hsla)\([0-9\s.,%]+\)|[A-Za-z]{3,30}|url\(\s*#[A-Za-z0-9_.\-]+\s*\))$")]
    private static partial Regex ColorValue();

    [GeneratedRegex(@"^\s*(-?\d+(\.\d+)?)[\s,]+(-?\d+(\.\d+)?)[\s,]+(\d+(\.\d+)?)[\s,]+(\d+(\.\d+)?)\s*$")]
    private static partial Regex ViewBoxPattern();

    [GeneratedRegex(@"^\s*(\d+(\.\d+)?)\s*(px)?\s*$")]
    private static partial Regex PlainLength();

    private sealed class Context
    {
        public IReadOnlyDictionary<string, List<(string Property, string Value)>> ClassAttributes = new Dictionary<string, List<(string, string)>>();
        public bool AutoIds;
        public int Auto;
        public int Count;
        public bool AssignedAuto;
        public HashSet<string> Ids = new(StringComparer.Ordinal);
    }

    public static ParsedFloorplan Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) throw new DomainException("floor.empty", "The file is empty.");
        if (xml.Length > MaxBytes) throw new DomainException("floor.too-large", $"The drawing is larger than {MaxBytes / 1024 / 1024} MB.");

        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersFromEntities = 0 };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            doc = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException)
        {
            throw new DomainException("floor.invalid", "The file is not valid SVG (XML) that can be read.");
        }

        var root = doc.Root;
        if (root is null || root.Name != Svg + "svg")
            throw new DomainException("floor.not-svg", "The file is not an SVG drawing.");

        var viewBox = ReadViewBox(root);

        var ctx = new Context();
        var cssText = string.Concat(doc.Descendants(Svg + "style").Select(s => s.Value + "\n"));
        var scoped = FloorplanCss.Scope(cssText);
        ctx.ClassAttributes = scoped.ClassAttributes;
        // A drawing that declares its seats is left as it is; a plain one gets ids on its shapes so seats can be marked.
        ctx.AutoIds = !doc.Descendants().Any(e => (string?)e.Attribute("data-kind") == "seat");

        var clean = Clean(root, ctx, 0, isRoot: true) ?? throw new DomainException("floor.not-svg", "The file is not an SVG drawing.");
        // The scope of the drawing's stylesheet (".floorplan .fp-seat"): the root carries the one unprefixed class.
        clean.SetAttributeValue("class", "floorplan");
        clean.SetAttributeValue("viewBox", viewBox);
        clean.SetAttributeValue("preserveAspectRatio", "xMidYMid meet");
        var svg = clean.ToString(SaveOptions.DisableFormatting);

        return new ParsedFloorplan(svg, scoped.Css, viewBox, FindSpots(clean), ctx.AssignedAuto);
    }

    private static string ReadViewBox(XElement root)
    {
        var vb = (string?)root.Attribute("viewBox");
        if (vb is not null && ViewBoxPattern().Match(vb) is { Success: true } m)
        {
            var w = double.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture);
            var h = double.Parse(m.Groups[7].Value, CultureInfo.InvariantCulture);
            if (w > 0 && h > 0)
                return string.Join(' ', new[] { 1, 3, 5, 7 }.Select(i => double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture).ToString("0.###", CultureInfo.InvariantCulture)));
        }
        else if (vb is null &&
                 PlainLength().Match((string?)root.Attribute("width") ?? "") is { Success: true } mw &&
                 PlainLength().Match((string?)root.Attribute("height") ?? "") is { Success: true } mh)
        {
            return $"0 0 {double.Parse(mw.Groups[1].Value, CultureInfo.InvariantCulture):0.###} {double.Parse(mh.Groups[1].Value, CultureInfo.InvariantCulture):0.###}"
                .Replace(',', '.');
        }
        throw new DomainException("floor.viewbox", "The drawing has no usable viewBox (or width and height).");
    }

    private static XElement? Clean(XElement source, Context ctx, int depth, bool isRoot = false)
    {
        if (depth > MaxDepth) throw new DomainException("floor.too-deep", "The drawing is nested too deeply.");
        if (source.Name.Namespace != Svg || !Elements.Contains(source.Name.LocalName)) return null;
        if (++ctx.Count > MaxElements) throw new DomainException("floor.too-complex", "The drawing has too many elements.");

        var name = source.Name.LocalName;
        var target = new XElement(Svg + name);
        var classes = new List<string>();
        var attrs = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var a in source.Attributes())
        {
            if (a.IsNamespaceDeclaration || a.Name.Namespace != XNamespace.None) continue;
            var key = a.Name.LocalName;
            var value = a.Value;
            if (key == "class")
            {
                classes.AddRange(value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(c => ClassPattern().IsMatch(c)));
            }
            else if (key == "style")
            {
                foreach (var decl in value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    var colon = decl.IndexOf(':');
                    if (colon <= 0) continue;
                    var prop = decl[..colon].Trim().ToLowerInvariant();
                    var val = decl[(colon + 1)..].Trim();
                    // style wins over the same presentation attribute, as in a browser
                    if (AllowedAttribute(prop, val)) attrs[prop] = val;
                }
            }
            else if (key == "id")
            {
                if (IdPattern().IsMatch(value) && ctx.Ids.Add(value)) attrs["id"] = value;
            }
            else if (DataAttribute().IsMatch(key))
            {
                if (value.Length <= 500) attrs[key] = value;
            }
            else if (key is "role")
            {
                if (value is "img" or "group" or "presentation") attrs[key] = value;
            }
            else if (key is "aria-label")
            {
                attrs[key] = value.Length > 200 ? value[..200] : value;
            }
            else if (!isRoot || key is not ("width" or "height" or "viewBox" or "preserveAspectRatio"))
            {
                if (AllowedAttribute(key, value) && !attrs.ContainsKey(key)) attrs[key] = value;
            }
        }

        // Rules whose value was a local url(#...) (a pattern fill) become attributes of the elements of that class.
        foreach (var c in classes)
            if (ctx.ClassAttributes.TryGetValue(FloorplanCss.ClassPrefix + c, out var extra))
                foreach (var (prop, val) in extra)
                    attrs.TryAdd(prop, val);

        if (ctx.AutoIds && !isRoot && (Shapes.Contains(name) || name == "g") && !attrs.ContainsKey("id"))
        {
            string id;
            do { id = "fp-e" + (++ctx.Auto).ToString(CultureInfo.InvariantCulture); } while (!ctx.Ids.Add(id));
            attrs["id"] = id;
            ctx.AssignedAuto = true;
        }

        if (classes.Count > 0)
            target.SetAttributeValue("class", string.Join(' ', classes.Distinct().Select(c => FloorplanCss.ClassPrefix + c)));
        foreach (var (k, v) in attrs) target.SetAttributeValue(k, v);

        foreach (var node in source.Nodes())
        {
            switch (node)
            {
                case XElement child:
                    if (Clean(child, ctx, depth + 1) is { } cleaned) target.Add(cleaned);
                    break;
                case XText text when name is "text" or "tspan" or "title" or "desc":
                    var t = text.Value;
                    target.Add(new XText(t.Length > 300 ? t[..300] : t));
                    break;
            }
        }
        return target;
    }

    private static bool AllowedAttribute(string name, string value)
    {
        if (Colors.Contains(name)) return ColorValue().IsMatch(value.Trim()) && FloorplanCss.IsSafeValue(value);
        if (Plain.Contains(name)) return FloorplanCss.IsSafeValue(value);
        return false;
    }

    /// <summary>The seats the drawing declares, each with the room and table it sits in.</summary>
    /// <summary>
    /// The seats a drawing declares, each with its table and room. A seat belongs to the table and room it is drawn in, or
    /// else the ones its <c>data-parent</c> (or its table's) names, or else the smallest room whose outline it lies in, so
    /// a revised drawing that groups its elements differently still gives the same answer.
    /// </summary>
    private static List<ParsedSpot> FindSpots(XElement root)
    {
        Dictionary<string, XElement> ById(string kind) => root.Descendants()
            .Where(e => (string?)e.Attribute("data-kind") == kind && (string?)e.Attribute("data-id") is not null)
            .GroupBy(e => (string)e.Attribute("data-id")!)
            .ToDictionary(g => g.Key, g => g.First());
        var rooms = ById("room");
        var tables = ById("table");
        List<(XElement Room, SvgBox Box)>? roomBoxes = null;

        var spots = new List<ParsedSpot>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var seat in root.Descendants().Where(e => (string?)e.Attribute("data-kind") == "seat"))
        {
            var id = (string?)seat.Attribute("id");
            if (id is null || !seen.Add(id)) continue;

            var parent = (string?)seat.Attribute("data-parent");
            var table = seat.Ancestors().FirstOrDefault(a => (string?)a.Attribute("data-kind") == "table")
                        ?? (parent is not null && tables.TryGetValue(parent, out var t) ? t : null);
            var room = seat.Ancestors().FirstOrDefault(a => (string?)a.Attribute("data-kind") == "room");
            var roomId = (string?)room?.Attribute("data-id") ?? (string?)table?.Attribute("data-parent")
                         ?? (parent is not null && rooms.ContainsKey(parent) ? parent : null);
            if (roomId is null && rooms.Count > 0 && SvgGeometry.Center(seat) is { } c)
            {
                roomBoxes ??= rooms.Values.Select(r => (Room: r, Box: SvgGeometry.Bounds(r))).Where(r => r.Box is not null)
                    .Select(r => (r.Room, r.Box!.Value)).OrderBy(r => r.Value.Width * r.Value.Height).ToList();
                roomId = (string?)roomBoxes.FirstOrDefault(r => r.Box.Contains(c.X, c.Y)).Room?.Attribute("data-id");
            }
            XElement? roomEl = null;
            if (roomId is not null) rooms.TryGetValue(roomId, out roomEl);
            var label = (string?)seat.Attribute("data-id") ?? id;
            spots.Add(new ParsedSpot(
                id, label.Length > 100 ? label[..100] : label, roomId,
                (string?)roomEl?.Attribute("data-name"), (string?)roomEl?.Attribute("data-type") ?? (string?)table?.Attribute("data-type"),
                (string?)table?.Attribute("data-id")));
        }
        return spots;
    }
}
