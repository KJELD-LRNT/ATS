using System.Net;
using AssetTracker.Core.Admin;
using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using AssetTracker.Tests.Directory;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static AssetTracker.Tests.Security.SuperAdminTestSupport;

namespace AssetTracker.Tests.Admin;

/// <summary>Admin → System roles: every role and the manager sites, one row per person (plan §1).</summary>
public class SystemRolesTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public SystemRolesTests(AppFactory f) => _f = f;

    private const string Page = "/Admin/SystemRoles";
    private static int _n;

    private static TestUser NewUser(string name)
    {
        var n = Interlocked.Increment(ref _n);
        return new TestUser($@"CORP\{name}", $"S-1-5-21-1000-1000-1000-88{n:00}");
    }

    private Task<Person> PersonAsync(TestUser user, bool active = true, bool withSid = true, Site? site = null) =>
        _f.WithDbAsync(db => Seed.PersonAsync(db, user.Username, site, active: active, sid: withSid ? user.Sid : null));

    private Task<RoleChange> SaveAsync(TestUser actor, string directoryId, AppRoles roles, params int[] sites) =>
        _f.RunAsAsync(actor, sp => sp.GetRequiredService<RoleAssignmentService>().SaveAsync(directoryId, roles, sites));

    private Task<AppRoles> RolesOfAsync(string directoryId) => _f.WithDbAsync(async db =>
        (await db.RoleAssignments.Where(r => r.PrincipalDirectoryId == directoryId).Select(r => r.Role).ToListAsync())
        .Aggregate(AppRoles.None, (a, r) => a | r));

    private static Task<HttpResponseMessage> PostSaveAsync(HttpClient c, string directoryId, IEnumerable<string> roles,
        IEnumerable<int>? sites = null, string query = "") =>
        FormHelper.PostFormAsync(c, Page + query, Page + "?handler=Save" + (query.Length > 0 ? "&" + query[1..] : ""),
            new[] { new KeyValuePair<string, string>("directoryId", directoryId) }
                .Concat(roles.Select(r => new KeyValuePair<string, string>("roles", r)))
                .Concat((sites ?? []).Select(s => new KeyValuePair<string, string>("siteIds", s.ToString()))));

    [Fact]
    public async Task Assigns_and_removes_each_role_through_the_page_with_audit_rows()
    {
        var user = NewUser("sr.each");
        var person = await PersonAsync(user);
        var admin = _f.CreateClientAs(TestUser.Admin);

        foreach (var role in RoleAssignmentService.Assignable)
        {
            var res = await PostSaveAsync(admin, person.DirectoryId, [role.ToString()]);
            Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
            Assert.Equal(role, await RolesOfAsync(person.DirectoryId));
        }
        var all = await PostSaveAsync(admin, person.DirectoryId, ["It", "Manager", "Admin", "Facility"]);
        Assert.Equal(HttpStatusCode.Redirect, all.StatusCode);
        Assert.Equal(AppRoles.It | AppRoles.Manager | AppRoles.Admin | AppRoles.Facility, await RolesOfAsync(person.DirectoryId));
        var html = await admin.GetStringAsync(Page);
        Assert.Contains("sr.each", html);
        Assert.Contains("Gave IT, Manager and Admin", html); // Facility was already there from the loop

        var none = await PostSaveAsync(admin, person.DirectoryId, []);
        Assert.Equal(HttpStatusCode.Redirect, none.StatusCode);
        Assert.Equal(AppRoles.None, await RolesOfAsync(person.DirectoryId));
        await admin.GetStringAsync(Page); // shows (and so clears) the message, which names the person
        Assert.DoesNotContain("sr.each", await admin.GetStringAsync(Page)); // no role, no site: no row

        var audit = await _f.WithDbAsync(db => db.AuditEvents.AsNoTracking()
            .Where(a => a.EntityType == nameof(RoleAssignment) && a.DetailsJson!.Contains(person.DirectoryId))
            .Select(a => new { a.Action, a.Actor, a.DetailsJson }).ToListAsync());
        foreach (var role in RoleAssignmentService.Assignable)
        {
            Assert.Contains(audit, a => a.Action == "role.assign" && a.DetailsJson!.Contains($"\"Role\":\"{role}\""));
            Assert.Contains(audit, a => a.Action == "role.remove" && a.DetailsJson!.Contains($"\"Role\":\"{role}\""));
        }
        Assert.All(audit, a => Assert.Equal(TestUser.Admin.Name, a.Actor));
        // 4 assigns + 3 removes in the loop, 3 assigns, 4 removes.
        Assert.Equal(7, audit.Count(a => a.Action == "role.assign"));
        Assert.Equal(7, audit.Count(a => a.Action == "role.remove"));
    }

    [Fact]
    public async Task Only_admins_open_and_save_the_page()
    {
        var person = await PersonAsync(NewUser("sr.only"));
        foreach (var user in new[] { TestUser.It, TestUser.Manager, TestUser.Facility, TestUser.Nobody })
        {
            var c = _f.CreateClientAs(user);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync(Page)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/Admin/Grants")).StatusCode);
            await Assert.ThrowsAsync<AccessDeniedException>(() => SaveAsync(user, person.DirectoryId, AppRoles.It));
            await Assert.ThrowsAsync<AccessDeniedException>(() =>
                _f.RunAsAsync(user, sp => sp.GetRequiredService<RoleAssignmentService>().PeopleAsync(new RoleFilter())));
        }
        Assert.Equal(AppRoles.None, await RolesOfAsync(person.DirectoryId));
    }

    [Fact]
    public async Task An_admin_cannot_remove_their_own_admin_role()
    {
        var me = NewUser("sr.me") with { Roles = AppRoles.Admin | AppRoles.It };
        await _f.EnsureUserAsync(me);
        var person = await _f.WithDbAsync(db => db.People.AsNoTracking().SingleAsync(p => p.Sid == me.Sid));

        var ex = await Assert.ThrowsAsync<DomainException>(() => SaveAsync(me, person.DirectoryId, AppRoles.It));
        Assert.Equal("You can't remove your own Admin role. Ask another admin to do it.", ex.Message);
        var row = await _f.WithDbAsync(db => db.RoleAssignments.SingleAsync(r => r.PrincipalDirectoryId == person.DirectoryId && r.Role == AppRoles.Admin));
        await Assert.ThrowsAsync<DomainException>(() =>
            _f.RunAsAsync(me, sp => sp.GetRequiredService<RoleAssignmentService>().RemoveAsync(row.Id)));

        // The page: the box is greyed out (a hidden field keeps it), and a forged post is refused with the message.
        var c = _f.CreateClientAs(me);
        var html = await c.GetStringAsync(Page);
        Assert.Contains("(you can't remove your own Admin role)", html);
        var res = await PostSaveAsync(c, person.DirectoryId, ["It"]);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("You can&#x27;t remove your own Admin role.", await res.Content.ReadAsStringAsync());
        Assert.Equal(AppRoles.Admin | AppRoles.It, await RolesOfAsync(person.DirectoryId));

        // Other roles of their own can go; another admin may remove their Admin role.
        Assert.Equal(AppRoles.It, (await SaveAsync(me, person.DirectoryId, AppRoles.Admin)).Removed);
        Assert.Equal(AppRoles.Admin, (await SaveAsync(TestUser.Admin, person.DirectoryId, AppRoles.None)).Removed);
    }

    [Fact]
    public async Task A_role_needs_an_active_person_with_a_windows_account_a_grant_only_an_active_person()
    {
        var site = await _f.WithDbAsync(db => Seed.SiteAsync(db, "SRG1"));
        var noSid = await PersonAsync(NewUser("sr.nosid"), withSid: false);
        var inactive = await PersonAsync(NewUser("sr.inactive"), active: false);

        var ex = await Assert.ThrowsAsync<DomainException>(() => SaveAsync(TestUser.Admin, noSid.DirectoryId, AppRoles.Manager));
        Assert.Contains("no Windows account", ex.Message);
        ex = await Assert.ThrowsAsync<DomainException>(() => SaveAsync(TestUser.Admin, inactive.DirectoryId, AppRoles.It));
        Assert.Contains("inactive", ex.Message);
        await Assert.ThrowsAsync<DomainException>(() => SaveAsync(TestUser.Admin, inactive.DirectoryId, AppRoles.None, site.Id));
        await Assert.ThrowsAsync<DomainException>(() => SaveAsync(TestUser.Admin, "no-such-directory-id", AppRoles.It));

        // A site grant alone needs no Windows account.
        var change = await SaveAsync(TestUser.Admin, noSid.DirectoryId, AppRoles.None, site.Id);
        Assert.Equal(["SRG1"], change.SitesAdded);
        Assert.True(await _f.WithDbAsync(db => db.ManagerSiteGrants.AnyAsync(g => g.PrincipalDirectoryId == noSid.DirectoryId && g.SiteId == site.Id)));

        // The page shows why and greys out the role boxes, but not the site boxes.
        var html = await _f.CreateClientAs(TestUser.Admin).GetStringAsync(Page + "?q=sr.nosid");
        Assert.Contains("No Windows account", html);
        Assert.Contains("No effect without the Manager role.", html);
    }

    [Fact]
    public async Task Inactive_people_keep_their_roles_which_can_still_be_removed()
    {
        var user = NewUser("sr.left");
        var person = await PersonAsync(user);
        await SaveAsync(TestUser.Admin, person.DirectoryId, AppRoles.It | AppRoles.Facility);
        await _f.WithDbAsync(async db =>
        {
            (await db.People.SingleAsync(p => p.Id == person.Id)).IsActive = false;
            await db.SaveChangesAsync();
        });

        var admin = _f.CreateClientAs(TestUser.Admin);
        Assert.DoesNotContain("sr.left", await admin.GetStringAsync(Page));
        var html = await admin.GetStringAsync(Page + "?inactive=true");
        Assert.Contains("sr.left", html);
        Assert.Contains("Inactive", html);

        var change = await SaveAsync(TestUser.Admin, person.DirectoryId, AppRoles.It);
        Assert.Equal(AppRoles.Facility, change.Removed);
        // adding while inactive is refused
        await Assert.ThrowsAsync<DomainException>(() => SaveAsync(TestUser.Admin, person.DirectoryId, AppRoles.It | AppRoles.Admin));
    }

    [Fact]
    public async Task Manager_sites_are_on_the_same_row_with_grant_audit_and_access()
    {
        var (a, b) = await _f.WithDbAsync(async db => (await Seed.SiteAsync(db, "SRS1"), await Seed.SiteAsync(db, "SRS2")));
        var mgr = NewUser("sr.mgr");
        var person = await PersonAsync(mgr);
        var admin = _f.CreateClientAs(TestUser.Admin);

        var res = await PostSaveAsync(admin, person.DirectoryId, ["Manager"], [a.Id, b.Id]);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var access = await _f.RunAsAsync(mgr, sp => sp.GetRequiredService<AccessScope>().GetAsync());
        Assert.True(access.CanRead(a.Id) && access.CanRead(b.Id));
        Assert.Contains("Manager; added site SRS1, SRS2", System.Net.WebUtility.HtmlDecode(await admin.GetStringAsync(Page)));

        res = await PostSaveAsync(admin, person.DirectoryId, ["Manager"], [b.Id]);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        access = await _f.RunAsAsync(mgr, sp => sp.GetRequiredService<AccessScope>().GetAsync());
        Assert.False(access.CanRead(a.Id));
        Assert.True(access.CanRead(b.Id));

        var actions = await _f.WithDbAsync(db => db.AuditEvents.AsNoTracking()
            .Where(e => e.EntityType == nameof(ManagerSiteGrant) && e.DetailsJson!.Contains(person.DirectoryId))
            .OrderBy(e => e.Id).Select(e => new { e.Action, e.SiteId }).ToListAsync());
        Assert.Equal(["grant.add", "grant.add", "grant.remove"], actions.Select(x => x.Action));
        Assert.Equal(a.Id, actions[^1].SiteId);

        // Old links to Admin → Grants land on System roles.
        var old = await admin.GetAsync("/Admin/Grants");
        Assert.Equal(HttpStatusCode.Redirect, old.StatusCode);
        Assert.Equal(Page, old.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Filters_by_role_and_site()
    {
        var site = await _f.WithDbAsync(db => Seed.SiteAsync(db, "SRF1"));
        var it = await PersonAsync(NewUser("sr.filter.it"));
        var mgr = await PersonAsync(NewUser("sr.filter.mgr"));
        var based = await PersonAsync(NewUser("sr.filter.based"), site: site);
        await SaveAsync(TestUser.Admin, it.DirectoryId, AppRoles.It);
        await SaveAsync(TestUser.Admin, mgr.DirectoryId, AppRoles.Manager, site.Id);
        await SaveAsync(TestUser.Admin, based.DirectoryId, AppRoles.Facility);

        Task<List<string>> NamesAsync(RoleFilter filter) => _f.RunAsAsync(TestUser.Admin, async sp =>
            (await sp.GetRequiredService<RoleAssignmentService>().PeopleAsync(filter))
            .Select(r => r.Username).Where(u => u.StartsWith("sr.filter.")).Order().ToList());

        Assert.Equal(["sr.filter.based", "sr.filter.it", "sr.filter.mgr"], await NamesAsync(new RoleFilter()));
        Assert.Equal(["sr.filter.it"], await NamesAsync(new RoleFilter(AppRoles.It)));
        Assert.Equal(["sr.filter.mgr"], await NamesAsync(new RoleFilter(AppRoles.Manager)));
        Assert.Equal(["sr.filter.based", "sr.filter.mgr"], await NamesAsync(new RoleFilter(SiteId: site.Id)));
        Assert.Equal(["sr.filter.mgr"], await NamesAsync(new RoleFilter(AppRoles.Manager, site.Id)));

        var html = await _f.CreateClientAs(TestUser.Admin).GetStringAsync($"{Page}?role=It&site=");
        Assert.Contains("sr.filter.it", html);
        Assert.DoesNotContain("sr.filter.mgr", html);
        html = await _f.CreateClientAs(TestUser.Admin).GetStringAsync($"{Page}?site={site.Id}");
        Assert.Contains("sr.filter.mgr", html);
        Assert.DoesNotContain("sr.filter.it<", html);

        // Saving a row keeps the filters (the ticked roles are not mistaken for the role filter).
        var res = await PostSaveAsync(_f.CreateClientAs(TestUser.Admin), it.DirectoryId, ["It", "Manager"], query: "?role=It&inactive=true");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal($"{Page}?role=It&inactive=true", res.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task The_super_admin_may_remove_the_last_admin_role_and_is_warned()
    {
        using var f = new AppFactory();
        var admin = TestUser.Admin;
        await f.EnsureUserAsync(admin);
        var person = await f.WithDbAsync(db => db.People.AsNoTracking().SingleAsync(p => p.Sid == admin.Sid));

        await ResetAccountAsync(f);
        var client = await SignedInClientAsync(f);
        Assert.DoesNotContain("id=\"roles-no-admin\"", await client.GetStringAsync(Page));

        var res = await PostSaveAsync(client, person.DirectoryId, []);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var html = await client.GetStringAsync(res.Headers.Location!.OriginalString);
        Assert.Contains("id=\"roles-warning\"", html);
        Assert.Contains("Nobody has the Admin role any more. From now on only the super admin can give roles.", html);
        Assert.Equal(AppRoles.None, await f.WithDbAsync(async db =>
            (await db.RoleAssignments.Where(r => r.PrincipalDirectoryId == person.DirectoryId).ToListAsync())
            .Aggregate(AppRoles.None, (a, r) => a | r.Role)));
        var audit = await f.WithDbAsync(db => db.AuditEvents.AsNoTracking().SingleAsync(a => a.Action == "role.remove"));
        Assert.Equal(AssetTracker.Core.Security.SuperAdmin.DisplayName, audit.Actor);

        // The former admin is locked out at once; the page keeps saying nobody has the role; the super admin gives it back.
        Assert.Equal(HttpStatusCode.Forbidden, (await f.CreateClientAs(admin).GetAsync(Page)).StatusCode);
        Assert.Contains("id=\"roles-no-admin\"", await client.GetStringAsync(Page));
        res = await PostSaveAsync(client, person.DirectoryId, ["Admin"], query: "?q=admin.user");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.CreateClientAs(admin).GetAsync(Page)).StatusCode);
    }

    [Fact]
    public async Task Settings_shows_a_notice_while_the_config_file_still_has_role_group_sids()
    {
        Assert.DoesNotContain("legacy-roles-note", await _f.CreateClientAs(TestUser.Admin).GetStringAsync("/Admin/Settings"));
        using var f = new SyncFactory(new() { ["Roles:ItGroupSids:0"] = "S-1-5-21-1000-1000-1000-5001" });
        var html = await f.CreateClientAs(TestUser.Admin).GetStringAsync("/Admin/Settings");
        Assert.Contains("id=\"legacy-roles-note\"", html);
        Assert.Contains("They are ignored", html);
    }
}
