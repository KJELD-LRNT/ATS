using System.Net;
using AssetTracker.Core.Security;
using AssetTracker.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Foundation;

public class AccessTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public AccessTests(AppFactory f) => _f = f;

    [Fact]
    public async Task Unauthenticated_request_is_challenged()
    {
        var res = await _f.CreateClient().GetAsync("/");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Health_endpoint_is_anonymous()
    {
        var res = await _f.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Theory]
    [InlineData("It", AppRoles.It)]
    [InlineData("Manager", AppRoles.Manager)]
    [InlineData("Admin", AppRoles.Admin)]
    [InlineData("Nobody", AppRoles.None)]
    public async Task Roles_are_derived_from_group_sids(string userKey, AppRoles expected)
    {
        var user = userKey switch
        {
            "It" => TestUser.It, "Manager" => TestUser.Manager, "Admin" => TestUser.Admin, _ => TestUser.Nobody,
        };
        var roles = await _f.RunAsAsync(user, sp => Task.FromResult(sp.GetRequiredService<ICurrentUser>().Roles));
        Assert.Equal(expected, roles);
    }

    [Fact]
    public async Task Overlapping_groups_union_roles_without_implied_admin_powers()
    {
        var both = new TestUser(@"CORP\both", "S-1-5-21-1000-1000-1000-2099", AppRoles.Admin | AppRoles.Manager);
        var roles = await _f.RunAsAsync(both, sp => Task.FromResult(sp.GetRequiredService<ICurrentUser>().Roles));
        Assert.Equal(AppRoles.Admin | AppRoles.Manager, roles);
    }

    [Fact]
    public async Task Admin_alone_has_no_inventory_access()
    {
        var access = await _f.RunAsAsync(TestUser.Admin, sp => sp.GetRequiredService<AccessScope>().GetAsync());
        Assert.False(access.CanReadInventory);
    }

    [Fact]
    public async Task Manager_sees_only_granted_sites()
    {
        var (a, b) = await _f.WithDbAsync(async db =>
        {
            var s1 = await Seed.SiteAsync(db, "ACC1");
            var s2 = await Seed.SiteAsync(db, "ACC2");
            await Seed.ManagerWithGrantsAsync(db, TestUser.Manager, s1);
            return (s1, s2);
        });

        var access = await _f.RunAsAsync(TestUser.Manager, sp => sp.GetRequiredService<AccessScope>().GetAsync());
        Assert.True(access.CanRead(a.Id));
        Assert.False(access.CanRead(b.Id));
        Assert.False(access.AllSites);
    }

    [Fact]
    public async Task Grant_has_no_effect_once_principal_leaves_manager_group()
    {
        var site = await _f.WithDbAsync(async db =>
        {
            var s = await Seed.SiteAsync(db, "ACC3");
            await Seed.ManagerWithGrantsAsync(db, TestUser.Nobody, s);
            return s;
        });

        var access = await _f.RunAsAsync(TestUser.Nobody, sp => sp.GetRequiredService<AccessScope>().GetAsync());
        Assert.False(access.CanRead(site.Id));
    }
}
