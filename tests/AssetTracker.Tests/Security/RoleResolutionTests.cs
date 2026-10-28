using System.Net;
using System.Security.Claims;
using AssetTracker.Core.Admin;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using AssetTracker.Tests.Infrastructure;
using AssetTracker.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Security;

/// <summary>
/// Every Windows user's roles come from <c>RoleAssignments</c> (plan §1): matched on the person's SID, only while the
/// person is active; AD group SIDs in the token are ignored. (The super admin's exactly-Admin, no-lookup branch is
/// <c>SuperAdminSignInTests.The_claims_transformation_gives_the_super_admin_exactly_admin_without_a_database_lookup</c>.)
/// </summary>
public class RoleResolutionTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public RoleResolutionTests(AppFactory f) => _f = f;

    private static int _n;

    /// <summary>A Windows user with no roles of its own (the test gives them through the database).</summary>
    private static TestUser NewUser(string name)
    {
        var n = Interlocked.Increment(ref _n);
        return new TestUser($@"CORP\{name}", $"S-1-5-21-1000-1000-1000-77{n:00}");
    }

    private async Task<AppRoles> ResolveAsync(TestUser user, params string[] groupSids)
    {
        using var scope = _f.Services.CreateScope();
        var principal = TestAuthHandler.BuildPrincipal(user.Name, user.Sid);
        foreach (var g in groupSids) ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(ClaimTypes.GroupSid, g));
        var result = await scope.ServiceProvider.GetRequiredService<IClaimsTransformation>().TransformAsync(principal);
        return result.FindAll(AppClaimTypes.Role).Aggregate(AppRoles.None, (a, c) => a | Enum.Parse<AppRoles>(c.Value));
    }

    private Task<Person> PersonWithRolesAsync(TestUser user, AppRoles roles, bool active = true) => _f.WithDbAsync(async db =>
    {
        var p = await Seed.PersonAsync(db, user.Username, active: active, sid: user.Sid);
        foreach (var r in RoleAssignmentService.Assignable.Where(r => roles.HasFlag(r)))
            db.RoleAssignments.Add(new RoleAssignment
            {
                Role = r, PrincipalDirectoryId = p.DirectoryId, PrincipalName = p.Username, GrantedAt = DateTimeOffset.UtcNow,
                GrantedBy = "test",
            });
        await db.SaveChangesAsync();
        return p;
    });

    [Theory]
    [InlineData(AppRoles.It)]
    [InlineData(AppRoles.Manager)]
    [InlineData(AppRoles.Admin)]
    [InlineData(AppRoles.Facility)]
    [InlineData(AppRoles.It | AppRoles.Manager | AppRoles.Admin | AppRoles.Facility)]
    public async Task Every_role_comes_from_role_assignments(AppRoles roles)
    {
        var user = NewUser("rr.every." + (int)roles);
        Assert.Equal(AppRoles.None, await ResolveAsync(user));
        await PersonWithRolesAsync(user, roles);
        Assert.Equal(roles, await ResolveAsync(user));
    }

    [Fact]
    public async Task Inactive_people_get_no_roles_and_get_them_back_when_active_again()
    {
        var user = NewUser("rr.inactive");
        var person = await PersonWithRolesAsync(user, AppRoles.It | AppRoles.Admin, active: false);
        Assert.Equal(AppRoles.None, await ResolveAsync(user));
        Assert.Equal(HttpStatusCode.Forbidden, (await _f.CreateClientAs(user).GetAsync("/Assets")).StatusCode);

        await _f.WithDbAsync(async db =>
        {
            (await db.People.SingleAsync(p => p.Id == person.Id)).IsActive = true;
            await db.SaveChangesAsync();
        });
        Assert.Equal(AppRoles.It | AppRoles.Admin, await ResolveAsync(user));
    }

    [Fact]
    public async Task Group_sids_in_the_token_are_ignored()
    {
        // The SIDs the old configuration used for the IT, manager and admin groups, and any other group.
        var groups = new[] { "S-1-5-21-1000-1000-1000-5001", "S-1-5-21-1000-1000-1000-5002", "S-1-5-21-1000-1000-1000-5003", "S-1-5-32-544" };
        var nobody = NewUser("rr.groups");
        Assert.Equal(AppRoles.None, await ResolveAsync(nobody, groups));

        var facility = NewUser("rr.groups.facility");
        await PersonWithRolesAsync(facility, AppRoles.Facility);
        Assert.Equal(AppRoles.Facility, await ResolveAsync(facility, groups));
    }

    [Fact]
    public async Task Someone_elses_sid_or_no_sid_gets_nothing()
    {
        var owner = NewUser("rr.owner");
        await PersonWithRolesAsync(owner, AppRoles.Admin);
        Assert.Equal(AppRoles.None, await ResolveAsync(NewUser("rr.other")));
        Assert.Equal(AppRoles.None, await ResolveAsync(owner with { Sid = "" }));
    }

    [Fact]
    public async Task A_change_takes_effect_on_the_next_request()
    {
        var user = NewUser("rr.next");
        var person = await PersonWithRolesAsync(user, AppRoles.None);
        var client = _f.CreateClientAs(user);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/Assets")).StatusCode);

        await _f.RunAsAsync(TestUser.Admin, sp => sp.GetRequiredService<RoleAssignmentService>()
            .SaveAsync(person.DirectoryId, AppRoles.It, []));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Assets")).StatusCode);

        await _f.RunAsAsync(TestUser.Admin, sp => sp.GetRequiredService<RoleAssignmentService>()
            .SaveAsync(person.DirectoryId, AppRoles.None, []));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/Assets")).StatusCode);
    }

    [Fact]
    public void Old_role_group_settings_are_detected_for_the_startup_warning_and_the_settings_notice()
    {
        var withKeys = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Roles:AdminGroupSids:0"] = "S-1-5-21-1-2-3-4" }).Build();
        var without = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Clock:TimeZoneId"] = "UTC" }).Build();
        Assert.True(LegacyRoleSettings.ArePresent(withKeys));
        Assert.False(LegacyRoleSettings.ArePresent(without));
        // The test host has none (appsettings.json no longer has a Roles section).
        Assert.False(LegacyRoleSettings.ArePresent(_f.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()));
    }
}
