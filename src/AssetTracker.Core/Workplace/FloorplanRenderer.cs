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
/// map is styled and scripted with (free desk, taken, not a desk, switched off, flagged, zone colour); a seat with
/// someone or something placed gets a marker: a person icon in the seat, small desktop and screen icons just outside
/// it (away from its table) and a label with the person's name and the desktop's tag, which the page scales to a fixed
/// size on screen. Zones get a dashed outline per table and their name per room (a table can be split between zones).
/// Nothing assumes a particular drawing: positions come from <see cref="SvgGeometry"/> (any shape, any transform), and
/// sizes (markers, outlines, zone names) follow the size of the drawing's own seats. Only data of this application goes
/// in; the drawing itself was sanitised on import.
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

    public static string Render(Floor floor, IReadOnlyCollection<Workspot> spots, IReadOnlyCollection<FloorZone> zones,
        IReadOnlyDictionary<int, SpotState>? states = null, Area? viewBox = null, bool clipToViewBox = false)
    {
        var root = XElement.Parse(floor.Svg);
        var byId = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var e in root.DescendantsAndSelf())
            if ((string?)e.Attribute("id") is { } id) byId.TryAdd(id, e);

        var zoneById = zones.ToDictionary(t => t.Id);
        var geometry = new Geometry(root);
        // Zone outlines are drawn per table (or per room for seats without a table), so a table split between two zones
        // shows both halves, and a zone spread over several tables does not get one box over everything in between.
        // Side: where all its seats sit relative to their table (null when they differ), so the name can go on that side
        // rather than across the table.
        var zoneBounds = new Dictionary<(int Zone, string Group, string Room), (SvgBox Box, Side? Side)>();
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
            if (spot.RoomId is not null) el.SetAttributeValue("data-room", spot.RoomId);
            if (spot.RoomName is not null) el.SetAttributeValue("data-room-name", spot.RoomName);
            if (spot.TableId is not null) el.SetAttributeValue("data-table", spot.TableId);
            if (state?.Person is not null) el.SetAttributeValue("data-person", state.Person);
            if (state?.Desktop is not null) el.SetAttributeValue("data-desktop", state.Desktop);
            if (spot.IsDesk) el.SetAttributeValue("data-desk", "1");

            var box = geometry.Seat(el);
            FloorZone? zone = null;
            if (spot.IsActive && spot.ZoneId is { } zoneId && zoneById.TryGetValue(zoneId, out zone))
            {
                classes.Add($"zc-{zone.ColorIndex}");
                el.SetAttributeValue("data-zone", zoneId.ToString(CultureInfo.InvariantCulture));
                if (box is { } b)
                {
                    var key = (zoneId, spot.TableId ?? spot.RoomId ?? "", spot.RoomId ?? "");
                    var side = geometry.Direction(el, spot.TableId, b);
                    zoneBounds[key] = zoneBounds.TryGetValue(key, out var cur) ? (cur.Box.Union(b), cur.Side == side ? side : null) : (b, side);
                }
            }

            // Tooltip: who sits there (or what kind of seat it is), what is placed, and the zone and room.
            var tip = state?.Person is { } who ? who + (state.PersonLeft ? " (no longer works here)" : "")
                : !spot.IsActive ? "Switched off" : !spot.IsDesk ? "Seat, not a desk" : "Free desk";
            if (state?.Desktop is not null) tip += " · desktop " + state.Desktop;
            if (state?.Screens > 0) tip += $" · {state.Screens} screen{(state.Screens == 1 ? "" : "s")}";
            if (spot.Place(zone?.Name) is { Length: > 0 } place) tip += " · " + place;
            if (el.Element(Svg + "title") is { } title) title.Value = tip;
            else el.AddFirst(new XElement(Svg + "title", tip));

            var existing = (string?)el.Attribute("class");
            el.SetAttributeValue("class", string.IsNullOrEmpty(existing) ? string.Join(' ', classes) : existing + " " + string.Join(' ', classes));

            if (state is not null && box is { } seat)
                AddMarker(markers, labels, spot, state, seat, geometry.Direction(el, spot.TableId, seat));
        }

        AddZoneLayer(root, geometry.Unit(spots.Where(s => s.IsActive).Select(s => s.ElementId), floor), zoneById, zoneBounds);
        root.Add(markers, labels);
        // The box around the rooms, tables and seats (not the paper margins of the drawing): the map fits this.
        if (ContentOf(root, spots) is { } content) root.SetAttributeValue("data-content", content.ToString());
        root.AddFirst(new XElement(Svg + "defs", XElement.Parse("<x xmlns=\"http://www.w3.org/2000/svg\">" + Icons + "</x>").Elements()));
        if (viewBox is { } vb)
        {
            root.SetAttributeValue("viewBox", vb.ToString());
            // A printed area whose shape differs from the paper is letterboxed; without a clip the drawing around the
            // area would show in that space, with people who are not in the printed list.
            if (clipToViewBox) ClipTo(root, vb);
        }
        return root.ToString(SaveOptions.DisableFormatting);
    }

    private static void ClipTo(XElement root, Area area)
    {
        var defs = root.Element(Svg + "defs")!;
        var f = CultureInfo.InvariantCulture;
        defs.Add(new XElement(Svg + "clipPath", new XAttribute("id", "fp-print-area"),
            new XElement(Svg + "rect", new XAttribute("x", area.X.ToString("0.###", f)), new XAttribute("y", area.Y.ToString("0.###", f)),
                new XAttribute("width", area.Width.ToString("0.###", f)), new XAttribute("height", area.Height.ToString("0.###", f)))));
        var content = root.Nodes().Where(n => n != defs).ToList();
        foreach (var n in content) n.Remove();
        root.Add(new XElement(Svg + "g", new XAttribute("class", "fp-print-area"), new XAttribute("clip-path", "url(#fp-print-area)"), content));
    }

    /// <summary>
    /// The box around what matters in the drawing (its rooms, tables and seats), with a small margin, in drawing units;
    /// null when the drawing has none of them. The map's <em>Fit</em> and a printed whole floor use it instead of the
    /// drawing's viewBox, which usually has empty paper around the plan.
    /// </summary>
    public static Area? Content(Floor floor, IReadOnlyCollection<Workspot> spots) => ContentOf(XElement.Parse(floor.Svg), spots);

    private static Area? ContentOf(XElement root, IReadOnlyCollection<Workspot> spots)
    {
        var seatIds = new HashSet<string>(spots.Select(s => s.ElementId), StringComparer.Ordinal);
        SvgBox? box = null;
        foreach (var e in root.DescendantsAndSelf())
        {
            if (e.Name.LocalName is "defs" or "symbol" || e.Ancestors().Any(a => a.Name.LocalName is "defs" or "symbol")) continue;
            var kind = (string?)e.Attribute("data-kind");
            var isPart = kind is "room" or "table" or "seat" || ((string?)e.Attribute("id") is { } id && seatIds.Contains(id));
            if (!isPart || SvgGeometry.Bounds(e) is not { } b || b.Width <= 0 && b.Height <= 0) continue;
            box = box is { } a ? a.Union(b) : b;
        }
        if (box is not { } c) return null;
        var pad = Math.Max(c.Width, c.Height) * 0.025;
        return new Area(c.MinX - pad, c.MinY - pad, c.Width + 2 * pad, c.Height + 2 * pad);
    }

    /// <summary>The centre of each seat (for picking the seats of a printed area).</summary>
    public static IReadOnlyDictionary<int, (double X, double Y)> Centers(Floor floor, IReadOnlyCollection<Workspot> spots)
    {
        var root = XElement.Parse(floor.Svg);
        var byId = root.DescendantsAndSelf().Where(e => e.Attribute("id") is not null)
            .GroupBy(e => (string)e.Attribute("id")!).ToDictionary(g => g.Key, g => g.First());
        var result = new Dictionary<int, (double, double)>();
        foreach (var spot in spots)
            if (byId.TryGetValue(spot.ElementId, out var el) && SvgGeometry.Bounds(el) is { } b)
                result[spot.Id] = (b.Cx, b.Cy);
        return result;
    }

    private enum Side { Up, Down, Left, Right }

    /// <summary>Measurements of one drawing, cached: seat boxes, table boxes and the typical seat size.</summary>
    private sealed class Geometry(XElement root)
    {
        private readonly Dictionary<XElement, SvgBox?> _seats = [];
        private readonly Dictionary<XElement, SvgBox?> _tables = [];
        private Dictionary<string, XElement>? _tablesById;
        private Dictionary<string, XElement>? _byElementId;

        public SvgBox? Seat(XElement seat)
        {
            if (!_seats.TryGetValue(seat, out var box)) _seats[seat] = box = SvgGeometry.Bounds(seat);
            return box;
        }

        /// <summary>
        /// Which way is "away from the table" for a seat (its badges and label go there). The table is the seat's
        /// <c>data-kind="table"</c> ancestor or the table its id names; its box is what is drawn for the table itself, or
        /// else the middle of its seats. Below when the seat has no table.
        /// </summary>
        public Side Direction(XElement seat, string? tableId, SvgBox box)
        {
            var table = seat.Ancestors().FirstOrDefault(a => (string?)a.Attribute("data-kind") == "table");
            if (table is null && tableId is not null)
            {
                _tablesById ??= root.Descendants().Where(e => (string?)e.Attribute("data-kind") == "table" && e.Attribute("data-id") is not null)
                    .GroupBy(e => (string)e.Attribute("data-id")!).ToDictionary(g => g.Key, g => g.First());
                _tablesById.TryGetValue(tableId, out table);
            }
            if (table is null || Table(table) is not { } t) return Side.Down;
            var dx = (box.Cx - t.Cx) / Math.Max(t.Width / 2, 0.001);
            var dy = (box.Cy - t.Cy) / Math.Max(t.Height / 2, 0.001);
            if (Math.Abs(dy) >= Math.Abs(dx)) return dy < 0 ? Side.Up : Side.Down;
            return dx < 0 ? Side.Left : Side.Right;
        }

        private SvgBox? Table(XElement table)
        {
            if (_tables.TryGetValue(table, out var box)) return box;
            box = SvgGeometry.Bounds(table, e => (string?)e.Attribute("data-kind") == "seat");
            if (box is { } own && (own.Width > 0 || own.Height > 0)) return _tables[table] = box;
            // Nothing drawn for the table itself (only its seats, maybe elsewhere): the middle of its seats.
            var tableId = (string?)table.Attribute("data-id");
            SvgBox? seats = null;
            foreach (var s in root.Descendants().Where(e => (string?)e.Attribute("data-kind") == "seat" &&
                         (e.Ancestors().Contains(table) || (tableId is not null && (string?)e.Attribute("data-parent") == tableId))))
                if (Seat(s) is { } b) seats = seats is { } a ? a.Union(b) : b;
            return _tables[table] = seats;
        }

        /// <summary>
        /// The typical seat size (median of the shorter side of the seats), so outlines and zone names fit the drawing
        /// whatever its scale; a part of the drawing's width when it has no measurable seats.
        /// </summary>
        public double Unit(IEnumerable<string> elementIds, Floor floor)
        {
            _byElementId ??= root.DescendantsAndSelf().Where(e => e.Attribute("id") is not null)
                .GroupBy(e => (string)e.Attribute("id")!).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var sizes = elementIds.Select(id => _byElementId.TryGetValue(id, out var el) ? Seat(el) : null)
                .Where(b => b is not null).Select(b => Math.Min(b!.Value.Width, b.Value.Height)).Where(v => v > 0).Order().ToList();
            if (sizes.Count > 0) return sizes[sizes.Count / 2];
            var parts = floor.ViewBox.Split(' ');
            return parts.Length == 4 && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w / 200 : 5;
        }
    }

    private static void AddMarker(XElement markers, XElement labels, Workspot spot, SpotState state, SvgBox seat, Side side)
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

    /// <summary>
    /// A dashed outline in the zone's colour around each of its tables (or groups of seats in a room), and the zone's name
    /// once per room at its top table there: above it, or below or beside it when those seats are all on that side of their
    /// table, so the name does not run across the table.
    /// </summary>
    /// <param name="unit">The typical seat size: the outline keeps about half a seat of room, the name is a bit larger
    /// than a seat.</param>
    private static void AddZoneLayer(XElement root, double unit, Dictionary<int, FloorZone> zones,
        Dictionary<(int Zone, string Group, string Room), (SvgBox Box, Side? Side)> bounds)
    {
        if (bounds.Count == 0) return;
        var pad = unit * 0.6;
        var font = unit * 1.3;
        var layer = new XElement(Svg + "g", new XAttribute("class", "zone-layer"), new XAttribute("pointer-events", "none"));
        foreach (var (key, (b, _)) in bounds.OrderBy(kv => kv.Key.Zone).ThenBy(kv => kv.Value.Box.MinY).ThenBy(kv => kv.Value.Box.MinX))
        {
            var zone = zones[key.Zone];
            layer.Add(new XElement(Svg + "rect",
                new XAttribute("class", $"zone-outline zc-{zone.ColorIndex}"),
                new XAttribute("x", Num(b.MinX - pad)), new XAttribute("y", Num(b.MinY - pad)),
                new XAttribute("width", Num(b.Width + 2 * pad)), new XAttribute("height", Num(b.Height + 2 * pad)),
                new XAttribute("rx", Num(pad)), new XAttribute("fill", "none"), new XAttribute("data-zone", key.Zone.ToString(CultureInfo.InvariantCulture))));
        }
        foreach (var room in bounds.GroupBy(kv => (kv.Key.Zone, kv.Key.Room)).OrderBy(g => g.Key.Zone))
        {
            var zone = zones[room.Key.Zone];
            var (top, side) = room.Select(kv => kv.Value).OrderBy(v => v.Box.MinY).ThenBy(v => v.Box.MinX).First();
            var y = side == Side.Down ? top.MaxY + pad + font : top.MinY - pad - font * 0.35;
            // Seats left of their table: the name ends at their right edge and runs away from the table.
            var (x, anchor) = side == Side.Left ? (top.MaxX + pad, "end") : (top.MinX - pad, "start");
            foreach (var cls in new[] { "zone-halo", $"zone-label zc-{zone.ColorIndex}" })
                layer.Add(new XElement(Svg + "text", new XAttribute("class", cls),
                    new XAttribute("x", Num(x)), new XAttribute("y", Num(y)), new XAttribute("text-anchor", anchor),
                    new XAttribute("font-size", Num(font)), new XAttribute("data-zone", room.Key.Zone.ToString(CultureInfo.InvariantCulture)), zone.Name));
        }
        root.Add(layer);
    }

    private static string Num(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

}
