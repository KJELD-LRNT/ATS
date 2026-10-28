using System.Text.Encodings.Web;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AssetTracker.Web.Pages.Shared;

/// <summary>
/// View helpers for the "Quiet utility" UI style (see docs/development.md, "UI style"): status pills that always pair
/// colour with a word, inline SVG icons and the sidebar's active-page logic. Markup only; no behaviour.
/// </summary>
public static class Ui
{
    // ---------------------------------------------------------------- status pills

    /// <summary>A status pill: <c>&lt;span class="pill pill--{modifier}"&gt;text&lt;/span&gt;</c>.</summary>
    public static IHtmlContent Pill(string modifier, string text, string? id = null) =>
        new HtmlString($"<span class=\"pill pill--{modifier}\"{(id is null ? "" : $" id=\"{Enc(id)}\"")}>{Enc(text)}</span>");

    public static string Label(AssetState s) => s switch
    {
        AssetState.CheckedOut => "Checked out",
        _ => s.ToString(),
    };

    public static string Modifier(AssetState s) => s switch
    {
        AssetState.Available => "available",
        AssetState.CheckedOut => "checked-out",
        AssetState.Unavailable => "unavailable",
        _ => "retired",
    };

    public static IHtmlContent Pill(AssetState s) => Pill(Modifier(s), Label(s));

    /// <summary>Overdue pill from whole days past the due date (0 = due today).</summary>
    public static IHtmlContent OverduePill(int daysOverdue) => daysOverdue <= 0
        ? Pill("due-today", "Due today")
        : Pill("overdue", daysOverdue == 1 ? "1 day overdue" : $"{daysOverdue} days overdue");

    public static IHtmlContent Pill(SyncRunStatus s) => s switch
    {
        SyncRunStatus.Succeeded => Pill("available", "Succeeded"),
        SyncRunStatus.Running => Pill("checked-out", "Running"),
        SyncRunStatus.Rejected => Pill("due-today", "Rejected"),
        _ => Pill("overdue", s.ToString()),
    };

    public static IHtmlContent Pill(NotificationStatus s) => s switch
    {
        NotificationStatus.Sent => Pill("available", "Sent"),
        NotificationStatus.Pending => Pill("checked-out", "Pending"),
        NotificationStatus.Failed => Pill("overdue", "Failed"),
        _ => Pill("unavailable", s.ToString()),
    };

    public static string Label(LoanEventType t) => t switch
    {
        LoanEventType.CheckedOut => "Checked out",
        LoanEventType.PartiallyReturned => "Partially returned",
        _ => t.ToString(),
    };

    /// <summary>Timeline marker modifier and icon for a loan event type.</summary>
    public static (string Modifier, string Icon) Marker(LoanEventType t) => t switch
    {
        LoanEventType.CheckedOut => ("checked-out", "arrow-out"),
        LoanEventType.Returned or LoanEventType.PartiallyReturned => ("available", "arrow-in"),
        _ => ("corrected", "pencil"),
    };

    // ---------------------------------------------------------------- stock meter

    /// <summary>
    /// Attributes for a <c>&lt;meter class="meter"&gt;</c> showing available of on-hand units: below 20 % is "low"
    /// (red), up to 40 % "mid" (amber), above that "ok" (accent). With a <paramref name="minimum"/> the thresholds are the
    /// minimum and twice the minimum instead. The browser colours the bar from low/high/optimum.
    /// </summary>
    public static StockMeter Stock(int available, int onHand, int? minimum = null)
    {
        var max = Math.Max(onHand, 1);
        double low = max * 0.2, high = max * 0.4;
        // A pool with a minimum stock level is judged by it: red below the minimum, amber up to twice it.
        if (minimum is { } min and > 0)
        {
            // The bar then shows stock against that target, so a pool under its minimum is red even when most units are in.
            max = Math.Max(max, min * 2);
            low = min;
            high = min * 2.0;
        }
        var level = available < low ? "low" : available <= high ? "mid" : "ok";
        static string N(double d) => d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return new StockMeter(max, N(low), N(high), level);
    }

    public sealed record StockMeter(int Max, string Low, string High, string Level);

    // ---------------------------------------------------------------- people

    /// <summary>Up to two initials for an avatar: "DEMO\manager.ams" → "MA", "Robin Demoberg" → "RD".</summary>
    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var n = name[(name.LastIndexOf('\\') + 1)..];
        var parts = n.Split([' ', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        var s = parts.Length >= 2 ? $"{parts[0][0]}{parts[^1][0]}" : n.Length >= 2 ? n[..2] : n;
        return s.ToUpperInvariant();
    }

    /// <summary>Roles for the sidebar footer: "IT · Admin", or "No access".</summary>
    public static string RolesLabel(AppRoles roles)
    {
        var list = new List<string>();
        if (roles.HasFlag(AppRoles.It)) list.Add("IT");
        if (roles.HasFlag(AppRoles.Manager)) list.Add("Manager");
        if (roles.HasFlag(AppRoles.Admin)) list.Add("Admin");
        if (roles.HasFlag(AppRoles.Facility)) list.Add("Facility");
        return list.Count == 0 ? "No access" : string.Join(" · ", list);
    }

    // ---------------------------------------------------------------- navigation

    /// <summary>
    /// True when the current page is one of <paramref name="pages"/>: an exact page path ("/Ops/Index") or a folder
    /// prefix ending in "/" ("/Assets/" matches every page under Pages/Assets).
    /// </summary>
    public static bool IsCurrent(ViewContext ctx, params string[] pages)
    {
        var current = ctx.RouteData.Values["page"] as string ?? "";
        return pages.Any(p => p.EndsWith('/')
            ? current.StartsWith(p, StringComparison.OrdinalIgnoreCase)
            : string.Equals(current, p, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- icons

    private static readonly Dictionary<string, string> IconPaths = new()
    {
        ["overview"] = "<rect x=\"3\" y=\"3\" width=\"7\" height=\"9\" rx=\"1\"/><rect x=\"14\" y=\"3\" width=\"7\" height=\"5\" rx=\"1\"/><rect x=\"14\" y=\"12\" width=\"7\" height=\"9\" rx=\"1\"/><rect x=\"3\" y=\"16\" width=\"7\" height=\"5\" rx=\"1\"/>",
        ["map"] = "<path d=\"M9 4 3 6.5v14L9 18l6 2.5 6-2.5v-14L15 6.5z\"/><path d=\"M9 4v14M15 6.5v14\"/>",
        ["roles"] = "<circle cx=\"9\" cy=\"8\" r=\"3.5\"/><path d=\"M2.5 20a6.5 6.5 0 0 1 13 0\"/><path d=\"M17 7.5l1.5 1.5L21.5 6\"/>",
        ["assets"] ="<rect x=\"2\" y=\"4\" width=\"20\" height=\"13\" rx=\"2\"/><path d=\"M8 21h8M12 17v4\"/>",
        ["pools"] = "<path d=\"M21 8 12 3 3 8v8l9 5 9-5z\"/><path d=\"m3 8 9 5 9-5M12 13v8\"/>",
        ["loans"] = "<path d=\"M7 17 17 7M8 7h9v9\"/>",
        ["reports"] = "<path d=\"M4 20V10M10 20V4M16 20v-7M22 20H2\"/>",
        ["mail"] = "<rect x=\"3\" y=\"5\" width=\"18\" height=\"14\" rx=\"2\"/><path d=\"m3 7 9 6 9-6\"/>",
        ["ops"] = "<path d=\"M3 12h4l3-8 4 16 3-8h4\"/>",
        ["audit"] = "<path d=\"M14 3H6a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9z\"/><path d=\"M14 3v6h6M8 13h8M8 17h5\"/>",
        ["sync"] = "<path d=\"M21 12a9 9 0 0 1-15.5 6.2L3 16M3 12a9 9 0 0 1 15.5-6.2L21 8\"/><path d=\"M21 3v5h-5M3 21v-5h5\"/>",
        ["sites"] = "<path d=\"M12 21s-7-6.2-7-11.5A7 7 0 0 1 19 9.5C19 14.8 12 21 12 21z\"/><circle cx=\"12\" cy=\"9.5\" r=\"2.5\"/>",
        ["types"] = "<path d=\"m12 3 9 5-9 5-9-5z\"/><path d=\"m3 13 9 5 9-5\"/>",
        ["vendors"] = "<path d=\"M3 21h18M5 21V7l7-4 7 4v14\"/><path d=\"M9 21v-6h6v6\"/>",
        ["models"] = "<rect x=\"4\" y=\"4\" width=\"16\" height=\"16\" rx=\"2\"/><path d=\"M9 9h6v6H9z\"/>",
        ["grants"] ="<circle cx=\"8\" cy=\"15\" r=\"4\"/><path d=\"m10.8 12.2 9.2-9.2M17 6l3 3M14 9l2 2\"/>",
        ["settings"] = "<path d=\"M4 6h10M18 6h2M4 12h4M12 12h8M4 18h12M20 18h0\"/><circle cx=\"16\" cy=\"6\" r=\"2\"/><circle cx=\"10\" cy=\"12\" r=\"2\"/><circle cx=\"18\" cy=\"18\" r=\"2\"/>",
        ["privacy"] = "<path d=\"M12 3 4 6v6c0 4.5 3.4 8.3 8 9 4.6-.7 8-4.5 8-9V6z\"/><path d=\"m9 12 2 2 4-4\"/>",
        ["demo"] = "<path d=\"M9 3h6M10 3v6L4.5 18.5A1.7 1.7 0 0 0 6 21h12a1.7 1.7 0 0 0 1.5-2.5L14 9V3\"/><path d=\"M7 15h10\"/>",
        ["inbox"] = "<path d=\"M22 12h-6l-2 3h-4l-2-3H2\"/><path d=\"M5.5 5h13L22 12v6a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2v-6z\"/>",
        ["tag"] = "<path d=\"M20.6 13.4 13.4 20.6a2 2 0 0 1-2.8 0L3 13V3h10l7.6 7.6a2 2 0 0 1 0 2.8z\"/><circle cx=\"7.5\" cy=\"7.5\" r=\"1.5\"/>",
        ["plus"] = "<path d=\"M12 5v14M5 12h14\"/>",
        ["minus"] = "<path d=\"M5 12h14\"/>",
        ["expand"] = "<path d=\"M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5\"/>",
        ["fit"] = "<path d=\"M9 4H4v5M15 4h5v5M9 20H4v-5M15 20h5v-5\"/><rect x=\"8\" y=\"8\" width=\"8\" height=\"8\" rx=\"1\"/>",
        ["area"] = "<path d=\"M4 7V4h3M10 4h4M17 4h3v3M20 10v4M20 17v3h-3M14 20h-4M7 20H4v-3M4 14v-4\"/>",
        ["search"] = "<circle cx=\"11\" cy=\"11\" r=\"7\"/><path d=\"m20 20-3.5-3.5\"/>",
        ["warning"] = "<path d=\"M12 9v4M12 17h.01\"/><path d=\"M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z\"/>",
        ["error"] = "<circle cx=\"12\" cy=\"12\" r=\"9\"/><path d=\"M12 8v5M12 16h.01\"/>",
        ["check"] = "<path d=\"m5 12 5 5 9-10\"/>",
        ["arrow-out"] = "<path d=\"M7 17 17 7M8 7h9v9\"/>",
        ["arrow-in"] = "<path d=\"M17 7 7 17M16 17H7V8\"/>",
        ["x"] = "<path d=\"M6 6l12 12M18 6 6 18\"/>",
        ["chevron-left"] = "<path d=\"m15 6-6 6 6 6\"/>",
        ["chevron-right"] = "<path d=\"m9 6 6 6-6 6\"/>",
        ["arrow-right"] = "<path d=\"M5 12h14M13 6l6 6-6 6\"/>",
        ["pencil"] = "<path d=\"M12 20h9\"/><path d=\"M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4z\"/>",
        ["download"] = "<path d=\"M12 4v11M7 10l5 5 5-5M5 20h14\"/>",
        ["sun"] = "<circle cx=\"12\" cy=\"12\" r=\"4\"/><path d=\"M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4\"/>",
        ["moon"] = "<path d=\"M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z\"/>",
        ["monitor"] = "<rect x=\"2\" y=\"3\" width=\"20\" height=\"14\" rx=\"2\"/><path d=\"M8 21h8M12 17v4\"/>",
        ["sidebar"] = "<rect x=\"3\" y=\"4\" width=\"18\" height=\"16\" rx=\"2\"/><path d=\"M9 4v16M15 10l-2 2 2 2\"/>",
    };

    /// <summary>An inline, decorative (aria-hidden) stroke icon that inherits <c>currentColor</c>.</summary>
    public static IHtmlContent Icon(string name, int size = 18, double stroke = 1.8) =>
        new HtmlString($"<svg class=\"icon\" width=\"{size}\" height=\"{size}\" viewBox=\"0 0 24 24\" fill=\"none\" " +
                       $"stroke=\"currentColor\" stroke-width=\"{stroke.ToString(System.Globalization.CultureInfo.InvariantCulture)}\" " +
                       "stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\" focusable=\"false\">" +
                       IconPaths[name] + "</svg>");

    private static string Enc(string s) => HtmlEncoder.Default.Encode(s);
}
