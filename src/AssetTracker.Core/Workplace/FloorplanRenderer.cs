using System.Globalization;
using System.Xml.Linq;
using AssetTracker.Core.Domain;

namespace AssetTracker.Core.Workplace;

/// <summary>
/// Puts the current state on a stored (already sanitised) drawing: every seat gets its workspot id and the classes the
/// map is styled and scripted with, and every team gets a coloured outline and label around its seats. Only data of
/// this application goes in; the drawing itself is never trusted again at this point, it was sanitised on import.
/// </summary>
public static class FloorplanRenderer
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    public static string Render(Floor floor, IReadOnlyCollection<Workspot> spots, IReadOnlyCollection<FloorTeam> teams,
        IReadOnlyDictionary<int, SpotOccupancy>? occupancy = null)
    {
        var root = XElement.Parse(floor.Svg);
        var byId = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var e in root.DescendantsAndSelf())
            if ((string?)e.Attribute("id") is { } id) byId.TryAdd(id, e);

        var teamById = teams.ToDictionary(t => t.Id);
        var bounds = new Dictionary<int, (double MinX, double MinY, double MaxX, double MaxY)>();

        foreach (var spot in spots)
        {
            if (!byId.TryGetValue(spot.ElementId, out var el)) continue;
            var classes = new List<string> { "fp-ws" };
            if (el.Attribute("data-kind") is null) classes.Add("fp-ws-manual");
            if (!spot.IsActive) classes.Add("fp-ws-off");
            el.SetAttributeValue("data-ws", spot.Id.ToString(CultureInfo.InvariantCulture));
            el.SetAttributeValue("data-label", spot.Label);
            if (spot.RoomId is not null) el.SetAttributeValue("data-room", spot.RoomId);
            if (spot.RoomName is not null) el.SetAttributeValue("data-room-name", spot.RoomName);
            if (spot.TableId is not null) el.SetAttributeValue("data-table", spot.TableId);
            if (spot.IsActive && spot.TeamId is { } teamId && teamById.TryGetValue(teamId, out var team))
            {
                classes.Add($"fp-team-{team.ColorIndex}");
                el.SetAttributeValue("data-team", teamId.ToString(CultureInfo.InvariantCulture));
                if (Bounds(el) is { } b)
                    bounds[teamId] = bounds.TryGetValue(teamId, out var cur)
                        ? (Math.Min(cur.MinX, b.MinX), Math.Min(cur.MinY, b.MinY), Math.Max(cur.MaxX, b.MaxX), Math.Max(cur.MaxY, b.MaxY))
                        : b;
            }
            // Who sits here and what is placed: classes for the occupancy view and a tooltip with the names.
            var tip = spot.Label;
            if (occupancy is not null && occupancy.TryGetValue(spot.Id, out var occ))
            {
                if (occ.Person is not null)
                {
                    classes.Add(occ.PersonLeft ? "fp-occ fp-left" : "fp-occ");
                    el.SetAttributeValue("data-person", occ.Person);
                    tip += " · " + occ.Person + (occ.PersonLeft ? " (left)" : "");
                }
                if (occ.Items > 0)
                {
                    classes.Add("fp-itm");
                    tip += $" · {occ.Items} item{(occ.Items == 1 ? "" : "s")}";
                }
            }
            if (el.Element(Svg + "title") is { } title) title.Value = tip;
            else el.AddFirst(new XElement(Svg + "title", tip));
            var existing = (string?)el.Attribute("class");
            el.SetAttributeValue("class", string.IsNullOrEmpty(existing) ? string.Join(' ', classes) : existing + " " + string.Join(' ', classes));
        }

        AddTeamLayer(root, floor, teamById, bounds);
        return root.ToString(SaveOptions.DisableFormatting);
    }

    private static void AddTeamLayer(XElement root, Floor floor, Dictionary<int, FloorTeam> teams,
        Dictionary<int, (double MinX, double MinY, double MaxX, double MaxY)> bounds)
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
                new XAttribute("width", Num(b.MaxX - b.MinX + 2 * pad)), new XAttribute("height", Num(b.MaxY - b.MinY + 2 * pad)),
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

    /// <summary>Box of a circle, ellipse or rectangle (a group: its first such descendant). No transforms are applied.</summary>
    private static (double MinX, double MinY, double MaxX, double MaxY)? Bounds(XElement e)
    {
        var shape = e.Name.LocalName is "circle" or "ellipse" or "rect"
            ? e
            : e.Descendants().FirstOrDefault(d => d.Name.LocalName is "circle" or "ellipse" or "rect");
        if (shape is null) return null;
        switch (shape.Name.LocalName)
        {
            case "circle" when D(shape, "cx") is { } cx && D(shape, "cy") is { } cy && D(shape, "r") is { } r:
                return (cx - r, cy - r, cx + r, cy + r);
            case "ellipse" when D(shape, "cx") is { } ex && D(shape, "cy") is { } ey && D(shape, "rx") is { } rx && D(shape, "ry") is { } ry:
                return (ex - rx, ey - ry, ex + rx, ey + ry);
            case "rect" when D(shape, "width") is { } w && D(shape, "height") is { } h:
                var x = D(shape, "x") ?? 0; var y = D(shape, "y") ?? 0;
                return (x, y, x + w, y + h);
            default:
                return null;
        }
    }
}
