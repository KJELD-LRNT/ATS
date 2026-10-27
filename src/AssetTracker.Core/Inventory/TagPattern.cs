using System.Text;
using System.Text.RegularExpressions;
using AssetTracker.Core.Common;

namespace AssetTracker.Core.Inventory;

/// <summary>
/// The tag generator's pattern language, set per asset type by admins. Letters, digits, '-', '_' and '.' are literal;
/// <c>{SITE}</c> becomes the site's tag code and <c>{000}</c> the running number, zero-padded to as many digits as there
/// are zeros. Exactly one number token is required. <c>{SITE}D{000}OMA</c> at a site with code "R" gives R<b>D</b>001OMA,
/// RD002OMA ... (numbers are counted per type and site).
/// </summary>
public static partial class TagPattern
{
    public const int MaxLength = 100;
    public const int SiteCodeMaxLength = 16;

    [GeneratedRegex(@"\{[^}]*\}|[^{}]+|[{}]")]
    private static partial Regex Pieces();

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+$")]
    private static partial Regex Literal();

    [GeneratedRegex(@"^\{0{1,9}\}$")]
    private static partial Regex NumberToken();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,16}$")]
    private static partial Regex SiteCode();

    /// <summary>Trims a pattern; blank = no generator (null). Throws <see cref="DomainException"/> when it is invalid.</summary>
    public static string? Validate(string? pattern)
    {
        var p = pattern?.Trim();
        if (string.IsNullOrEmpty(p)) return null;
        if (p.Length > MaxLength)
            throw new DomainException("pattern.length", $"The tag pattern may be at most {MaxLength} characters.");

        var numbers = 0;
        var worst = 0;
        foreach (Match m in Pieces().Matches(p))
        {
            var piece = m.Value;
            if (piece.Equals("{SITE}", StringComparison.OrdinalIgnoreCase)) worst += SiteCodeMaxLength;
            else if (NumberToken().IsMatch(piece)) { numbers++; worst += piece.Length - 2 + 3; }
            else if (Literal().IsMatch(piece)) worst += piece.Length;
            else
                throw new DomainException("pattern.invalid",
                    $"Unexpected '{piece}' in the tag pattern. Use letters, digits, '-', '_' or '.', {{SITE}} for the site code and {{000}} for the number.");
        }
        if (numbers != 1)
            throw new DomainException("pattern.number",
                "The tag pattern needs exactly one number token such as {000} (the zeros set the minimum number of digits).");
        if (worst > Inventory.AssetService.TagMaxLength)
            throw new DomainException("pattern.too_long", $"Tags from this pattern could exceed {Inventory.AssetService.TagMaxLength} characters.");
        return p;
    }

    public static bool UsesSite(string pattern) => pattern.Contains("{SITE}", StringComparison.OrdinalIgnoreCase);

    /// <summary>Normalizes a site tag code (blank = none). Throws <see cref="DomainException"/> when invalid.</summary>
    public static string? NormalizeSiteCode(string? code)
    {
        var c = code?.Trim();
        if (string.IsNullOrEmpty(c)) return null;
        if (!SiteCode().IsMatch(c))
            throw new DomainException("site.tag_code",
                $"The tag code may have 1-{SiteCodeMaxLength} letters, digits, '-' or '_'.");
        return c.ToUpperInvariant();
    }

    /// <summary>Renders the tag for <paramref name="number"/>; <paramref name="siteCode"/> may be null when the pattern has no {SITE}.</summary>
    public static string Render(string pattern, string? siteCode, int number)
    {
        var sb = new StringBuilder();
        foreach (Match m in Pieces().Matches(pattern))
        {
            var piece = m.Value;
            if (piece.Equals("{SITE}", StringComparison.OrdinalIgnoreCase)) sb.Append(siteCode);
            else if (NumberToken().IsMatch(piece)) sb.Append(number.ToString().PadLeft(piece.Length - 2, '0'));
            else sb.Append(piece);
        }
        return Normalization.Code(sb.ToString());
    }
}
