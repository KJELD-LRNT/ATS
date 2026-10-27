using System.Net;
using System.Text.RegularExpressions;
using AssetTracker.Tests.Infrastructure;

namespace AssetTracker.Tests.Foundation;

/// <summary>
/// WP5.2 "Quiet utility" style: the layout links the stylesheet and preloads self-hosted fonts that are actually
/// served, and pages carry no inline <c>style</c> attributes or blocks (the CSP would block them).
/// </summary>
public partial class UiStyleTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public UiStyleTests(AppFactory f) => _f = f;

    [GeneratedRegex("""\sstyle\s*=""", RegexOptions.IgnoreCase)]
    private static partial Regex StyleAttribute();

    [GeneratedRegex("""url\("\.\./fonts/([^"]+)"\)""")]
    private static partial Regex CssFontUrl();

    [Fact]
    public async Task Layout_links_the_stylesheet_and_preloads_self_hosted_fonts()
    {
        var html = await _f.CreateClientAs(TestUser.It).GetStringAsync("/");
        Assert.Contains("href=\"/css/site.css?v=", html);
        Assert.Contains("<link rel=\"preload\" href=\"/fonts/ibm-plex-sans-latin-400-normal.woff2\" as=\"font\" type=\"font/woff2\" crossorigin", html);
        Assert.DoesNotContain("fonts.googleapis.com", html);
        Assert.DoesNotContain("fonts.gstatic.com", html);
        Assert.Contains("class=\"skip-link\" href=\"#main\"", html);
    }

    [Fact]
    public async Task Every_font_face_in_the_stylesheet_is_served_with_its_license()
    {
        var client = _f.CreateClient();
        var css = await client.GetStringAsync("/css/site.css");
        Assert.Contains("font-display: swap", css);
        Assert.DoesNotMatch(new Regex("""url\(\s*["']?(https?:)?//"""), css); // no external resources
        Assert.DoesNotContain("@import", css);

        var fonts = CssFontUrl().Matches(css).Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Equal(6, fonts.Count); // Plex Sans 400/500/600/700 + Plex Mono 400/500
        foreach (var font in fonts)
        {
            var res = await client.GetAsync($"/fonts/{font}");
            Assert.True(res.StatusCode == HttpStatusCode.OK, $"/fonts/{font} returned {(int)res.StatusCode}");
            Assert.Equal("font/woff2", res.Content.Headers.ContentType?.MediaType);
            Assert.True((await res.Content.ReadAsByteArrayAsync()).Length > 1000);
        }

        var license = await client.GetAsync("/fonts/OFL.txt");
        Assert.Equal(HttpStatusCode.OK, license.StatusCode);
        Assert.Contains("SIL OPEN FONT LICENSE", await license.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Csp_allows_self_hosted_fonts_only()
    {
        var res = await _f.CreateClientAs(TestUser.It).GetAsync("/");
        var csp = res.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("font-src 'self'", csp);
        Assert.Contains("style-src 'self'", csp);
    }

    public static TheoryData<string, string> Pages() => new()
    {
        { "it", "/" }, { "it", "/Assets" }, { "it", "/Assets/Create" }, { "it", "/Pools" }, { "it", "/Pools/Create" }, { "it", "/Pools?low=true" }, { "it", "/Loans?inactive=true" },
        { "it", "/Loans" }, { "it", "/Loans/Checkout" }, { "it", "/Reports" }, { "it", "/Reports/Inventory" },
        { "it", "/Reports/Inventory?view=pools" }, { "it", "/Reports/Assignments" }, { "it", "/Reports/Overdue" },
        { "it", "/Reports/History" }, { "it", "/Audit" }, { "it", "/Ops" }, { "it", "/Ops/Mail" }, { "it", "/Admin/Sync" },
        { "manager", "/" }, { "manager", "/Assets" }, { "manager", "/Loans" },
        { "admin", "/" }, { "admin", "/Admin/Sites" }, { "admin", "/Admin/Grants?q=zz" }, { "admin", "/Admin/Settings" },
        { "admin", "/Admin/Sync" }, { "admin", "/Admin/Privacy?q=zz" },
        { "admin", "/Admin/Types" }, { "admin", "/Admin/TypeEdit/4" }, { "admin", "/Admin/TypeEdit/4?fieldId=5" },
        { "admin", "/Admin/Vendors" }, { "admin", "/Admin/Models" }, { "admin", "/Admin/OverviewTiles" },
        { "it", "/Assets/Create?typeId=3" }, { "it", "/Assets/Create?typeId=4" },
    };

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task Pages_have_no_inline_style_attributes_or_blocks(string role, string url)
    {
        var user = role switch { "manager" => TestUser.Manager, "admin" => TestUser.Admin, _ => TestUser.It };
        var res = await _f.CreateClientAs(user).GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        Assert.DoesNotMatch(StyleAttribute(), html);
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("class=\"sidebar\"", html);
    }

    [Theory]
    [InlineData("/Assets", "/Assets")]
    [InlineData("/Assets/Create", "/Assets")]
    [InlineData("/Ops", "/Ops")]
    [InlineData("/Ops/Mail", "/Ops/Mail")]
    [InlineData("/Reports/Overdue", "/Reports")]
    public async Task Sidebar_marks_the_current_section(string url, string activeHref)
    {
        var html = await _f.CreateClientAs(TestUser.It).GetStringAsync(url);
        var current = Regex.Matches(html, "<a class=\"nav-link\" href=\"([^\"]+)\" aria-current=\"page\"")
            .Select(m => m.Groups[1].Value).ToList();
        Assert.Equal([activeHref], current);
    }
}
