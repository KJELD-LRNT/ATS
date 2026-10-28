using System.Net;
using System.Security.Claims;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using AssetTracker.Tests.Infrastructure;
using AssetTracker.Web.Pages.Local;
using AssetTracker.Web.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static AssetTracker.Tests.Security.SuperAdminTestSupport;

namespace AssetTracker.Tests.Security;

/// <summary>
/// The built-in super admin (§2 of docs/plans/system-roles-and-super-admin.md): sign-in and sign-out, rights (exactly
/// Admin), sessions, audit and IT mail. Lockout and rate limits are in <see cref="SuperAdminLockoutTests"/>.
/// </summary>
public class SuperAdminSignInTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public SuperAdminSignInTests(AppFactory f) => _f = f;

    public const string Banner = "Signed in as the super admin (not an AD account).";

    [Fact]
    public async Task Sign_in_lands_on_settings_with_the_banner_audit_and_one_it_mail()
    {
        var account = await ResetAccountAsync(_f);
        var mailsBefore = await SignInMailCountAsync();
        var client = _f.CreateAnonymousClient("10.1.0.1");

        var res = await PostSignInAsync(client, Password);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/Admin/Settings", res.Headers.Location?.OriginalString);
        var cookie = Assert.Single(res.Headers.GetValues("Set-Cookie"), c => c.StartsWith(AppAuthentication.PlainHttpCookieName + "="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase); // a session cookie

        var html = await client.GetStringAsync("/Admin/Settings");
        Assert.Contains(Banner, html);
        Assert.Contains(SuperAdmin.DisplayName, html);

        var audit = await _f.WithDbAsync(db => db.AuditEvents.AsNoTracking()
            .Where(a => a.Action == "superadmin.signin").OrderByDescending(a => a.Id).FirstAsync());
        Assert.Equal(SuperAdmin.DisplayName, audit.Actor);
        Assert.Equal(account.Id.ToString(), audit.EntityId);
        Assert.Contains("10.1.0.1", audit.DetailsJson);
        var stored = await AccountAsync(_f);
        Assert.Equal(("10.1.0.1", _f.Clock.UtcNow), (stored.LastSignInIp, stored.LastSignInAt!.Value));

        // One IT mail per sign-in.
        Assert.Equal(mailsBefore + 1, await SignInMailCountAsync());
        await SignedInClientAsync(_f);
        Assert.Equal(mailsBefore + 2, await SignInMailCountAsync());
        var mail = await _f.WithDbAsync(db => db.Outbox.AsNoTracking().OrderByDescending(o => o.Id).FirstAsync());
        Assert.Equal(NotificationKind.SystemAlert, mail.Kind);
        Assert.Contains("super admin signed in", mail.Subject);
    }

    private Task<int> SignInMailCountAsync() => _f.WithDbAsync(db => db.Outbox.CountAsync(o => o.DedupeKey.StartsWith("superadmin-signin:")));

    public static TheoryData<string> AdminPages =>
    [
        "/", "/Admin/Settings", "/Admin/SystemRoles", "/Admin/Sites", "/Admin/Sync", "/Admin/Privacy", "/Admin/Grants",
        "/Admin/Types", $"/Admin/TypeEdit/{BuiltIn.Laptop}", "/Admin/Vendors", "/Admin/Models", "/Admin/OverviewTiles",
        "/superadmin/account",
    ];

    [Theory]
    [MemberData(nameof(AdminPages))]
    public async Task Super_admin_reaches_every_admin_page_without_a_sid(string url)
    {
        await ResetAccountAsync(_f);
        var client = await SignedInClientAsync(_f);
        var res = await client.GetAsync(url);
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"GET {url}: {(int)res.StatusCode}");
        Assert.Contains(Banner, await res.Content.ReadAsStringAsync());
    }

    /// <summary>Inventory, loans, reports, floor plans, ops and the audit log (D2) are IT/manager/facility pages.</summary>
    public static TheoryData<string> ItPages =>
    [
        "/Assets", "/Assets/Create", "/Pools", "/Loans", "/Loans/Checkout", "/Reports", "/Reports/Overdue", "/Workplace",
        "/Audit", "/Ops", "/Ops/Mail",
    ];

    [Theory]
    [MemberData(nameof(ItPages))]
    public async Task It_pages_are_403_and_not_a_redirect(string url)
    {
        await ResetAccountAsync(_f);
        var client = await SignedInClientAsync(_f);
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(client, url));
    }

    [Fact]
    public async Task Admin_work_is_audited_as_the_super_admin()
    {
        await ResetAccountAsync(_f);
        var client = await SignedInClientAsync(_f);
        var res = await FormHelper.PostFormAsync(client, "/Admin/Sites", "/Admin/Sites?handler=Create",
            ("Input.Key", "SUPA1"), ("Input.Name", "Super admin site"), ("Input.IsActive", "true"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var site = await _f.WithDbAsync(db => db.Sites.AsNoTracking().SingleAsync(s => s.Key == "SUPA1"));
        var audit = await _f.WithDbAsync(db => db.AuditEvents.AsNoTracking()
            .SingleAsync(a => a.EntityType == "Site" && a.EntityId == site.Id.ToString()));
        Assert.Equal(SuperAdmin.DisplayName, audit.Actor);

        // System roles: the super admin gives a role; "granted by" is its name.
        var person = await _f.WithDbAsync(db => Seed.PersonAsync(db, "supa.facility", sid: "S-1-5-21-77-77-77-7701"));
        res = await FormHelper.PostFormAsync(client, "/Admin/SystemRoles?q=supa.facility",
            "/Admin/SystemRoles?handler=Add&q=supa.facility", ("personId", person.Id.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal(SuperAdmin.DisplayName, await _f.WithDbAsync(db => db.RoleAssignments
            .Where(r => r.PrincipalDirectoryId == person.DirectoryId).Select(r => r.GrantedBy).SingleAsync()));
    }

    [Fact]
    public async Task Wrong_password_or_username_is_refused_with_one_generic_message()
    {
        await ResetAccountAsync(_f);
        var client = _f.CreateAnonymousClient();
        foreach (var (user, password) in new[] { (SuperAdmin.UserName, "wrong password, long enough"), ("admin", Password) })
        {
            var res = await PostSignInAsync(client, password, user);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var html = await res.Content.ReadAsStringAsync();
            Assert.Contains(System.Net.WebUtility.HtmlEncode(SuperAdminModel.FailedMessage), html);
            Assert.DoesNotContain(Banner, html);
            Assert.False(res.Headers.TryGetValues("Set-Cookie", out var cookies) &&
                         cookies.Any(c => c.StartsWith(AppAuthentication.PlainHttpCookieName + "=") && !c.Contains("expires=Thu, 01 Jan 1970")));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(client, "/Admin/Settings"));
        var failed = await _f.WithDbAsync(db => db.AuditEvents.AsNoTracking()
            .Where(a => a.Action == "superadmin.signin-failed").OrderByDescending(a => a.Id).Take(2).ToListAsync());
        Assert.All(failed, a => Assert.Equal(SuperAdmin.DisplayName, a.Actor));
        Assert.Equal(2, (await AccountAsync(_f)).FailedCount);
    }

    [Fact]
    public async Task A_disabled_or_missing_account_is_refused_and_the_page_says_so()
    {
        await ResetAccountAsync(_f, enabled: false);
        var client = _f.CreateAnonymousClient();
        Assert.Contains("The super admin account is switched off", await client.GetStringAsync("/superadmin"));
        var res = await PostSignInAsync(client, Password);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(SuperAdminModel.FailedMessage), await res.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(client, "/Admin/Settings"));

        await _f.WithDbAsync(db => db.LocalAccounts.ExecuteDeleteAsync());
        Assert.Contains("The super admin account is switched off", await client.GetStringAsync("/superadmin"));
        Assert.Equal(HttpStatusCode.OK, (await PostSignInAsync(client, Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(client, "/Admin/Settings"));

        await ResetAccountAsync(_f);
        Assert.DoesNotContain("switched off", await client.GetStringAsync("/superadmin"));
    }

    [Fact]
    public async Task A_password_reset_or_disable_ends_open_sessions()
    {
        await ResetAccountAsync(_f);
        var client = await SignedInClientAsync(_f);
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, "/Admin/Settings"));

        await ResetAccountAsync(_f); // the setup command's set-password: new hash and stamp
        var res = await client.GetAsync("/Admin/Settings");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.StartsWith("/superadmin", res.Headers.Location!.AbsolutePath);
        // The stale cookie was removed: this browser is anonymous again (Windows sign-in would apply).
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(client, "/Admin/Settings"));

        client = await SignedInClientAsync(_f);
        await _f.WithDbAsync(db => db.LocalAccounts.ExecuteUpdateAsync(s => s.SetProperty(a => a.IsEnabled, false)));
        Assert.Equal(HttpStatusCode.Redirect, await StatusAsync(client, "/Admin/Settings"));
    }

    [Fact]
    public async Task Thirty_minutes_idle_ends_the_session_but_activity_keeps_it()
    {
        await ResetAccountAsync(_f);
        var active = await SignedInClientAsync(_f);
        var idle = await SignedInClientAsync(_f);
        for (var i = 0; i < 3; i++)
        {
            _f.Clock.Advance(TimeSpan.FromMinutes(20));
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(active, "/Admin/Sites"));
        }
        Assert.Equal(HttpStatusCode.Redirect, await StatusAsync(idle, "/Admin/Sites"));
    }

    [Fact]
    public async Task The_session_ends_eight_hours_after_sign_in_even_with_activity()
    {
        await ResetAccountAsync(_f);
        var client = await SignedInClientAsync(_f);
        for (var minutes = 20; minutes < 8 * 60; minutes += 20)
        {
            _f.Clock.Advance(TimeSpan.FromMinutes(20));
            Assert.True(await StatusAsync(client, "/Admin/Sites") == HttpStatusCode.OK, $"after {minutes} min");
        }
        _f.Clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(HttpStatusCode.Redirect, await StatusAsync(client, "/Admin/Sites"));
    }

    [Fact]
    public async Task Changing_the_password_needs_the_current_one_keeps_this_session_and_ends_others()
    {
        await ResetAccountAsync(_f);
        var me = await SignedInClientAsync(_f);
        var other = await SignedInClientAsync(_f);
        const string next = "a brand new long passphrase";

        async Task<string> ChangeAsync(string current, string password, string confirm)
        {
            var res = await FormHelper.PostFormAsync(me, "/superadmin/account", "/superadmin/account",
                ("CurrentPassword", current), ("NewPassword", password), ("ConfirmPassword", confirm));
            return res.StatusCode == HttpStatusCode.Redirect ? "ok" : await res.Content.ReadAsStringAsync();
        }

        Assert.Contains("The current password is wrong.", await ChangeAsync("not the password at all", next, next));
        Assert.Contains("at least 16 characters", await ChangeAsync(Password, "short", "short"));
        Assert.Contains("published demo password", await ChangeAsync(Password, SuperAdmin.DemoPassword, SuperAdmin.DemoPassword));
        Assert.Contains("do not match", await ChangeAsync(Password, next, next + "x"));
        Assert.Equal("ok", await ChangeAsync(Password, next, next));

        Assert.Contains("Your password is changed", await me.GetStringAsync("/superadmin/account"));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(me, "/Admin/Settings"));
        Assert.Equal(HttpStatusCode.Redirect, await StatusAsync(other, "/Admin/Settings"));
        Assert.Equal(PasswordVerificationResult.Success, SuperAdmin.VerifyPassword((await AccountAsync(_f)).PasswordHash!, next));
        var audit = await _f.WithDbAsync(db => db.AuditEvents.AsNoTracking().SingleAsync(a => a.Action == "superadmin.password-change"));
        Assert.Equal(SuperAdmin.DisplayName, audit.Actor);
        await SignedInClientAsync(_f, next);
    }

    [Fact]
    public async Task Windows_users_cannot_open_the_account_page_or_change_the_password()
    {
        await ResetAccountAsync(_f);
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(_f.CreateClientAs(TestUser.Admin), "/superadmin/account"));
        await Assert.ThrowsAsync<AssetTracker.Core.Common.AccessDeniedException>(() => _f.RunAsAsync(TestUser.Admin,
            sp => sp.GetRequiredService<SuperAdminService>().ChangePasswordAsync(Password, "x long new password here", "x long new password here")));
    }

    [Fact]
    public async Task Sign_out_is_a_post_with_antiforgery_and_is_audited()
    {
        await ResetAccountAsync(_f);
        var client = await SignedInClientAsync(_f, ip: "10.1.0.9");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/superadmin/signout", new FormUrlEncodedContent([]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, "/Admin/Settings"));
        // A GET never signs out.
        Assert.Equal(HttpStatusCode.Redirect, await StatusAsync(client, "/superadmin/signout"));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, "/Admin/Settings"));

        var res = await FormHelper.PostFormAsync(client, "/superadmin/account", "/superadmin/signout");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/superadmin?signedOut=true", res.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(client, "/Admin/Settings"));
        Assert.Contains("You are signed out.", await client.GetStringAsync("/superadmin?signedOut=true"));
        var audit = await _f.WithDbAsync(db => db.AuditEvents.AsNoTracking()
            .Where(a => a.Action == "superadmin.signout").OrderByDescending(a => a.Id).FirstAsync());
        Assert.Equal(SuperAdmin.DisplayName, audit.Actor);
        Assert.Contains("10.1.0.9", audit.DetailsJson);
    }

    [Fact]
    public async Task A_signed_in_super_admin_on_the_sign_in_page_goes_to_the_account_page()
    {
        await ResetAccountAsync(_f);
        var client = await SignedInClientAsync(_f);
        var res = await client.GetAsync("/superadmin");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/superadmin/account", res.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task An_old_hash_format_is_upgraded_on_sign_in()
    {
        await ResetAccountAsync(_f);
        var v2 = new PasswordHasher<LocalAccount>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV2,
        })).HashPassword(new LocalAccount(), Password);
        await _f.WithDbAsync(db => db.LocalAccounts.ExecuteUpdateAsync(s => s.SetProperty(a => a.PasswordHash, v2)));
        await SignedInClientAsync(_f);
        var hash = (await AccountAsync(_f)).PasswordHash!;
        Assert.NotEqual(v2, hash);
        Assert.Equal(0x01, Convert.FromBase64String(hash)[0]);
        Assert.Equal(PasswordVerificationResult.Success, SuperAdmin.VerifyPassword(hash, Password));
    }

    [Fact]
    public async Task Every_page_except_superadmin_and_health_needs_sign_in()
    {
        var anonymous = _f.CreateAnonymousClient();
        var routes = _f.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Select(e => "/" + System.Text.RegularExpressions.Regex.Replace(e.RoutePattern.RawText ?? "", @"\{[^}]+\}", "1").TrimEnd('/'))
            .Distinct().ToList();
        Assert.Contains("/superadmin", routes);
        Assert.Contains("/Admin/Settings", routes);
        // /superadmin/signout is anonymous so an expired cookie can be cleared; a GET only redirects to /superadmin.
        var open = new HashSet<string> { "/superadmin", "/superadmin/signout", "/health", "/health/ready" };
        foreach (var url in routes)
        {
            var status = await StatusAsync(anonymous, url);
            if (open.Contains(url))
                Assert.True(status is HttpStatusCode.OK or HttpStatusCode.Redirect or HttpStatusCode.ServiceUnavailable, $"{url}: {status}");
            else
                Assert.True(status == HttpStatusCode.Unauthorized, $"anonymous GET {url}: {(int)status}");
        }
    }

    [Fact]
    public async Task The_claims_transformation_gives_the_super_admin_exactly_admin_without_a_database_lookup()
    {
        var principal = AppAuthentication.CreatePrincipal("stamp", _f.Clock.UtcNow);
        // Even with group SID claims added, nothing but Admin; a null DbContext proves there is no lookup.
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(ClaimTypes.GroupSid, TestGroups.It));
        var options = _f.Services.GetRequiredService<IOptionsMonitor<RoleGroupOptions>>();
        var result = await new AppRoleClaimsTransformation(options, null!).TransformAsync(principal);
        Assert.Equal([nameof(AppRoles.Admin)], result.FindAll(AppClaimTypes.Role).Select(c => c.Value));
        Assert.Null(result.FindFirst(ClaimTypes.PrimarySid));
        Assert.Equal(SuperAdmin.DisplayName, result.Identity!.Name);
    }

    [Fact]
    public void Production_cookie_is_host_prefixed_secure_httponly_and_strict()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<AssetTracker.Core.Abstractions.IClock>(new FakeClock());
        services.AddAppAuthentication(plainHttp: false);
        using var sp = services.BuildServiceProvider();
        var o = sp.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AppAuthentication.SuperAdminScheme);
        Assert.Equal("__Host-ats-superadmin", o.Cookie.Name);
        Assert.Equal(Microsoft.AspNetCore.Http.CookieSecurePolicy.Always, o.Cookie.SecurePolicy);
        Assert.True(o.Cookie.HttpOnly);
        Assert.Equal(Microsoft.AspNetCore.Http.SameSiteMode.Strict, o.Cookie.SameSite);
        Assert.Equal("/", o.Cookie.Path);
        Assert.Equal("/superadmin", o.LoginPath);
        Assert.Equal((TimeSpan.FromMinutes(30), true), (o.ExpireTimeSpan, o.SlidingExpiration));
    }
}
