using System.Net;
using AssetTracker.Core.Admin;
using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Admin;

public class AdminPagesTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public AdminPagesTests(AppFactory f) => _f = f;

    /// <summary>Admin-only pages. <c>/Admin/Sync</c> is readable by IT too (D7, see the sync history tests below).</summary>
    public static TheoryData<string> AdminPages => ["/Admin/Sites", "/Admin/Grants", "/Admin/Settings"];

    [Theory]
    [MemberData(nameof(AdminPages))]
    public async Task Admin_pages_are_forbidden_for_it_manager_and_plain_users(string url)
    {
        foreach (var user in new[] { TestUser.It, TestUser.Manager, TestUser.Nobody })
        {
            var res = await _f.CreateClientAs(user).GetAsync(url);
            Assert.True(res.StatusCode == HttpStatusCode.Forbidden, $"{user.Name} GET {url}: {res.StatusCode}");
        }
        Assert.Equal(HttpStatusCode.OK, (await _f.CreateClientAs(TestUser.Admin).GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Admin_posts_are_forbidden_for_non_admins_even_with_a_valid_form()
    {
        var it = _f.CreateClientAs(TestUser.It);
        var res = await it.PostAsync("/Admin/Settings",
            new FormUrlEncodedContent([new("ItMailbox", "attacker@example.test")]));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.False(await _f.WithDbAsync(db => db.Settings.AnyAsync(s => s.Value == "attacker@example.test")));
    }

    [Fact]
    public async Task Admin_services_reject_non_admin_callers()
    {
        foreach (var user in new[] { TestUser.It, TestUser.Manager })
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() =>
                _f.RunAsAsync(user, sp => sp.GetRequiredService<AdminService>().SearchPeopleAsync("user")));
            await Assert.ThrowsAsync<AccessDeniedException>(() =>
                _f.RunAsAsync(user, sp => sp.GetRequiredService<AdminService>().CreateSiteAsync("NOPE", "Nope", null, true)));
            await Assert.ThrowsAsync<AccessDeniedException>(() =>
                _f.RunAsAsync(user, sp => sp.GetRequiredService<AdminService>().RunSyncNowAsync()));
        }
    }

    [Fact]
    public async Task Admin_can_create_and_edit_a_site_with_audit()
    {
        var c = _f.CreateClientAs(TestUser.Admin);
        var res = await FormHelper.PostFormAsync(c, "/Admin/Sites", "/Admin/Sites?handler=Create",
            ("Input.Key", "ADM1"), ("Input.Name", "Admin Site One"), ("Input.DirectoryValue", "Admin One"),
            ("Input.IsActive", "true"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var site = await _f.WithDbAsync(db => db.Sites.AsNoTracking().SingleAsync(s => s.Key == "ADM1"));
        Assert.Equal("Admin One", site.DirectoryValue);

        // Duplicate key is a conflict.
        res = await FormHelper.PostFormAsync(c, "/Admin/Sites", "/Admin/Sites?handler=Create",
            ("Input.Key", "adm1"), ("Input.Name", "Dup"), ("Input.IsActive", "true"));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("already exists", await res.Content.ReadAsStringAsync());

        res = await FormHelper.PostFormAsync(c, $"/Admin/Sites?editId={site.Id}", $"/Admin/Sites?handler=Update&id={site.Id}",
            ("Input.Name", "Admin Site Renamed"), ("Input.DirectoryValue", ""), ("Input.IsActive", "false"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        site = await _f.WithDbAsync(db => db.Sites.AsNoTracking().SingleAsync(s => s.Id == site.Id));
        Assert.Equal(("ADM1", "Admin Site Renamed", (string?)null, false), (site.Key, site.Name, site.DirectoryValue, site.IsActive));

        var audits = await _f.WithDbAsync(db => db.AuditEvents.Where(a => a.EntityType == "Site" && a.EntityId == site.Id.ToString())
            .OrderBy(a => a.Id).ToListAsync());
        Assert.Equal(["site.create", "site.update"], audits.Select(a => a.Action));
        Assert.All(audits, a => Assert.Equal(TestUser.Admin.Name, a.Actor));
    }

    [Fact]
    public async Task Directory_value_must_be_unique_across_sites()
    {
        await _f.WithDbAsync(db => Seed.SiteAsync(db, "ADM2", "Shared Value"));
        var res = await FormHelper.PostFormAsync(_f.CreateClientAs(TestUser.Admin), "/Admin/Sites", "/Admin/Sites?handler=Create",
            ("Input.Key", "ADM3"), ("Input.Name", "Three"), ("Input.DirectoryValue", "shared value"), ("Input.IsActive", "true"));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Admin_grants_and_removes_site_access_and_removal_revokes_manager_access()
    {
        var mgr = new TestUser(@"CORP\grant.mgr", "S-1-5-21-1000-1000-1000-2101", TestGroups.Manager);
        var (site, person) = await _f.WithDbAsync(async db =>
        {
            var s = await Seed.SiteAsync(db, "GRT1");
            var p = await Seed.PersonAsync(db, "grant.mgr", sid: mgr.Sid);
            return (s, p);
        });
        var c = _f.CreateClientAs(TestUser.Admin);

        var search = await (await c.GetAsync("/Admin/Grants?q=GRANT.M")).Content.ReadAsStringAsync();
        Assert.Contains("grant.mgr", search);

        var res = await FormHelper.PostFormAsync(c, "/Admin/Grants?q=grant.mgr", "/Admin/Grants?handler=Add",
            ("personId", person.Id.ToString()), ("siteId", site.Id.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var grant = await _f.WithDbAsync(db => db.ManagerSiteGrants.AsNoTracking().SingleAsync(g => g.SiteId == site.Id));
        Assert.Equal(person.DirectoryId, grant.PrincipalDirectoryId);
        Assert.Equal(TestUser.Admin.Name, grant.GrantedBy);

        var access = await _f.RunAsAsync(mgr, sp => sp.GetRequiredService<AccessScope>().GetAsync());
        Assert.True(access.CanRead(site.Id));

        // Same grant twice is a conflict.
        res = await FormHelper.PostFormAsync(c, "/Admin/Grants?q=grant.mgr", "/Admin/Grants?handler=Add",
            ("personId", person.Id.ToString()), ("siteId", site.Id.ToString()));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);

        res = await FormHelper.PostFormAsync(c, "/Admin/Grants", $"/Admin/Grants?handler=Remove&grantId={grant.Id}");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.False(await _f.WithDbAsync(db => db.ManagerSiteGrants.AnyAsync(g => g.Id == grant.Id)));

        access = await _f.RunAsAsync(mgr, sp => sp.GetRequiredService<AccessScope>().GetAsync());
        Assert.False(access.CanRead(site.Id));

        var actions = await _f.WithDbAsync(db => db.AuditEvents
            .Where(a => a.EntityType == "ManagerSiteGrant" && a.EntityId == grant.Id.ToString())
            .OrderBy(a => a.Id).Select(a => a.Action).ToListAsync());
        Assert.Equal(["grant.add", "grant.remove"], actions);
    }

    [Fact]
    public async Task Inactive_person_cannot_be_granted_a_site()
    {
        var (site, person) = await _f.WithDbAsync(async db =>
            (await Seed.SiteAsync(db, "GRT2"), await Seed.PersonAsync(db, "grant.inactive", active: false)));
        await Assert.ThrowsAsync<DomainException>(() => _f.RunAsAsync(TestUser.Admin,
            sp => sp.GetRequiredService<AdminService>().AddGrantAsync(person.Id, site.Id)));
    }

    [Fact]
    public async Task People_search_escapes_like_wildcards()
    {
        await _f.WithDbAsync(db => Seed.PersonAsync(db, "wild_card.person"));
        var hits = await _f.RunAsAsync(TestUser.Admin, sp => sp.GetRequiredService<AdminService>().SearchPeopleAsync("%"));
        Assert.Empty(hits); // single character query is ignored
        hits = await _f.RunAsAsync(TestUser.Admin, sp => sp.GetRequiredService<AdminService>().SearchPeopleAsync("d_c"));
        Assert.Contains(hits, p => p.Username == "wild_card.person");
        hits = await _f.RunAsAsync(TestUser.Admin, sp => sp.GetRequiredService<AdminService>().SearchPeopleAsync("d%c"));
        Assert.DoesNotContain(hits, p => p.Username == "wild_card.person");
    }

    [Fact]
    public async Task Admin_sets_it_mailbox_with_validation_and_audit()
    {
        var c = _f.CreateClientAs(TestUser.Admin);
        var res = await FormHelper.PostFormAsync(c, "/Admin/Settings", "/Admin/Settings", ("ItMailbox", "not an email"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        res = await FormHelper.PostFormAsync(c, "/Admin/Settings", "/Admin/Settings", ("ItMailbox", "Desk <it@example.test>"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        res = await FormHelper.PostFormAsync(c, "/Admin/Settings", "/Admin/Settings", ("ItMailbox", " it-desk@example.test "));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var setting = await _f.WithDbAsync(db => db.Settings.AsNoTracking().SingleAsync(s => s.Key == AppSetting.ItMailbox));
        Assert.Equal("it-desk@example.test", setting.Value);
        Assert.Equal(TestUser.Admin.Name, setting.UpdatedBy);
        Assert.Contains("it-desk@example.test", await (await c.GetAsync("/Admin/Settings")).Content.ReadAsStringAsync());

        res = await FormHelper.PostFormAsync(c, "/Admin/Settings", "/Admin/Settings", ("ItMailbox", "it-list@example.test"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var audits = await _f.WithDbAsync(db => db.AuditEvents.Where(a => a.Action == "setting.update").OrderBy(a => a.Id).ToListAsync());
        Assert.Equal(2, audits.Count);
        Assert.Contains("\"before\":\"it-desk@example.test\"", audits[1].DetailsJson);
    }

    [Fact]
    public async Task Admin_can_run_sync_now_and_see_history()
    {
        _f.Directory.Entries = []; // empty result: run is recorded but rejected, nothing deactivated
        var c = _f.CreateClientAs(TestUser.Admin);
        var res = await FormHelper.PostFormAsync(c, "/Admin/Sync", "/Admin/Sync?handler=Run");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var run = await _f.WithDbAsync(db => db.SyncRuns.AsNoTracking().OrderByDescending(r => r.Id).FirstAsync());
        Assert.Equal(TestUser.Admin.Name, run.TriggeredBy);
        Assert.Equal(SyncRunStatus.Rejected, run.Status);

        var html = await (await c.GetAsync("/Admin/Sync")).Content.ReadAsStringAsync();
        Assert.Contains("Rejected", html);
        Assert.Contains("low-count", html);
        Assert.Contains("Run sync now", html);
    }

    /// <summary>Sponsor decision D7: IT can view sync history and issues read-only; only admins can run a sync.</summary>
    [Fact]
    public async Task D7_it_can_view_sync_history_read_only_but_only_admins_can_run_a_sync()
    {
        _f.Directory.Entries = [];
        await _f.RunAsAsync(TestUser.Admin, sp => sp.GetRequiredService<AdminService>().RunSyncNowAsync());
        var runs = await _f.WithDbAsync(db => db.SyncRuns.CountAsync());

        var it = _f.CreateClientAs(TestUser.It);
        var res = await it.GetAsync("/Admin/Sync");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("low-count", html);
        Assert.Contains("Read-only view", html);
        Assert.DoesNotContain("Run sync now", html);
        Assert.Contains("href=\"/Admin/Sync\"", await (await it.GetAsync("/Ops")).Content.ReadAsStringAsync());

        // A crafted POST with a valid antiforgery token is still forbidden for IT, and runs nothing.
        var token = await FormHelper.GetTokenAsync(it, "/Pools/Create");
        res = await it.PostAsync("/Admin/Sync?handler=Run",
            new FormUrlEncodedContent([new(FormHelper.TokenField, token)]));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal(runs, await _f.WithDbAsync(db => db.SyncRuns.CountAsync()));
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<AdminService>().RunSyncNowAsync()));

        // The service read allows IT or admin only.
        var page = await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<AdminService>().ListSyncRunsAsync(1));
        Assert.Equal(runs, page.TotalCount);
        foreach (var user in new[] { TestUser.Manager, TestUser.Nobody })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await _f.CreateClientAs(user).GetAsync("/Admin/Sync")).StatusCode);
            await Assert.ThrowsAsync<AccessDeniedException>(() =>
                _f.RunAsAsync(user, sp => sp.GetRequiredService<AdminService>().ListSyncRunsAsync(1)));
        }
    }

    [Fact]
    public async Task Admin_sets_a_site_timezone_validated_and_audited()
    {
        var c = _f.CreateClientAs(TestUser.Admin);
        var form = await (await c.GetAsync("/Admin/Sites")).Content.ReadAsStringAsync();
        Assert.Contains("<datalist id=\"timezones\">", form);
        Assert.Contains("UTC", form); // the default timezone label (Clock:TimeZoneId in tests)

        var res = await FormHelper.PostFormAsync(c, "/Admin/Sites", "/Admin/Sites?handler=Create",
            ("Input.Key", "TZ1"), ("Input.Name", "Timezone Site"), ("Input.TimeZoneId", " Europe/Amsterdam "),
            ("Input.IsActive", "true"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var site = await _f.WithDbAsync(db => db.Sites.AsNoTracking().SingleAsync(s => s.Key == "TZ1"));
        Assert.Equal("Europe/Amsterdam", site.TimeZoneId);

        // Unknown id: rejected, nothing changed.
        res = await FormHelper.PostFormAsync(c, $"/Admin/Sites?editId={site.Id}", $"/Admin/Sites?handler=Update&id={site.Id}",
            ("Input.Name", "Timezone Site"), ("Input.TimeZoneId", "Mars/Olympus_Mons"), ("Input.IsActive", "true"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("not a known timezone", await res.Content.ReadAsStringAsync());
        Assert.Equal("Europe/Amsterdam", (await _f.WithDbAsync(db => db.Sites.AsNoTracking().SingleAsync(s => s.Id == site.Id))).TimeZoneId);

        // Windows ids are accepted too; empty means "use the default timezone".
        await _f.RunAsAsync(TestUser.Admin, sp => sp.GetRequiredService<AdminService>()
            .UpdateSiteAsync(site.Id, "Timezone Site", null, true, "W. Europe Standard Time"));
        Assert.Equal("W. Europe Standard Time", (await _f.WithDbAsync(db => db.Sites.AsNoTracking().SingleAsync(s => s.Id == site.Id))).TimeZoneId);
        res = await FormHelper.PostFormAsync(c, $"/Admin/Sites?editId={site.Id}", $"/Admin/Sites?handler=Update&id={site.Id}",
            ("Input.Name", "Timezone Site"), ("Input.TimeZoneId", ""), ("Input.IsActive", "true"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Null((await _f.WithDbAsync(db => db.Sites.AsNoTracking().SingleAsync(s => s.Id == site.Id))).TimeZoneId);

        var audits = await _f.WithDbAsync(db => db.AuditEvents.Where(a => a.EntityType == "Site" && a.EntityId == site.Id.ToString())
            .OrderBy(a => a.Id).Select(a => a.DetailsJson).ToListAsync());
        Assert.Equal(3, audits.Count);
        Assert.Contains("\"TimeZoneId\":\"Europe/Amsterdam\"", audits[0]);
        Assert.Contains("\"TimeZoneId\":\"W. Europe Standard Time\"", audits[1]);
        Assert.Contains("\"TimeZoneId\":null", audits[2]);
    }
}
