using System.Net;
using AssetTracker.Core.Catalog;
using AssetTracker.Core.Domain;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Workplace;

/// <summary>Which asset types are placed at desks on floor plans (desktops and screens) is set per type by an admin.</summary>
public class WorkspotRoleTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public WorkspotRoleTests(AppFactory f) => _f = f;

    private Task<T> AsAdmin<T>(Func<CatalogService, Task<T>> action) =>
        _f.RunAsAsync(TestUser.Admin, sp => action(sp.GetRequiredService<CatalogService>()));

    [Fact]
    public async Task Desktop_and_screen_types_exist_by_default_and_other_types_are_not_placed()
    {
        var roles = await _f.WithDbAsync(db => db.AssetTypes.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.WorkspotRole));
        Assert.Equal(WorkspotRole.Desktop, roles[BuiltIn.Desktop]);
        Assert.Equal(WorkspotRole.Screen, roles[BuiltIn.Screen]);
        Assert.Equal(WorkspotRole.None, roles[BuiltIn.Laptop]);
        Assert.Equal(WorkspotRole.None, roles[BuiltIn.Headset]);
    }

    [Fact]
    public async Task The_role_is_saved_kept_when_not_given_and_cleared_for_pools()
    {
        var type = await AsAdmin(c => c.CreateTypeAsync(new TypeInput("Monitor WR1", InventoryMode.Tagged, WorkspotRole: WorkspotRole.Screen)));
        Assert.Equal(WorkspotRole.Screen, type.WorkspotRole);
        // an update that does not mention floor plans leaves the role alone
        await AsAdmin(c => c.UpdateTypeAsync(type.Id, new TypeInput("Monitor WR1b", InventoryMode.Tagged)));
        Assert.Equal(WorkspotRole.Screen, (await _f.WithDbAsync(db => db.AssetTypes.AsNoTracking().SingleAsync(t => t.Id == type.Id))).WorkspotRole);

        var pooled = await AsAdmin(c => c.CreateTypeAsync(new TypeInput("Dock WR1", InventoryMode.Pooled, WorkspotRole: WorkspotRole.Desktop)));
        Assert.Equal(WorkspotRole.None, pooled.WorkspotRole); // pools are never placed at desks
        Assert.True(await _f.WithDbAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "type.create" && e.DetailsJson!.Contains("\"WorkspotRole\":\"Screen\""))));
    }

    [Fact]
    public async Task The_admin_sets_the_role_on_the_type_page()
    {
        var type = await AsAdmin(c => c.CreateTypeAsync(new TypeInput("Thin client WR2", InventoryMode.Tagged)));
        var client = _f.CreateClientAs(TestUser.Admin);
        var edit = $"/Admin/TypeEdit/{type.Id}";
        Assert.Contains("On floor plans", await client.GetStringAsync(edit));
        var res = await FormHelper.PostFormAsync(client, edit, edit,
            ("Input.Name", "Thin client WR2"), ("Input.IsActive", "true"), ("Input.UsesModel", "true"), ("Input.UsesDescription", "true"),
            ("Input.UsesSerialNumber", "true"), ("Input.UsesCondition", "true"), ("Input.WorkspotRole", "Desktop"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal(WorkspotRole.Desktop, (await _f.WithDbAsync(db => db.AssetTypes.AsNoTracking().SingleAsync(t => t.Id == type.Id))).WorkspotRole);
    }
}
