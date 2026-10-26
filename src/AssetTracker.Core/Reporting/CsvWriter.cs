using System.Globalization;
using System.Text;

namespace AssetTracker.Core.Reporting;

/// <summary>
/// Excel-compatible, spreadsheet-formula-safe CSV (RFC 4180): comma separator, CRLF row endings, fields quoted
/// when they contain a comma, quote, CR or LF (embedded quotes doubled), UTF-8 with a byte-order mark so Excel
/// detects the encoding.
/// <para>
/// <b>Formula neutralization.</b> Every <see cref="Text"/> cell whose first character could start a formula
/// (<c>= + - @</c>, TAB, CR, or the fullwidth forms <c>＝ ＋ － ＠</c>) is prefixed with a single quote so a
/// spreadsheet shows it as text instead of evaluating it. Values the application formats itself
/// (<see cref="Number"/>, dates, booleans) bypass this, so e.g. a negative stock delta stays numeric.
/// Never pass user-controlled text through <see cref="Number"/>.
/// </para>
/// </summary>
public sealed class CsvWriter
{
    private static readonly char[] FormulaTriggers = ['=', '+', '-', '@', '\t', '\r', '＝', '＋', '－', '＠'];
    private static readonly char[] QuoteTriggers = [',', '"', '\r', '\n'];

    private readonly StringBuilder _sb = new();
    private bool _rowHasCells;

    public int RowCount { get; private set; }

    /// <summary>Writes a header row (headers are text cells too).</summary>
    public CsvWriter Header(params string[] names)
    {
        foreach (var n in names) Text(n);
        return EndRow();
    }

    /// <summary>A text cell: formula-neutralized, then quoted as needed. Null is an empty cell.</summary>
    public CsvWriter Text(string? value) => Raw(value is null ? "" : Neutralize(value));

    /// <summary>A number formatted by the application (invariant culture). Not neutralized.</summary>
    public CsvWriter Number(long value) => Raw(value.ToString(CultureInfo.InvariantCulture));

    public CsvWriter Number(long? value) => value is { } v ? Number(v) : Raw("");

    /// <summary>ISO 8601 calendar date (yyyy-MM-dd).</summary>
    public CsvWriter Date(DateOnly? value) => Raw(value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "");

    /// <summary>UTC timestamp as "yyyy-MM-dd HH:mm:ss" (Excel parses it as a date-time).</summary>
    public CsvWriter Timestamp(DateTimeOffset? value) =>
        Raw(value?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "");

    public CsvWriter Bool(bool value) => Raw(value ? "Yes" : "No");

    public CsvWriter EndRow()
    {
        _sb.Append("\r\n");
        _rowHasCells = false;
        RowCount++;
        return this;
    }

    public override string ToString() => _sb.ToString();

    /// <summary>The document as UTF-8 bytes with a BOM.</summary>
    public byte[] ToUtf8Bytes()
    {
        var bom = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(_sb.ToString());
        var result = new byte[bom.Length + body.Length];
        bom.CopyTo(result, 0);
        body.CopyTo(result, bom.Length);
        return result;
    }

    /// <summary>Prefixes a single quote when <paramref name="value"/> starts with a formula-trigger character.</summary>
    public static string Neutralize(string value) =>
        value.Length > 0 && Array.IndexOf(FormulaTriggers, value[0]) >= 0 ? "'" + value : value;

    /// <summary>RFC 4180 field escaping (no neutralization).</summary>
    public static string Escape(string value) =>
        value.IndexOfAny(QuoteTriggers) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    private CsvWriter Raw(string value)
    {
        if (_rowHasCells) _sb.Append(',');
        _sb.Append(Escape(value));
        _rowHasCells = true;
        return this;
    }
}
