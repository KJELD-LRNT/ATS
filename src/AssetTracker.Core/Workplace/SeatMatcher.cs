using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AssetTracker.Core.Domain;

namespace AssetTracker.Core.Workplace;

/// <summary>
/// Finds the seats of a floor again in a revised drawing, so their zones, placements and settings carry over even when
/// the drawing is re-made: first by element id, then by the drawing's own seat id (<c>data-id</c>), then by position
/// (the nearest seat within half the usual distance between seats, in the drawing's coordinates or, when the drawing was
/// re-scaled, relative to its viewBox, whichever finds more). An automatic id (<c>fp-e12</c>, given to shapes without one)
/// says nothing across versions, so such a match must also be in the same place.
/// </summary>
internal static partial class SeatMatcher
{
    [GeneratedRegex(@"^fp-e\d+$")]
    private static partial Regex AutoId();

    private static readonly HashSet<string> Shapes = new(StringComparer.Ordinal) { "rect", "circle", "ellipse", "path", "polygon", "polyline", "g" };

    /// <param name="ElementId">The element in the new drawing.</param>
    /// <param name="Parsed">The declared seat there (null for a shape of a plain drawing).</param>
    /// <param name="Spot">The seat it is, or null for a new seat.</param>
    /// <param name="Moved">Found by seat id or position rather than its old element id.</param>
    public sealed record Pair(string ElementId, ParsedSpot? Parsed, Workspot? Spot, bool Moved);

    private sealed record Point(double X, double Y, double Nx, double Ny);

    /// <summary>
    /// Pairs the seats of the new drawing with the floor's workspots. Declared seats (<paramref name="declared"/>) that
    /// match nothing are new; for workspots marked by hand on a plain drawing any shape with an id can be their match.
    /// Workspots that are not in the result were not found.
    /// </summary>
    public static List<Pair> Match(IReadOnlyCollection<Workspot> existing, string oldSvg, string oldViewBox,
        IReadOnlyList<ParsedSpot> declared, string newSvg, string newViewBox)
    {
        var oldRoot = XElement.Parse(oldSvg);
        var newRoot = XElement.Parse(newSvg);
        var oldEls = IdIndex(oldRoot);
        var newEls = IdIndex(newRoot);
        var oldPos = existing.ToDictionary(w => w.Id, w => oldEls.TryGetValue(w.ElementId, out var e) ? Locate(e, oldViewBox) : null);
        var wasDeclared = existing.ToDictionary(w => w.Id, w => oldEls.TryGetValue(w.ElementId, out var e) ? e.Attribute("data-kind") is not null : w.RoomId is not null || w.TableId is not null);

        // Candidates in the new drawing: its declared seats, plus (for seats marked by hand) any shape with an id.
        var seats = declared.ToDictionary(s => s.ElementId, StringComparer.Ordinal);
        var newPos = new Dictionary<string, Point?>(StringComparer.Ordinal);
        Point? NewPos(string id) => newPos.TryGetValue(id, out var p) ? p
            : newPos[id] = newEls.TryGetValue(id, out var e) ? Locate(e, newViewBox) : null;

        var taken = new HashSet<string>(StringComparer.Ordinal);
        var pairs = new List<Pair>();
        var open = existing.ToList();
        var threshold = Threshold(declared.Count >= 2 ? declared.Select(s => NewPos(s.ElementId)) : existing.Select(w => oldPos[w.Id]), Size(oldViewBox));

        bool Fits(Workspot w, string id) =>
            wasDeclared[w.Id] ? seats.ContainsKey(id) : newEls.TryGetValue(id, out var e) && Shapes.Contains(e.Name.LocalName) && e.Attribute("data-kind") is null;

        void Take(Workspot w, string id, bool moved)
        {
            pairs.Add(new Pair(id, seats.GetValueOrDefault(id), w, moved));
            taken.Add(id);
            open.Remove(w);
        }

        // 1. The same element id (an automatic id only when it is still in about the same place).
        foreach (var w in open.ToList())
        {
            if (taken.Contains(w.ElementId) || !Fits(w, w.ElementId)) continue;
            if (AutoId().IsMatch(w.ElementId) && !Near(oldPos[w.Id], NewPos(w.ElementId), threshold)) continue;
            Take(w, w.ElementId, false);
        }

        // 2. The drawing's own seat id (a re-made drawing with other element ids).
        var bySeatId = declared.Where(s => !taken.Contains(s.ElementId)).GroupBy(s => s.Label).ToDictionary(g => g.Key, g => g.First().ElementId);
        foreach (var w in open.ToList())
            if (wasDeclared[w.Id] && bySeatId.TryGetValue(w.Label, out var id) && !taken.Contains(id))
                Take(w, id, true);

        // 3. Position: the nearest free candidate, in drawing units or relative to the viewBox, whichever finds more.
        var candidates = declared.Count > 0
            ? declared.Select(s => s.ElementId).Where(id => !taken.Contains(id)).ToList()
            : newEls.Where(kv => Shapes.Contains(kv.Value.Name.LocalName) && kv.Value.Attribute("data-kind") is null && !taken.Contains(kv.Key))
                .Select(kv => kv.Key).ToList();
        var raw = Nearest(open, candidates, oldPos, NewPos, threshold, relative: false, Fits);
        var rel = Nearest(open, candidates, oldPos, NewPos, threshold, relative: true, Fits);
        foreach (var (w, id) in rel.Count > raw.Count ? rel : raw) Take(w, id, true);

        // Declared seats that matched nothing are new.
        foreach (var s in declared)
            if (!taken.Contains(s.ElementId)) pairs.Add(new Pair(s.ElementId, s, null, false));
        return pairs;
    }

    private static List<(Workspot, string)> Nearest(List<Workspot> open, List<string> candidates, Dictionary<int, Point?> oldPos,
        Func<string, Point?> newPos, (double Raw, double Rel) threshold, bool relative, Func<Workspot, string, bool> fits)
    {
        var options = new List<(double Distance, Workspot Spot, string Id)>();
        foreach (var w in open)
        {
            if (oldPos[w.Id] is not { } a) continue;
            foreach (var id in candidates)
            {
                if (newPos(id) is not { } b || !fits(w, id)) continue;
                var d = relative ? Dist(a.Nx - b.Nx, a.Ny - b.Ny) : Dist(a.X - b.X, a.Y - b.Y);
                if (d <= (relative ? threshold.Rel : threshold.Raw)) options.Add((d, w, id));
            }
        }
        var used = new HashSet<string>(StringComparer.Ordinal);
        var done = new HashSet<int>();
        var result = new List<(Workspot, string)>();
        foreach (var (_, w, id) in options.OrderBy(o => o.Distance))
            if (!done.Contains(w.Id) && used.Add(id)) { done.Add(w.Id); result.Add((w, id)); }
        return result;
    }

    /// <summary>
    /// Half the median distance between neighbouring seats (in drawing units and relative to the viewBox); with fewer than
    /// two seats, 2% of the drawing's size.
    /// </summary>
    private static (double Raw, double Rel) Threshold(IEnumerable<Point?> points, double viewSize)
    {
        var p = points.Where(x => x is not null).Select(x => x!).Take(1500).ToList();
        if (p.Count < 2) return (viewSize * 0.02, 0.02);
        double Median(Func<Point, Point, double> dist)
        {
            var near = p.Select((a, i) => p.Where((_, j) => j != i).Min(b => dist(a, b))).Order().ToList();
            return near[near.Count / 2];
        }
        return (Median((a, b) => Dist(a.X - b.X, a.Y - b.Y)) / 2, Median((a, b) => Dist(a.Nx - b.Nx, a.Ny - b.Ny)) / 2);
    }

    private static bool Near(Point? a, Point? b, (double Raw, double Rel) threshold) =>
        a is null || b is null || Dist(a.X - b.X, a.Y - b.Y) <= threshold.Raw || Dist(a.Nx - b.Nx, a.Ny - b.Ny) <= threshold.Rel;

    private static double Dist(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy);

    private static double[] View(string viewBox) =>
        viewBox.Split(' ').Select(s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0).ToArray();

    private static double Size(string viewBox) => View(viewBox) is { Length: 4 } v ? Math.Max(Math.Max(v[2], v[3]), 1e-9) : 1;

    private static Point? Locate(XElement e, string viewBox)
    {
        if (SvgGeometry.Center(e) is not { } c) return null;
        var v = View(viewBox);
        var size = Size(viewBox);
        return new Point(c.X, c.Y, v.Length == 4 ? (c.X - v[0]) / size : c.X, v.Length == 4 ? (c.Y - v[1]) / size : c.Y);
    }

    private static Dictionary<string, XElement> IdIndex(XElement root) =>
        root.DescendantsAndSelf().Where(e => e.Attribute("id") is not null)
            .GroupBy(e => (string)e.Attribute("id")!).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
}
