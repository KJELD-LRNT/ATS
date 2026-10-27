using System.Text;
using System.Text.RegularExpressions;

namespace AssetTracker.Core.Workplace;

/// <summary>
/// Turns the stylesheet of an imported drawing into one the page can serve safely: only a fixed list of presentation
/// properties, no at-rules, no external references; every rule is scoped to <c>.floorplan</c> and its class names are
/// prefixed (<c>.seat</c> becomes <c>.floorplan .fp-seat</c>), so the drawing cannot restyle the application and the
/// application cannot restyle the drawing. A <c>url(#id)</c> reference (a pattern fill) cannot live in a separate
/// stylesheet, so a rule for a single class with such a value is returned as an attribute for the importer to set on
/// the elements of that class.
/// </summary>
internal static partial class FloorplanCss
{
    public const string ClassPrefix = "fp-";
    public const int MaxLength = 200_000;

    private static readonly HashSet<string> Properties = new(StringComparer.OrdinalIgnoreCase)
    {
        "fill", "stroke", "stroke-width", "stroke-dasharray", "stroke-dashoffset", "stroke-linejoin", "stroke-linecap",
        "opacity", "fill-opacity", "stroke-opacity", "fill-rule", "font", "font-size", "font-family", "font-weight",
        "font-style", "text-anchor", "dominant-baseline", "letter-spacing", "pointer-events", "visibility", "display",
    };

    [GeneratedRegex(@"^[A-Za-z0-9\s.,\-+%#()_'""]*$")]
    private static partial Regex SafeValue();

    [GeneratedRegex(@"url\(\s*#([A-Za-z0-9_.\-]+)\s*\)")]
    private static partial Regex LocalUrl();

    [GeneratedRegex(@"^[A-Za-z0-9_\-\.\s>*]+$")]
    private static partial Regex SafeSelector();

    [GeneratedRegex(@"\.([A-Za-z0-9_\-]+)")]
    private static partial Regex ClassToken();

    [GeneratedRegex(@"^\.([A-Za-z0-9_\-]+)$")]
    private static partial Regex SingleClass();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"@(import|charset|namespace)\b[^;{}]*;", RegexOptions.IgnoreCase)]
    private static partial Regex StatementAtRule();

    /// <summary>A presentation value that is safe in an attribute or a declaration (local <c>url(#id)</c> allowed).</summary>
    public static bool IsSafeValue(string value)
    {
        if (value.Length > 300 || value.Contains('!')) return false;
        var stripped = LocalUrl().Replace(value, "");
        if (stripped.Contains("url(", StringComparison.OrdinalIgnoreCase)) return false;
        return SafeValue().IsMatch(stripped);
    }

    public static bool IsAllowedProperty(string name) => Properties.Contains(name);

    public sealed record Result(string Css, IReadOnlyDictionary<string, List<(string Property, string Value)>> ClassAttributes);

    public static Result Scope(string css)
    {
        var attrs = new Dictionary<string, List<(string, string)>>();
        var sb = new StringBuilder();
        css = Comment().Replace(css, "");
        // Statement at-rules (@import, @charset, @namespace) end with ";" and have no block: drop them first, or they
        // would be read as the start of the next rule's selector.
        css = StatementAtRule().Replace(css, "");
        var pos = 0;
        while (pos < css.Length)
        {
            var open = css.IndexOf('{', pos);
            if (open < 0) break;
            var selectorText = css[pos..open].Trim();
            var close = MatchingBrace(css, open);
            var end = close < 0 ? css.Length : close;
            var body = css[(open + 1)..end];
            pos = close < 0 ? css.Length : close + 1;

            // At-rules (@media, @import, @font-face...) and nested blocks are not carried over.
            if (selectorText.Length == 0 || selectorText.StartsWith('@') || body.Contains('{')) continue;

            var selectors = selectorText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(s => SafeSelector().IsMatch(s) && !s.StartsWith('>')).ToList();
            if (selectors.Count == 0) continue;

            var kept = new List<string>();
            foreach (var decl in body.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = decl.IndexOf(':');
                if (colon <= 0) continue;
                var name = decl[..colon].Trim().ToLowerInvariant();
                var value = decl[(colon + 1)..].Trim();
                if (!Properties.Contains(name) || !IsSafeValue(value)) continue;
                if (LocalUrl().IsMatch(value))
                {
                    // Only a rule for one class can become an attribute; anything else loses the declaration.
                    if (selectors.Count == 1 && SingleClass().Match(selectors[0]) is { Success: true } m)
                    {
                        var key = ClassPrefix + m.Groups[1].Value;
                        if (!attrs.TryGetValue(key, out var list)) attrs[key] = list = [];
                        list.Add((name, value));
                    }
                    continue;
                }
                kept.Add($"{name}:{value}");
            }
            if (kept.Count == 0) continue;

            var scoped = selectors.Select(s => ".floorplan " + ClassToken().Replace(s, m => "." + ClassPrefix + m.Groups[1].Value));
            sb.Append(string.Join(",", scoped)).Append('{').Append(string.Join(";", kept)).Append("}\n");
            if (sb.Length > MaxLength) break;
        }
        return new Result(sb.ToString(), attrs.ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    private static int MatchingBrace(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }
}
