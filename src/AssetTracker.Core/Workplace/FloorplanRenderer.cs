using System.Globalization;
using System.Xml.Linq;
using AssetTracker.Core.Domain;

namespace AssetTracker.Core.Workplace;

/// <summary>A rectangle in drawing units.</summary>
public readonly record struct Area(double X, double Y, double Width, double Height)
{
    public bool Contains(double x, double y) => x >= X && x <= X + Width && y >= Y && y <= Y + Height;

    public override string ToString() => string.Join(' ', new[] { X, Y, Width, Height }.Select(v => v.ToString("0.##", CultureInfo.InvariantCulture)));

    /// <summary>Reads "x,y,width,height" (or space separated); null when it is not four numbers with a positive size.</summary>
    public static Area? Parse(string? text)
    {
        var parts = (text ?? "").Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4) return null;
        var v = new double[4];
        for (var i = 0; i < 4; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) || !double.IsFinite(v[i])) return null;
        return v[2] > 0 && v[3] > 0 ? new Area(v[0], v[1], v[2], v[3]) : null;
    }
}

/// <summary>
/// Puts the current state on a stored (already sanitised) drawing. Every seat gets its workspot id and the classes the
/// map is styled and scripted with (free desk, taken, not a desk, switched off, flagged, team colour); a seat with
/// someone or something placed gets a marker: a person icon in the seat, small desktop and screen icons just outside
/// it (away from its table) and a label with the person's name and the desktop's tag, which the page scales to a fixed
/// size on screen. Every team gets a dashed outline and its name. Only data of this application goes in; the drawing
/// itself was sanitised on import.
/// </summary>
public static class FloorplanRenderer
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    /// <summary>Longest name shown on the map (the tooltip and the side panel have the full name).</summary>
    public const int NameMax = 22;

    // Icon shapes (24 × 24) for the markers; referenced with <use>, coloured with CSS.
    private const string Icons =
        "<symbol id=\"fp-i-person\" viewBox=\"0 0 24 24\"><circle cx=\"12\" cy=\"7.5\" r=\"4.5\"/><path d=\"M3.5 22a8.5 8.5 0 0 1 17 0z\"/></symbol>" +
        "<symbol id=\"fp-i-desktop\" viewBox=\"0 0 24 24\"><path d=\"M7 2h10a1.5 1.5 0 0 1 1.5 1.5v17A1.5 1.5 0 0 1 17 22H7a1.5 1.5 0 0 1-1.5-1.5v-17A1.5 1.5 0 0 1 7 2zm2 3v2h6V5zm0 4v2h6V9zm3 8.2a1.6 1.6 0 1 0 0 .1z\"/></symbol>" +
        "<symbol id=\"fp-i-screen\" viewBox=\"0 0 24 24\"><path d=\"M3.5 3h17A1.5 1.5 0 0 1 22 4.5v11a1.5 1.5 0 0 1-1.5 1.5H14v2.5h3V22H7v-2.5h3V17H3.5A1.5 1.5 0 0 1 2 15.5v-11A1.5 1.5 0 0 1 3.5 3zm1 2.5v9h15v-9z\"/></symbol>" +
        "<symbol id=\"fp-i-warn\" viewBox=\"0 0 24 24\"><path d=\"M12 2 1 21h22zm-1.2 7h2.4l-.4 6h-1.6zm1.2 7.6a1.4 1.4 0 1 1 0 2.8 1.4 1.4 0 0 1 0-2.8z\"/></symbol>";

    public static string Render(Floor floor, IReadOnlyCollection<Workspot> spots, IReadOnlyCollection<FloorTeam> teams,
        IReadOnlyDictionary<int, SpotState>? states = null, Area? viewBox = null)
    {
        var root = XElement.Parse(floor.Svg);
        var byId = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var e in root.DescendantsAndSelf())
            if ((string?)e.Attribute("id") is { } id) byId.TryAdd(id, e);

        var teamById = teams.ToDictionary(t => t.Id);
        var teamBounds = new Dictionary<int, Box>();
        var markers = new XElement(Svg + "g", new XAttribute("class", "fp-marks"), new XAttribute("pointer-events", "none"));
        var labels = new XElement(Svg + "g", new XAttribute("class", "fp-labels"), new XAttribute("pointer-events", "none"));

        foreach (var spot in spots)
        {
            if (!byId.TryGetValue(spot.ElementId, out var el)) continue;
            var state = states is not null && states.TryGetValue(spot.Id, out var s) ? s : null;
            var classes = new List<string> { "fp-ws" };
            if (el.Attribute("data-kind") is null) classes.Add("fp-ws-manual");
            if (!spot.IsActive) classes.Add("fp-ws-off");
            else if (!spot.IsDesk) classes.Add("fp-nodesk");
            else classes.Add(state?.Person is null ? "fp-free" : "fp-occ");
            if (state?.PersonLeft == true) classes.Add("fp-left");
            if (state?.Flagged == true) classes.Add("fp-flag");
            if (state?.Desktop is not null) classes.Add("fp-has-desktop");
            if (state?.Screens > 0) classes.Add("fp-has-screen");

            el.SetAttributeValue("data-ws", spot.Id.ToString(CultureInfo.InvariantCulture));
            el.SetAttributeValue("data-label", spot.Label);
            if (spot.RoomId is not null) el.SetAttributeValue("data-room", spot.RoomId);
            if (spot.RoomName is not null) el.SetAttributeValue("data-room-name", spot.RoomName);
            if (spot.TableId is not null) el.SetAttributeValue("data-table", spot.TableId);
            if (state?.Person is not null) el.SetAttributeValue("data-person", state.Person);
            if (state?.Desktop is not null) el.SetAttributeValue("data-desktop", state.Desktop);
            if (spot.IsDesk) el.SetAttributeValue("data-desk", "1");

            var box = Bounds(el);
            if (spot.IsActive && spot.TeamId is { } teamId && teamById.TryGetValue(teamId, out var team))
            {
                classes.Add($"fp-team-{team.ColorIndex}");
                el.SetAttributeValue("data-team", teamId.ToString(CultureInfo.InvariantCulture));
                if (box is { } b) teamBounds[teamId] = teamBounds.TryGetValue(teamId, out var cur) ? cur.Union(b) : b;
            }

            // Tooltip: label, who sits there and what is placed.
            var tip = spot.Label;
            if (state?.Person is not null) tip += " · " + state.Person + (state.PersonLeft ? " (no longer works here)" : "");
            if (state?.Desktop is not null) tip += " · desktop " + state.Desktop;
            if (state?.Screens > 0) tip += $" · {state.Screens} screen{(state.Screens == 1 ? "" : "s")}";
            if (spot.IsActive && spot.IsDesk && state?.Person is null) tip += " · free desk";
            if (el.Element(Svg + "title") is { } title) title.Value = tip;
            else el.AddFirst(new XElement(Svg + "title", tip));

            var existing = (string?)el.Attribute("class");
            el.SetAttributeValue("class", string.IsNullOrEmpty(existing) ? string.Join(' ', classes) : existing + " " + string.Join(' ', classes));

            if (state is not null && box is { } seat)
                AddMarker(markers, labels, spot, state, seat, Direction(el, seat));
        }

        AddTeamLayer(root, floor, teamById, teamBounds);
        root.Add(markers, labels);
        root.AddFirst(new XElement(Svg + "defs", XElement.Parse("<x xmlns=\"http://www.w3.org/2000/svg\">" + Icons + "</x>").Elements()));
        if (viewBox is { } vb) root.SetAttributeValue("viewBox", vb.ToString());
        return root.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>The centre of each seat (for picking the seats of a printed area).</summary>
    public static IReadOnlyDictionary<int, (double X, double Y)> Centers(Floor floor, IReadOnlyCollection<Workspot> spots)
    {
        var root = XElement.Parse(floor.Svg);
        var byId = root.DescendantsAndSelf().Where(e => e.Attribute("id") is not null)
            .GroupBy(e => (string)e.Attribute("id")!).ToDictionary(g => g.Key, g => g.First());
        var result = new Dictionary<int, (double, double)>();
        foreach (var spot in spots)
            if (byId.TryGetValue(spot.ElementId, out var el) && Bounds(el) is { } b)
                result[spot.Id] = (b.Cx, b.Cy);
        return result;
    }

    private enum Side { Up, Down, Left, Right }

    /// <summary>Which way is "away from the table" for a seat (its marker and label go there); below when it has no table.</summary>
    private static Side Direction(XElement seat, Box box)
    {
        var table = seat.Ancestors().FirstOrDefault(a => (string?)a.Attribute("data-kind") == "table");
        var tableBox = table?.Elements(Svg + "rect").Select(r => Bounds(r)).FirstOrDefault(b => b is not null);
        if (tableBox is not { } t) return Side.Down;
        var dx = (box.Cx - t.Cx) / Math.Max(t.Width / 2, 0.001);
        var dy = (box.Cy - t.Cy) / Math.Max(t.Height / 2, 0.001);
        if (Math.Abs(dy) >= Math.Abs(dx)) return dy < 0 ? Side.Up : Side.Down;
        return dx < 0 ? Side.Left : Side.Right;
    }

    private static void AddMarker(XElement markers, XElement labels, Workspot spot, SpotState state, Box seat, Side side)
    {
        var r = Math.Max(Math.Min(seat.Width, seat.Height) / 2, 0.5);
        var (cx, cy) = (seat.Cx, seat.Cy);
        var g = new XElement(Svg + "g", new XAttribute("class", state.Flagged ? "fp-mark fp-mark-flag" : "fp-mark"), new XAttribute("data-mark", spot.Id.ToString(CultureInfo.InvariantCulture)));

        if (state.Person is not null)
            g.Add(Use("fp-i-person", state.PersonLeft ? "fp-i fp-i-person fp-i-left" : "fp-i fp-i-person", cx - r * 0.72, cy - r * 0.72, r * 1.44));

        // Desktop and screen badges sit just outside the seat, side by side, away from the table.
        var badge = r * 0.62;
        var step = r * 0.68;
        var (bx, by, ox, oy) = side switch
        {
            Side.Up => (cx, cy - r - badge * 1.05, step, 0.0),
            Side.Down => (cx, cy + r + badge * 1.05, step, 0.0),
            Side.Left => (cx - r - badge * 1.05, cy, 0.0, step),
            _ => (cx + r + badge * 1.05, cy, 0.0, step),
        };
        var shown = new List<string>();
        if (state.Desktop is not null) shown.Add("desktop");
        if (state.Screens > 0) shown.Add("screen");
        for (var i = 0; i < shown.Count; i++)
        {
            var shift = shown.Count == 1 ? 0 : (i == 0 ? -1 : 1);
            var x = bx + ox * shift;
            var y = by + oy * shift;
            g.Add(new XElement(Svg + "circle", new XAttribute("class", $"fp-badge fp-badge-{shown[i]}"),
                new XAttribute("cx", Num(x)), new XAttribute("cy", Num(y)), new XAttribute("r", Num(badge))));
            g.Add(Use($"fp-i-{shown[i]}", "fp-i fp-i-badge", x - badge * 0.7, y - badge * 0.7, badge * 1.4));
        }
        if (state.Flagged)
            g.Add(new XElement(Svg + "circle", new XAttribute("class", "fp-flag-ring"), new XAttribute("cx", Num(cx)),
                new XAttribute("cy", Num(cy)), new XAttribute("r", Num(r * 1.25))));
        markers.Add(g);

        if (state.Person is null && state.Desktop is null) return;
        // The label anchor is past the badges; the page scales the text to a fixed size on screen around it.
        var gap = r + badge * 2.3;
        var (ax, ay, cls) = side switch
        {
            Side.Up => (cx, cy - gap, "fp-label fp-label-up"),
            Side.Down => (cx, cy + gap, "fp-label fp-label-down"),
            Side.Left => (cx - gap, cy, "fp-label fp-label-left"),
            _ => (cx + gap, cy, "fp-label fp-label-right"),
        };
        var label = new XElement(Svg + "g", new XAttribute("class", cls),
            new XAttribute("data-ax", Num(ax)), new XAttribute("data-ay", Num(ay)), new XAttribute("data-label-for", spot.Id.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("transform", $"translate({Num(ax)} {Num(ay)})"));
        var lines = new List<(string Text, string Class)>();
        if (state.Person is not null) lines.Add((Short(state.Person), state.PersonLeft ? "fp-label-name fp-label-left-person" : "fp-label-name"));
        if (state.Desktop is not null) lines.Add((state.Desktop, "fp-label-tag"));
        // Line positions in screen pixels (the group is scaled so one unit is one pixel): stacked away from the seat.
        const double lineHeight = 13;
        for (var i = 0; i < lines.Count; i++)
        {
            double y = side switch
            {
                Side.Up => -4 - (lines.Count - 1 - i) * lineHeight,
                Side.Down => 10 + i * lineHeight,
                _ => 4 + (i - (lines.Count - 1) / 2.0) * lineHeight,
            };
            label.Add(new XElement(Svg + "text", new XAttribute("class", lines[i].Class), new XAttribute("y", Num(y)), lines[i].Text));
        }
        labels.Add(label);
    }

    private static string Short(string name) => name.Length <= NameMax ? name : name[..(NameMax - 1)].TrimEnd() + "…";

    private static XElement Use(string icon, string cls, double x, double y, double size) =>
        new(Svg + "use", new XAttribute("href", "#" + icon), new XAttribute("class", cls),
            new XAttribute("x", Num(x)), new XAttribute("y", Num(y)), new XAttribute("width", Num(size)), new XAttribute("height", Num(size)));

    private static void AddTeamLayer(XElement root, Floor floor, Dictionary<int, FloorTeam> teams, Dictionary<int, Box> bounds)
    {
        if (bounds.Count == 0) return;
        var parts = floor.ViewBox.Split(' ');
        var width = double.Parse(parts[2], CultureInfo.InvariantCulture);
        var pad = width / 330;
        var font = width / 170;
        var layer = new XElement(Svg + "g", new XAttribute("class", "fp-teamlayer"), new XAttribute("pointer-events", "none"));
        foreach (var (teamId, b) in bounds.OrderBy(kv => kv.Key))
        {
            var team = teams[teamId];
            var x = b.MinX - pad; var y = b.MinY - pad;
            layer.Add(new XElement(Svg + "rect",
                new XAttribute("class", $"fp-team-outline fp-team-{team.ColorIndex}"),
                new XAttribute("x", Num(x)), new XAttribute("y", Num(y)),
                new XAttribute("width", Num(b.Width + 2 * pad)), new XAttribute("height", Num(b.Height + 2 * pad)),
                new XAttribute("rx", Num(pad)), new XAttribute("fill", "none"), new XAttribute("data-team", teamId.ToString(CultureInfo.InvariantCulture))));
            foreach (var cls in new[] { "fp-team-halo", $"fp-team-label fp-team-{team.ColorIndex}" })
                layer.Add(new XElement(Svg + "text", new XAttribute("class", cls),
                    new XAttribute("x", Num(x + pad)), new XAttribute("y", Num(y - font * 0.35)),
                    new XAttribute("font-size", Num(font)), team.Name));
        }
        root.Add(layer);
    }

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static double? D(XElement e, string name) =>
        double.TryParse((string?)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private readonly record struct Box(double MinX, double MinY, double MaxX, double MaxY)
    {
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
        public double Cx => (MinX + MaxX) / 2;
        public double Cy => (MinY + MaxY) / 2;
        public Box Union(Box o) => new(Math.Min(MinX, o.MinX), Math.Min(MinY, o.MinY), Math.Max(MaxX, o.MaxX), Math.Max(MaxY, o.MaxY));
    }

    /// <summary>Box of a circle, ellipse or rectangle (a group: its first such descendant). No transforms are applied.</summary>
    private static Box? Bounds(XElement e)
    {
        var shape = e.Name.LocalName is "circle" or "ellipse" or "rect"
            ? e
            : e.Descendants().FirstOrDefault(d => d.Name.LocalName is "circle" or "ellipse" or "rect");
        if (shape is null) return null;
        switch (shape.Name.LocalName)
        {
            case "circle" when D(shape, "cx") is { } cx && D(shape, "cy") is { } cy && D(shape, "r") is { } r:
                return new Box(cx - r, cy - r, cx + r, cy + r);
            case "ellipse" when D(shape, "cx") is { } ex && D(shape, "cy") is { } ey && D(shape, "rx") is { } rx && D(shape, "ry") is { } ry:
                return new Box(ex - rx, ey - ry, ex + rx, ey + ry);
            case "rect" when D(shape, "width") is { } w && D(shape, "height") is { } h:
                var x = D(shape, "x") ?? 0; var y = D(shape, "y") ?? 0;
                return new Box(x, y, x + w, y + h);
            default:
                return null;
        }
    }
}
