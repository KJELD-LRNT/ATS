using System.Globalization;
using System.Text;

namespace AssetTracker.Infrastructure.Demo;

/// <summary>
/// A fictional floor plan as SVG, in the format of the floor plan renderer the facility team uses (rooms, tables and
/// seats carry <c>data-kind</c>, <c>data-id</c>, <c>data-name</c>, <c>data-type</c> and <c>data-parent</c>), so the demo
/// goes through the same import as a real drawing. It deliberately includes a <c>style</c> element, a <c>style</c>
/// attribute and a pattern fill, which the importer has to rewrite.
/// </summary>
public static class DemoFloorplans
{
    private static string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    public sealed record Room(string Id, string Name, string Type, double X, double Y, double W, double H, int Tables, int SeatsPerTable);

    public static string Build(int floor, string title, IReadOnlyList<Room> rooms)
    {
        const double width = 640, height = 320;
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {N(width)} {N(height)}\" class=\"floorplan\" data-floor=\"{floor}\" role=\"img\" aria-label=\"{title}\">");
        sb.Append("<style>.bg{fill:#f7f6f2}.title{font:600 12px system-ui,Segoe UI,Arial,sans-serif;fill:#26323f}");
        sb.Append(".outline{fill:#ffffff;stroke:#3c4650;stroke-width:1.2}.notfitted{fill:url(#hatch);stroke:none}");
        sb.Append(".room{stroke-width:.8}.room.office{fill:#fdebcf;stroke:#cf9437}.room.meeting{fill:#d9e9fb;stroke:#4f86c6}");
        sb.Append(".room.canteen{fill:#e1f1d9;stroke:#69a74f}.room.support{fill:#ededea;stroke:#c2c2bc}");
        sb.Append(".rname{font:600 7px system-ui,Segoe UI,Arial,sans-serif;fill:#2d3640}.table rect{fill:#ffffff;stroke:#2f3b4a;stroke-width:.8}");
        sb.Append(".seat{fill:#2b6cb0;stroke:#ffffff;stroke-width:.5}</style>");
        sb.Append("<defs><pattern id=\"hatch\" width=\"6\" height=\"6\" patternUnits=\"userSpaceOnUse\" patternTransform=\"rotate(45)\">");
        sb.Append("<rect width=\"6\" height=\"6\" fill=\"#ecebe7\"/><line x1=\"0\" y1=\"0\" x2=\"0\" y2=\"6\" stroke=\"#d3d1cb\" stroke-width=\"2\"/></pattern></defs>");
        sb.Append($"<rect class=\"bg\" width=\"{N(width)}\" height=\"{N(height)}\"/>");
        sb.Append($"<text class=\"title\" x=\"12\" y=\"18\">{title}</text>");
        sb.Append("<polygon class=\"outline\" points=\"12,30 628,30 628,310 12,310\"/>");

        var labels = new StringBuilder();
        sb.Append("<g id=\"rooms\">");
        foreach (var r in rooms)
        {
            sb.Append($"<g class=\"room {r.Type}\" id=\"f{floor}-room-{r.Id}\" data-kind=\"room\" data-id=\"{r.Id}\" data-type=\"{r.Type}\" data-name=\"{r.Name}\">");
            sb.Append($"<title>{r.Id} {r.Name}</title><rect class=\"shape\" x=\"{N(r.X)}\" y=\"{N(r.Y)}\" width=\"{N(r.W)}\" height=\"{N(r.H)}\" rx=\"1\"/></g>");
            labels.Append($"<text class=\"rname\" x=\"{N(r.X + 4)}\" y=\"{N(r.Y + 10)}\" style=\"font-size:7px;pointer-events:none\">{r.Name}</text>");
        }
        sb.Append("</g><g id=\"tables\">");
        foreach (var r in rooms.Where(r => r.Tables > 0))
        {
            var tableW = Math.Min(r.W - 16, r.SeatsPerTable / 2 * 18 + 8);
            var gap = (r.H - 22) / r.Tables;
            for (var t = 1; t <= r.Tables; t++)
            {
                var tid = $"{r.Id}-T{t:00}";
                var tx = r.X + (r.W - tableW) / 2;
                var ty = r.Y + 22 + (t - 1) * gap + gap / 2 - 5;
                sb.Append($"<g class=\"table {r.Type}\" id=\"f{floor}-table-{tid}\" data-kind=\"table\" data-id=\"{tid}\" data-type=\"{r.Type}\" data-parent=\"{r.Id}\" data-name=\"{r.Type}\">");
                sb.Append($"<title>{tid}</title><rect x=\"{N(tx)}\" y=\"{N(ty)}\" width=\"{N(tableW)}\" height=\"10\" rx=\"1.2\"/>");
                var perSide = (r.SeatsPerTable + 1) / 2;
                for (var s = 1; s <= r.SeatsPerTable; s++)
                {
                    var col = (s - 1) % perSide;
                    var top = s <= perSide;
                    var cx = tx + 6 + col * ((tableW - 12) / Math.Max(1, perSide - 1 == 0 ? 1 : perSide - 1));
                    var cy = top ? ty - 4 : ty + 14;
                    sb.Append($"<circle class=\"seat\" id=\"f{floor}-seat-{tid}-S{s:00}\" data-kind=\"seat\" data-id=\"{tid}-S{s:00}\" cx=\"{N(cx)}\" cy=\"{N(cy)}\" r=\"3\"/>");
                }
                sb.Append("</g>");
            }
        }
        sb.Append("</g><g id=\"labels\">").Append(labels).Append("</g></svg>");
        return sb.ToString();
    }

    public static string Amsterdam1() => Build(1, "Amsterdam · Floor 1",
    [
        new("1.01", "Finance", "office", 20, 40, 140, 120, 2, 6),
        new("1.02", "Engineering", "office", 164, 40, 160, 120, 3, 6),
        new("1.03", "Service desk", "office", 328, 40, 140, 120, 2, 6),
        new("1.04", "Sales", "office", 472, 40, 148, 120, 2, 6),
        new("1.05", "Boardroom", "meeting", 20, 170, 150, 130, 1, 12),
        new("1.06", "Canteen", "canteen", 174, 170, 300, 130, 3, 8),
        new("1.07", "Print and storage", "support", 478, 170, 142, 130, 0, 0),
    ]);

    public static string Amsterdam2() => Build(2, "Amsterdam · Floor 2",
    [
        new("2.01", "Open space north", "office", 20, 40, 300, 120, 3, 8),
        new("2.02", "Open space south", "office", 324, 40, 296, 120, 3, 8),
        new("2.03", "Project room", "meeting", 20, 170, 200, 130, 1, 8),
    ]);

    public static string NewYork1() => Build(1, "New York · Floor 1",
    [
        new("1.01", "Operations", "office", 20, 40, 200, 120, 2, 6),
        new("1.02", "Sales", "office", 224, 40, 200, 120, 2, 6),
    ]);
}
