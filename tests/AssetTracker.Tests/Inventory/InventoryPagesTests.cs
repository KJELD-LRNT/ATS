using System.Net;
using AssetTracker.Core.Domain;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Tests.Inventory;

public class InventoryPagesTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public InventoryPagesTests(AppFactory f) => _f = f;

    [Theory]
    [InlineData("/Assets")]
    [InlineData("/Pools")]
    [InlineData("/Assets/Create")]
    [InlineData("/Pools/Create")]
    public async Task It_can_open_inventory_pages(string url)
    {
        var res = await _f.CreateClientAs(TestUser.It).GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Theory]
    [InlineData("/Assets")]
    [InlineData("/Pools")]
    public async Task Admin_only_and_plain_users_get_403_on_inventory(string url)
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _f.CreateClientAs(TestUser.Admin).GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _f.CreateClientAs(TestUser.Nobody).GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Manager_reads_granted_site_but_gets_404_for_other_sites()
    {
        var manager = new TestUser(@"CORP\mgr.ip01", "S-1-5-21-1000-1000-1000-3101", TestGroups.Manager);
        var (mine, theirs, pool, otherPool) = await _f.WithDbAsync(async db =>
        {
            var granted = await Seed.SiteAsync(db, "IP01");
            var other = await Seed.SiteAsync(db, "IP02");
            await Seed.ManagerWithGrantsAsync(db, manager, granted);
            return (await Seed.AssetAsync(db, "IP-0001", granted), await Seed.AssetAsync(db, "IP-0002", other),
                await Seed.PoolAsync(db, "HS-IP", granted, 3), await Seed.PoolAsync(db, "HS-IP", other, 3));
        });
        var client = _f.CreateClientAs(manager);

        var list = await (await client.GetAsync("/Assets?q=IP-000")).Content.ReadAsStringAsync();
        Assert.Contains("IP-0001", list);
        Assert.DoesNotContain("IP-0002", list);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/Assets/Details/{mine.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Assets/Details/{theirs.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/Pools/Details/{pool.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Pools/Details/{otherPool.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/Assets/Edit/{mine.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/Pools/Adjust/{pool.Id}")).StatusCode);
    }

    [Fact]
    public async Task Manager_cannot_post_create()
    {
        var site = await _f.WithDbAsync(db => Seed.SiteAsync(db, "IP03"));
        var client = _f.CreateClientAs(TestUser.Manager);

        // Authorization runs before antiforgery validation, so the policy rejects the request outright.
        var res = await client.PostAsync("/Assets/Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Tag"] = "IP-0003", ["Input.Type"] = "Laptop", ["Input.SiteId"] = site.Id.ToString(),
            ["Input.Description"] = "Laptop",
        }));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.False(await _f.WithDbAsync(db => db.Assets.AnyAsync(a => a.Tag == "IP-0003")));
    }

    [Fact]
    public async Task It_creates_an_asset_through_the_form()
    {
        var site = await _f.WithDbAsync(db => Seed.SiteAsync(db, "IP04"));
        var client = _f.CreateClientAs(TestUser.It);

        var res = await AntiforgeryHelper.PostFormAsync(client, "/Assets/Create", new Dictionary<string, string>
        {
            ["Input.Tag"] = "ip-0004", ["Input.Type"] = "Phone", ["Input.SiteId"] = site.Id.ToString(),
            ["Input.Description"] = "Phone model", ["Input.PhoneNumber"] = "+31 6 1234 0404",
            ["Input.Imei"] = "490154203237518",
        });

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var asset = await _f.WithDbAsync(db => db.Assets.SingleAsync(a => a.Tag == "IP-0004"));
        Assert.Equal("+31612340404", asset.PhoneNumber);
        Assert.EndsWith($"/Assets/Details/{asset.Id}", res.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Form_errors_are_shown_and_post_without_token_is_rejected()
    {
        var site = await _f.WithDbAsync(db => Seed.SiteAsync(db, "IP05"));
        var client = _f.CreateClientAs(TestUser.It);
        var fields = new Dictionary<string, string>
        {
            ["Input.Tag"] = "IP-0005", ["Input.Type"] = "Laptop", ["Input.SiteId"] = site.Id.ToString(),
            ["Input.Description"] = "Laptop", ["Input.PhoneNumber"] = "+31612340505",
        };

        var invalid = await AntiforgeryHelper.PostFormAsync(client, "/Assets/Create", fields);
        Assert.Equal(HttpStatusCode.OK, invalid.StatusCode);
        Assert.Contains("only applies to phones", await invalid.Content.ReadAsStringAsync());

        fields.Remove("Input.PhoneNumber");
        var noToken = await client.PostAsync("/Assets/Create", new FormUrlEncodedContent(fields));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);
        Assert.False(await _f.WithDbAsync(db => db.Assets.AnyAsync(a => a.Tag == "IP-0005")));
    }

    [Fact]
    public async Task It_adjusts_and_transfers_pool_stock_through_the_form()
    {
        var (src, dst) = await _f.WithDbAsync(async db =>
        {
            var a = await Seed.SiteAsync(db, "IP06");
            var b = await Seed.SiteAsync(db, "IP07");
            return (await Seed.PoolAsync(db, "HS-FORM", a, 2), await Seed.PoolAsync(db, "HS-FORM", b, 0));
        });
        var client = _f.CreateClientAs(TestUser.It);
        var form = $"/Pools/Adjust/{src.Id}";

        var receipt = await AntiforgeryHelper.PostFormAsync(client, form,
            new Dictionary<string, string> { ["quantity"] = "5", ["reason"] = "Delivery" }, form + "?handler=Receipt");
        Assert.Equal(HttpStatusCode.Redirect, receipt.StatusCode);

        var transfer = await AntiforgeryHelper.PostFormAsync(client, form, new Dictionary<string, string>
        {
            ["toPoolId"] = dst.Id.ToString(), ["quantity"] = "4", ["reason"] = "Rebalance",
        }, form + "?handler=Transfer");
        Assert.Equal(HttpStatusCode.Redirect, transfer.StatusCode);

        var noReason = await AntiforgeryHelper.PostFormAsync(client, form,
            new Dictionary<string, string> { ["quantity"] = "1", ["reason"] = "" }, form + "?handler=Loss");
        Assert.Equal(HttpStatusCode.OK, noReason.StatusCode);
        Assert.Contains("reason is required", await noReason.Content.ReadAsStringAsync());

        var balances = await _f.WithDbAsync(db => db.Pools.Where(p => p.Sku == "HS-FORM").OrderBy(p => p.Id)
            .Select(p => p.Available).ToListAsync());
        Assert.Equal([3, 4], balances);

        var details = await (await client.GetAsync($"/Pools/Details/{src.Id}")).Content.ReadAsStringAsync();
        Assert.Contains("Transfer out", details);
        Assert.Contains("Rebalance", details);
    }

    [Fact]
    public async Task Layout_shows_inventory_links_only_to_inventory_roles()
    {
        var it = await (await _f.CreateClientAs(TestUser.It).GetAsync("/")).Content.ReadAsStringAsync();
        var admin = await (await _f.CreateClientAs(TestUser.Admin).GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Contains("href=\"/Assets\"", it);
        Assert.DoesNotContain("href=\"/Assets\"", admin);
    }

    [Fact]
    public async Task Asset_tag_cannot_be_changed_through_the_edit_form()
    {
        var asset = await _f.WithDbAsync(async db => await Seed.AssetAsync(db, "IP-0008", await Seed.SiteAsync(db, "IP08")));
        var client = _f.CreateClientAs(TestUser.It);
        var form = $"/Assets/Edit/{asset.Id}";

        var res = await AntiforgeryHelper.PostFormAsync(client, form, new Dictionary<string, string>
        {
            ["Input.Description"] = "Renamed", ["Input.Tag"] = "HACKED", ["Tag"] = "HACKED",
        });

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var stored = await _f.WithDbAsync(db => db.Assets.AsNoTracking().SingleAsync(a => a.Id == asset.Id));
        Assert.Equal(("IP-0008", "Renamed"), (stored.Tag, stored.Description));
        Assert.Equal(AssetType.Laptop, stored.Type);
    }
}
