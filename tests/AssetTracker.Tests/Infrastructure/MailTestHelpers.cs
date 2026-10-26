using System.Net;
using System.Text.RegularExpressions;

namespace AssetTracker.Tests.Infrastructure;

/// <summary>Form POST helpers for the mail ops page tests (real antiforgery round trip).</summary>
public static partial class MailTestHelpers
{
    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();

    /// <summary>Reads the antiforgery token from a rendered page. The client keeps the matching cookie.</summary>
    public static async Task<string> GetAntiforgeryTokenAsync(HttpClient client, string pageUrl)
    {
        var res = await client.GetAsync(pageUrl);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var match = TokenRegex().Match(await res.Content.ReadAsStringAsync());
        Assert.True(match.Success, "No antiforgery token found on " + pageUrl);
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    public static Task<HttpResponseMessage> PostFormAsync(HttpClient client, string url, string? token,
        params (string Name, string Value)[] fields)
    {
        var all = fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)).ToList();
        if (token is not null) all.Add(new("__RequestVerificationToken", token));
        return client.PostAsync(url, new FormUrlEncodedContent(all));
    }
}
