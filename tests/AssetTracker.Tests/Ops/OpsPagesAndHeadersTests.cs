using System.Net;
using AssetTracker.Core.Ops;
using AssetTracker.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Ops;

public class OpsPagesAndHeadersTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public OpsPagesAndHeadersTests(AppFactory f) => _f = f;

    [Fact]
    public async Task It_sees_the_ops_dashboard()
    {
        await _f.RunAsAsync(TestUser.Nobody, sp => sp.GetRequiredService<OpsStatusService>().RecordHeartbeatAsync("worker-1.2.3"));

        var res = await _f.CreateClientAs(TestUser.It).GetAsync("/Ops");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("id=\"app-version\"", html);
        Assert.Contains("Reachable", html);
        Assert.Contains("worker-1.2.3", html);
        Assert.Contains("id=\"outbox-pending\"", html);
        Assert.Contains("No directory sync", html); // banner: no successful sync in this database
        Assert.Contains("href=\"/Ops/Mail\"", html);
    }

    [Theory]
    [InlineData("Manager")]
    [InlineData("Admin")]
    [InlineData("Nobody")]
    public async Task Non_it_users_are_forbidden(string userKey)
    {
        var user = userKey switch { "Manager" => TestUser.Manager, "Admin" => TestUser.Admin, _ => TestUser.Nobody };
        Assert.Equal(HttpStatusCode.Forbidden, (await _f.CreateClientAs(user).GetAsync("/Ops")).StatusCode);
    }

    [Fact]
    public async Task Ops_nav_link_is_shown_to_it_only()
    {
        Assert.Contains("href=\"/Ops\"", await _f.CreateClientAs(TestUser.It).GetStringAsync("/"));
        Assert.DoesNotContain("href=\"/Ops\"", await _f.CreateClientAs(TestUser.Manager).GetStringAsync("/"));
    }

    [Fact]
    public async Task Pages_carry_security_headers_and_are_not_cached()
    {
        var res = await _f.CreateClientAs(TestUser.It).GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", res.Headers.GetValues("Referrer-Policy").Single());
        var csp = res.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("script-src 'self'", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.True(res.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Pages_have_no_inline_scripts_or_handlers_that_the_csp_would_block()
    {
        var html = await _f.CreateClientAs(TestUser.It).GetStringAsync("/");
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("onsubmit=", html);
        Assert.Contains("/js/site.js", html);
    }

    [Fact]
    public async Task Static_files_get_security_headers_but_stay_cacheable()
    {
        var res = await _f.CreateClient().GetAsync("/css/site.css");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.NotEqual(true, res.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Csv_export_is_not_cached()
    {
        var res = await _f.CreateClientAs(TestUser.It).GetAsync("/Reports/Inventory?handler=Export");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.StartsWith("text/csv", res.Content.Headers.ContentType?.ToString());
        Assert.True(res.Headers.CacheControl?.NoStore);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Antiforgery_cookie_is_http_only_and_same_site_strict()
    {
        var res = await _f.CreateClientAs(TestUser.Admin).GetAsync("/Admin/Settings");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var cookie = res.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }
}
