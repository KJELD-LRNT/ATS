using System.Net;
using System.Text.RegularExpressions;

namespace AssetTracker.Tests.Infrastructure;

/// <summary>
/// Posts Razor Pages forms the way a browser does: GET the form page (the client keeps the antiforgery cookie),
/// read the hidden <c>__RequestVerificationToken</c>, then POST it with the form fields.
/// </summary>
public static partial class AntiforgeryHelper
{
    public const string FieldName = "__RequestVerificationToken";

    [GeneratedRegex("""<input[^>]*name="__RequestVerificationToken"[^>]*value="([^"]+)"[^>]*>""")]
    private static partial Regex TokenInput();

    public static async Task<string> GetTokenAsync(HttpClient client, string formUrl)
    {
        var res = await client.GetAsync(formUrl);
        if (res.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException($"GET {formUrl} returned {(int)res.StatusCode}; cannot read antiforgery token.");
        var match = TokenInput().Match(await res.Content.ReadAsStringAsync());
        if (!match.Success) throw new InvalidOperationException($"No antiforgery token found on {formUrl}.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    /// <summary>GETs <paramref name="formUrl"/> for a token, then POSTs the fields to <paramref name="postUrl"/> (defaults to the form URL).</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string formUrl,
        IDictionary<string, string> fields, string? postUrl = null)
    {
        var token = await GetTokenAsync(client, formUrl);
        var body = new Dictionary<string, string>(fields) { [FieldName] = token };
        return await client.PostAsync(postUrl ?? formUrl, new FormUrlEncodedContent(body));
    }
}
