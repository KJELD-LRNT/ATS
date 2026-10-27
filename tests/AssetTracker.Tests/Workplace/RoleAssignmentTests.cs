using System.Net;
using AssetTracker.Core.Admin;
using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Workplace;

/// <summary>The Facility role lives in the application, not in an AD group.</summary>
public class RoleAssignmentTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public RoleAssignmentTests(AppFactory f) => _f = f;

    private static TestUser NoGroups(string name, int n) => new($@"CORP\{name}", $"S-1-5-21-1000-1000-1000-36{n:00}");

    private Task<T> AsAdmin<T>(Func<RoleAssignmentService, Task<T>> action) =>
        _f.RunAsAsync(TestUser.Admin, sp => action(sp.GetRequiredService<RoleAssignmentService>()));

    private async Task<HttpStatusCode> StatusAsync(TestUser user, string url) =>
        (await _f.CreateClientAs(user).GetAsync(url)).StatusCode;

    private Task<Person> PersonAsync(TestUser user, bool active = true, bool withSid = true) =>
        _f.WithDbAsync(db => Seed.PersonAsync(db, user.Name.Split('\\').Last(), active: active, sid: withSid ? user.Sid : null));

    [Fact]
    public async Task A_person_with_the_assigned_role_gets_facility_access_and_nothing_else()
    {
        var user = NoGroups("fac.one", 1);
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(user, "/Workplace"));

        var person = await PersonAsync(user);
        await AsAdmin(s => s.AssignAsync(person.Id, AppRoles.Facility));

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(user, "/Workplace"));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(user, "/Workplace/Import"));
        // facility opens neither inventory nor admin
        foreach (var url in new[] { "/Assets", "/Pools", "/Loans", "/Reports", "/Audit", "/Admin/SystemRoles", "/Admin/Sites" })
            Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(user, url));

        var home = await _f.CreateClientAs(user).GetStringAsync("/");
        Assert.Contains("floor plans", home);
        Assert.Contains("Facility", home);
    }

    [Fact]
    public async Task Removing_the_assignment_takes_the_access_away_at_once()
    {
        var user = NoGroups("fac.two", 2);
        var person = await PersonAsync(user);
        var row = await AsAdmin(s => s.AssignAsync(person.Id, AppRoles.Facility));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(user, "/Workplace"));

        await AsAdmin(async s => { await s.RemoveAsync(row.Id); return 0; });
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(user, "/Workplace"));
        Assert.True(await _f.WithDbAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "role.remove" && e.EntityId == row.Id.ToString())));
    }

    [Fact]
    public async Task The_role_has_no_effect_while_the_person_is_inactive_or_unknown_by_sid()
    {
        var user = NoGroups("fac.three", 3);
        var person = await PersonAsync(user);
        await AsAdmin(s => s.AssignAsync(person.Id, AppRoles.Facility));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(user, "/Workplace"));

        await _f.WithDbAsync(async db =>
        {
            var p = await db.People.SingleAsync(x => x.Id == person.Id);
            p.IsActive = false;
            await db.SaveChangesAsync();
        });
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(user, "/Workplace"));

        // someone else's SID gets nothing from this assignment
        var other = NoGroups("fac.three.other", 4);
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(other, "/Workplace"));
    }

    [Fact]
    public async Task The_role_adds_to_ad_roles()
    {
        // An IT user who is also assigned Facility keeps IT access to inventory.
        var person = await _f.WithDbAsync(db => Seed.PersonAsync(db, "it.user", sid: TestUser.It.Sid));
        await AsAdmin(s => s.AssignAsync(person.Id, AppRoles.Facility));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(TestUser.It, "/Assets"));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(TestUser.It, "/Workplace/Import"));
    }

    [Fact]
    public async Task Only_admins_manage_roles_only_assignable_roles_are_accepted_and_input_is_checked()
    {
        var user = NoGroups("fac.five", 5);
        var person = await PersonAsync(user);
        foreach (var other in new[] { TestUser.It, TestUser.Manager, TestUser.Nobody })
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() =>
                _f.RunAsAsync(other, sp => sp.GetRequiredService<RoleAssignmentService>().AssignAsync(person.Id, AppRoles.Facility)));
            await Assert.ThrowsAsync<AccessDeniedException>(() =>
                _f.RunAsAsync(other, sp => sp.GetRequiredService<RoleAssignmentService>().ListAsync()));
        }
        // AD-derived roles cannot be given here
        await Assert.ThrowsAsync<DomainException>(() => AsAdmin(s => s.AssignAsync(person.Id, AppRoles.It)));
        await Assert.ThrowsAsync<DomainException>(() => AsAdmin(s => s.AssignAsync(person.Id, AppRoles.Admin)));
        await Assert.ThrowsAsync<DomainException>(() => AsAdmin(s => s.AssignAsync(987654, AppRoles.Facility)));

        var inactive = await PersonAsync(NoGroups("fac.six", 6), active: false);
        await Assert.ThrowsAsync<DomainException>(() => AsAdmin(s => s.AssignAsync(inactive.Id, AppRoles.Facility)));
        var noSid = await PersonAsync(NoGroups("fac.seven", 7), withSid: false);
        await Assert.ThrowsAsync<DomainException>(() => AsAdmin(s => s.AssignAsync(noSid.Id, AppRoles.Facility)));

        var row = await AsAdmin(s => s.AssignAsync(person.Id, AppRoles.Facility));
        await Assert.ThrowsAsync<ConflictException>(() => AsAdmin(s => s.AssignAsync(person.Id, AppRoles.Facility)));
        Assert.True(await _f.WithDbAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "role.assign" && e.EntityId == row.Id.ToString())));
        Assert.Contains(await AsAdmin(s => s.ListAsync()), r => r.Id == row.Id && r.PersonUsername == person.Username);
    }

    [Fact]
    public async Task The_admin_page_assigns_and_removes_through_the_forms()
    {
        var user = NoGroups("fac.eight", 8);
        var person = await PersonAsync(user);
        var admin = _f.CreateClientAs(TestUser.Admin);
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(TestUser.It, "/Admin/SystemRoles"));

        var html = await admin.GetStringAsync("/Admin/SystemRoles?q=fac.eight");
        Assert.Contains("Give Facility role", html);

        var add = await FormHelper.PostFormAsync(admin, "/Admin/SystemRoles?q=fac.eight", "/Admin/SystemRoles?handler=Add&q=fac.eight",
            ("personId", person.Id.ToString()));
        Assert.Equal(HttpStatusCode.Redirect, add.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(user, "/Workplace"));
        Assert.Contains("fac.eight", await admin.GetStringAsync("/Admin/SystemRoles"));

        var id = await _f.WithDbAsync(db => db.RoleAssignments.Where(r => r.PrincipalName == "fac.eight").Select(r => r.Id).SingleAsync());
        var remove = await FormHelper.PostFormAsync(admin, "/Admin/SystemRoles", $"/Admin/SystemRoles?handler=Remove&id={id}");
        Assert.Equal(HttpStatusCode.Redirect, remove.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(user, "/Workplace"));
    }
}
