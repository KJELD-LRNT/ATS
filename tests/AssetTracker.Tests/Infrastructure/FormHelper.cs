using System.Text.RegularExpressions;

namespace AssetTracker.Tests.Infrastructure;

/// <summary>
/// Posts Razor Pages forms the way a browser does: GET the page (which sets the antiforgery cookie), extract the
/// <c>__RequestVerificationToken</c> hidden field, then POST the form with it. Use a client from
/// <see cref="AppFactory.CreateClientAs"/> (cookies are handled by the test client).
/// </summary>
public static partial class FormHelper
{
    public const string TokenField = "__RequestVerificationToken";

    [GeneratedRegex("""<input[^>]*name="__RequestVerificationToken"[^>]*value="([^"]+)"[^>]*>""")]
    private static partial Regex TokenPattern();

    /// <summary>GETs <paramref name="pageUrl"/> and returns the antiforgery token from its first form.</summary>
    public static async Task<string> GetTokenAsync(HttpClient client, string pageUrl)
    {
        var res = await client.GetAsync(pageUrl);
        var html = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"GET {pageUrl} returned {(int)res.StatusCode}: {html}");
        var m = TokenPattern().Match(html);
        if (!m.Success) throw new InvalidOperationException($"No antiforgery token found on {pageUrl}.");
        return m.Groups[1].Value;
    }

    /// <summary>GETs <paramref name="pageUrl"/> for a token, then POSTs <paramref name="fields"/> to <paramref name="postUrl"/>.</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string pageUrl, string postUrl,
        IEnumerable<KeyValuePair<string, string>> fields)
    {
        var token = await GetTokenAsync(client, pageUrl);
        var all = fields.Append(new KeyValuePair<string, string>(TokenField, token));
        return await client.PostAsync(postUrl, new FormUrlEncodedContent(all));
    }

    public static Task<HttpResponseMessage> PostFormAsync(HttpClient client, string pageUrl, string postUrl,
        params (string Name, string Value)[] fields) =>
        PostFormAsync(client, pageUrl, postUrl, fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)));
}
