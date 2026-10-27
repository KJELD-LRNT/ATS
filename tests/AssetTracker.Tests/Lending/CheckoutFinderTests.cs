using System.Net;
using System.Text.Json;
using AssetTracker.Core.Catalog;
using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Inventory;
using AssetTracker.Core.Lending;
using AssetTracker.Tests.Infrastructure;
using AssetTracker.Tests.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Lending;

/// <summary>
/// The check-out item finder: one search over tagged assets and quantity pools (<c>LendingService.SearchItemsAsync</c>), the
/// <c>?handler=Items</c> endpoint behind the page's typeahead, and the server-side fallback when text is submitted
/// without JavaScript. Each test uses its own sites and tags (the class shares one database).
/// </summary>
public class CheckoutFinderTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public CheckoutFinderTests(AppFactory f) => _f = f;

    private Task<IReadOnlyList<ItemOption>> FindAsync(string? term, int? typeId = null, int? siteId = null, int take = 10) =>
        _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<LendingService>().SearchItemsAsync(term, typeId, siteId, take));

    private Task<Site> SiteAsync(string key) => _f.WithDbAsync(db => Seed.SiteAsync(db, key));

    private static string Imei(int n) => AssetTracker.Infrastructure.Demo.DemoSeeder.DemoImei(n);

    // ---- search ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Finds_assets_by_tag_model_vendor_serial_description_phone_number_and_imei()
    {
        var site = await SiteAsync("FD01");
        var model = await _f.RunAsAsync(TestUser.Admin, async sp =>
        {
            var catalog = sp.GetRequiredService<CatalogService>();
            var vendor = await catalog.CreateVendorAsync("Finder vendor");
            return await catalog.CreateModelAsync(BuiltIn.Laptop, vendor.Id, "Zephyr 14");
        });
        var laptop = await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<AssetService>().CreateAsync(
            new CreateAssetInput("FD01-LAP", BuiltIn.Laptop, site.Id, "Reception laptop", SerialNumber: "SN-FD01-77", ModelId: model.Id)));
        var phone = await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<AssetService>().CreateAsync(
            new CreateAssetInput("FD01-PHN", BuiltIn.Phone, site.Id, Fields: Seed.Fields(
                (BuiltIn.PhonePhoneNumberField, ["+31612345601"]), (BuiltIn.PhoneImeiField, [Imei(701)])))));

        foreach (var term in new[] { "fd01-lap", "ZEPHYR", "finder vend", "sn-fd01-77", "reception" })
            Assert.Equal(laptop.Tag, Assert.Single(await FindAsync(term)).Key);
        // A number is found whatever its formatting.
        Assert.Equal(phone.Tag, Assert.Single(await FindAsync("+31 6 1234 5601")).Key);
        Assert.Equal(phone.Tag, Assert.Single(await FindAsync(Imei(701))).Key);

        var hit = Assert.Single(await FindAsync("zephyr"));
        Assert.Equal(("asset", "Laptop", "Finder vendor Zephyr 14", "Reception laptop", site.Key, true, "Available"),
            (hit.Kind, hit.Type, hit.Title, hit.Description, hit.SiteKey, hit.CanCheckOut, hit.Status));
    }

    [Fact]
    public async Task A_tag_with_digits_does_not_match_phone_numbers_that_contain_them()
    {
        var site = await SiteAsync("FD02");
        await _f.WithDbAsync(async db =>
        {
            await Seed.AssetAsync(db, "FD02-LT-001", site);
            var phone = await Seed.AssetAsync(db, "FD02-PH-9", site, BuiltIn.Phone);
            await Seed.ValueAsync(db, phone, BuiltIn.PhonePhoneNumberField, "+31612340010"); // contains "001"
        });

        Assert.Equal(["FD02-LT-001"], (await FindAsync("FD02-LT-001")).Select(i => i.Key));
        Assert.Equal(["FD02-LT-001"], (await FindAsync("LT-001")).Select(i => i.Key));
        // A term that is a number still finds the phone by its digits.
        Assert.Equal(["FD02-PH-9"], (await FindAsync("06 1234 0010".Replace("06", "+316"))).Select(i => i.Key));
    }

    [Fact]
    public async Task Items_that_cannot_be_lent_are_listed_with_their_state_after_those_that_can()
    {
        var site = await SiteAsync("FD03");
        var holder = await _f.WithDbAsync(db => Seed.PersonAsync(db, "fd03.holder", site));
        await _f.WithDbAsync(async db =>
        {
            await Seed.AssetAsync(db, "FD03-A", site);
            await Seed.AssetAsync(db, "FD03-B", site);
            var lent = await Seed.AssetAsync(db, "FD03-C", site, state: AssetState.CheckedOut);
            db.Loans.Add(new Loan { AssetId = lent.Id, HolderId = holder.Id, Quantity = 1, StartedAt = DateTimeOffset.UtcNow,
                HolderNameSnapshot = "Fiona Holder", CreatedBy = "test" });
            await Seed.AssetAsync(db, "FD03-D", site, state: AssetState.Unavailable);
            await Seed.AssetAsync(db, "FD03-E", site, state: AssetState.Retired);
            await db.SaveChangesAsync();
        });

        var found = await FindAsync("FD03-");
        // Retired is never offered; available first, then the rest.
        Assert.Equal(["FD03-A", "FD03-B", "FD03-C", "FD03-D"], found.Select(i => i.Key));
        Assert.Equal([true, true, false, false], found.Select(i => i.CanCheckOut));
        Assert.Equal(("Checked out", "Fiona Holder"), (found[2].Status, found[2].Holder));
        Assert.Equal(("Unavailable", null), (found[3].Status, found[3].Holder));

        // An exact tag comes first even when it cannot be lent.
        Assert.Equal("FD03-D", (await FindAsync("FD03-D")).First().Key);
        Assert.Equal("FD03-C", (await FindAsync("fd03-c")).First().Key);
    }

    [Fact]
    public async Task Pools_are_in_the_same_list_with_their_availability()
    {
        var site = await SiteAsync("FD04");
        await _f.WithDbAsync(async db =>
        {
            await Seed.PoolAsync(db, "FD04-HS", site, 12);
            await Seed.PoolAsync(db, "FD04-CAM", site, 0, BuiltIn.Webcam);
            await Seed.AssetAsync(db, "FD04-LT", site);
        });

        var found = await FindAsync("FD04-");
        Assert.Equal(["FD04-LT", "FD04-HS", "FD04-CAM"], found.Select(i => i.Key)); // lendable first, assets before pools
        var hs = found.Single(i => i.Key == "FD04-HS");
        Assert.Equal(("pool", "Headset", true, "12 available", 12), (hs.Kind, hs.Type, hs.CanCheckOut, hs.Status, hs.Available));
        var cam = found.Single(i => i.Key == "FD04-CAM");
        Assert.Equal((false, "None available"), (cam.CanCheckOut, cam.Status));

        // Pools are found by SKU, description or type name.
        Assert.Contains(await FindAsync("fd04-hs"), i => i.Key == "FD04-HS");
        Assert.Contains(await FindAsync("webcam", siteId: site.Id), i => i.Key == "FD04-CAM");
    }

    [Fact]
    public async Task Type_and_site_filters_narrow_the_results()
    {
        var a = await SiteAsync("FD05");
        var b = await SiteAsync("FD06");
        await _f.WithDbAsync(async db =>
        {
            await Seed.AssetAsync(db, "FDF-LT-5", a);
            await Seed.AssetAsync(db, "FDF-DT-5", a, BuiltIn.Desktop);
            await Seed.AssetAsync(db, "FDF-LT-6", b);
            await Seed.PoolAsync(db, "FDF-HS-5", a, 3);
        });

        Assert.Equal(["FDF-DT-5", "FDF-LT-5", "FDF-LT-6", "FDF-HS-5"], (await FindAsync("FDF-")).Select(i => i.Key));
        Assert.Equal(["FDF-DT-5", "FDF-LT-5", "FDF-HS-5"], (await FindAsync("FDF-", siteId: a.Id)).Select(i => i.Key));
        Assert.Equal(["FDF-LT-5", "FDF-LT-6"], (await FindAsync("FDF-", typeId: BuiltIn.Laptop)).Select(i => i.Key));
        Assert.Equal(["FDF-HS-5"], (await FindAsync("FDF-", typeId: BuiltIn.Headset, siteId: a.Id)).Select(i => i.Key));
        Assert.Empty(await FindAsync("FDF-", typeId: BuiltIn.Webcam));
    }

    [Fact]
    public async Task Without_a_term_only_what_is_available_is_listed_and_the_list_is_limited()
    {
        var site = await SiteAsync("FD07");
        await _f.WithDbAsync(async db =>
        {
            for (var i = 1; i <= 12; i++) await Seed.AssetAsync(db, $"FD07-{i:00}", site);
            await Seed.AssetAsync(db, "FD07-OUT", site, state: AssetState.CheckedOut);
            await Seed.PoolAsync(db, "FD07-EMPTY", site, 0);
        });

        var browse = await FindAsync(null, siteId: site.Id, take: 5);
        Assert.Equal(["FD07-01", "FD07-02", "FD07-03", "FD07-04", "FD07-05"], browse.Select(i => i.Key));
        var all = await FindAsync("  ", siteId: site.Id, take: 50);
        Assert.Equal(12, all.Count);
        Assert.DoesNotContain(all, i => i.Key is "FD07-OUT" or "FD07-EMPTY");
        Assert.All(all, i => Assert.True(i.CanCheckOut));
    }

    [Fact]
    public async Task Wildcard_characters_in_the_term_are_literal()
    {
        var site = await SiteAsync("FD08");
        await _f.WithDbAsync(db => Seed.AssetAsync(db, "FD08-X", site));
        Assert.Empty(await FindAsync("FD08%"));
        Assert.Empty(await FindAsync("FD0_-X"));
    }

    [Fact]
    public async Task Only_it_may_use_the_finder()
    {
        foreach (var user in new[] { TestUser.Manager, TestUser.Admin, TestUser.Nobody })
            await Assert.ThrowsAsync<AccessDeniedException>(() =>
                _f.RunAsAsync(user, sp => sp.GetRequiredService<LendingService>().SearchItemsAsync("x")));
    }

    // ---- endpoint -------------------------------------------------------------------------------------------

    private async Task<JsonElement> ItemsAsync(HttpClient client, string query)
    {
        var res = await client.GetAsync("/Loans/Checkout?handler=Items" + query);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("application/json", res.Content.Headers.ContentType!.MediaType);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task The_items_endpoint_returns_matches_with_site_and_state_and_is_for_it_only()
    {
        var site = await SiteAsync("FD09");
        var holder = await _f.WithDbAsync(db => Seed.PersonAsync(db, "fd09.holder", site));
        await _f.WithDbAsync(async db =>
        {
            await Seed.AssetAsync(db, "FD09-FREE", site);
            var lent = await Seed.AssetAsync(db, "FD09-LENT", site, state: AssetState.CheckedOut);
            db.Loans.Add(new Loan { AssetId = lent.Id, HolderId = holder.Id, Quantity = 1, StartedAt = DateTimeOffset.UtcNow,
                HolderNameSnapshot = "Hanna Holder", CreatedBy = "test" });
            await Seed.PoolAsync(db, "FD09-POOL", site, 4);
            await db.SaveChangesAsync();
        });
        var it = _f.CreateClientAs(TestUser.It);

        var found = (await ItemsAsync(it, "&q=fd09-")).EnumerateArray().ToDictionary(e => e.GetProperty("key").GetString()!);
        Assert.Equal(["FD09-FREE", "FD09-LENT", "FD09-POOL"], found.Keys.Order());
        var free = found["FD09-FREE"];
        Assert.Equal(("asset", "FD09", true, "Available"),
            (free.GetProperty("kind").GetString(), free.GetProperty("siteKey").GetString(), free.GetProperty("canCheckOut").GetBoolean(), free.GetProperty("status").GetString()));
        Assert.Equal(site.Id, free.GetProperty("siteId").GetInt32());
        var lentItem = found["FD09-LENT"];
        Assert.Equal((false, "Checked out", "Hanna Holder"),
            (lentItem.GetProperty("canCheckOut").GetBoolean(), lentItem.GetProperty("status").GetString(), lentItem.GetProperty("holder").GetString()));
        var pool = found["FD09-POOL"];
        Assert.Equal(("pool", 4), (pool.GetProperty("kind").GetString(), pool.GetProperty("available").GetInt32()));

        // Filters, browse (empty text) and the one-character rule.
        Assert.Equal(1, (await ItemsAsync(it, $"&q=fd09-&site={site.Id}&type={BuiltIn.Headset}")).GetArrayLength());
        var browse = (await ItemsAsync(it, $"&site={site.Id}")).EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToList();
        Assert.Equal(["FD09-FREE", "FD09-POOL"], browse.Order());
        Assert.Equal(0, (await ItemsAsync(it, "&q=f")).GetArrayLength());

        foreach (var user in new[] { TestUser.Manager, TestUser.Admin, TestUser.Nobody })
            Assert.Equal(HttpStatusCode.Forbidden,
                (await _f.CreateClientAs(user).GetAsync("/Loans/Checkout?handler=Items&q=fd09-")).StatusCode);
    }

    // ---- page -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_page_has_one_finder_with_type_and_site_filters_instead_of_the_tag_and_pool_pickers()
    {
        var html = await _f.CreateClientAs(TestUser.It).GetStringAsync("/Loans/Checkout");
        Assert.Contains("data-item-lookup", html);
        Assert.Contains("name=\"type\"", html);
        Assert.Contains("name=\"site\"", html);
        Assert.Contains("<option value=\"\">All types</option>", html);
        Assert.DoesNotContain("data-segmented", html);
        Assert.DoesNotContain("id=\"f-pool\"", html);
        // The picked-item ids are disabled until the finder (JavaScript) fills them in.
        Assert.Matches(@"<input[^>]*name=""assetId""[^>]*disabled", html);
    }

    [Fact]
    public async Task Text_that_finds_one_item_selects_it_without_javascript()
    {
        var site = await SiteAsync("FD10");
        var model = await _f.RunAsAsync(TestUser.Admin, async sp =>
        {
            var catalog = sp.GetRequiredService<CatalogService>();
            return await catalog.CreateModelAsync(BuiltIn.Laptop, (await catalog.CreateVendorAsync("Solo vendor")).Id, "Unicorn 9");
        });
        var asset = await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<AssetService>().CreateAsync(
            new CreateAssetInput("FD10-ONLY", BuiltIn.Laptop, site.Id, ModelId: model.Id)));
        await _f.WithDbAsync(db => Seed.PoolAsync(db, "FD10-UNIQUEPOOL", site, 6));
        var it = _f.CreateClientAs(TestUser.It);

        var byModel = await it.GetStringAsync("/Loans/Checkout?tag=unicorn");
        Assert.Contains($"name=\"Input.AssetId\" value=\"{asset.Id}\"", byModel);
        Assert.Contains("value=\"FD10-ONLY\"", byModel);

        var byPool = await it.GetStringAsync("/Loans/Checkout?tag=uniquepool");
        Assert.Contains("name=\"Input.PoolId\"", byPool);
        Assert.Contains("value=\"FD10-UNIQUEPOOL\"", byPool);
    }

    [Fact]
    public async Task Text_that_finds_several_items_lists_them_with_links_and_keeps_the_person_and_filters()
    {
        var site = await SiteAsync("FD11");
        var person = await _f.WithDbAsync(async db =>
        {
            await Seed.AssetAsync(db, "FD11-A", site);
            await Seed.AssetAsync(db, "FD11-B", site, state: AssetState.CheckedOut);
            return await Seed.PersonAsync(db, "fd11.person", site);
        });
        var it = _f.CreateClientAs(TestUser.It);

        var html = System.Net.WebUtility.HtmlDecode(
            await it.GetStringAsync($"/Loans/Checkout?tag=FD11-&who=fd11&holderId={person.Id}&site={site.Id}"));
        Assert.Contains("Several items match", html);
        var asset = await _f.WithDbAsync(db => db.Assets.SingleAsync(a => a.Tag == "FD11-A"));
        Assert.Matches($@"href=""/Loans/Checkout\?assetId={asset.Id}&[^""]*holderId={person.Id}", html);
        Assert.Contains("FD11-B", html);
        Assert.Contains("aria-disabled=\"true\"", html); // the checked-out one cannot be chosen
        Assert.DoesNotContain("name=\"Input.AssetId\"", html); // nothing to confirm yet

        // Following the link selects the item and the person.
        var picked = await it.GetStringAsync($"/Loans/Checkout?assetId={asset.Id}&who=fd11&holderId={person.Id}&site={site.Id}");
        Assert.Contains($"name=\"Input.AssetId\" value=\"{asset.Id}\"", picked);
        Assert.Contains($"value=\"{person.Id}\" required checked", picked);
    }

    [Fact]
    public async Task Text_that_finds_nothing_or_one_unavailable_item_explains_why()
    {
        var site = await SiteAsync("FD12");
        await _f.WithDbAsync(db => Seed.AssetAsync(db, "FD12-GONE", site, state: AssetState.Unavailable));
        var it = _f.CreateClientAs(TestUser.It);

        Assert.Contains("No item found for", System.Net.WebUtility.HtmlDecode(await it.GetStringAsync("/Loans/Checkout?tag=nothing-like-this-zz")));
        var unavailable = await it.GetStringAsync("/Loans/Checkout?tag=FD12-GONE");
        Assert.Contains("cannot be checked out", unavailable);
        Assert.DoesNotContain("id=\"checkout-confirm\"", unavailable);
    }

    [Fact]
    public async Task A_picked_pool_checks_out_with_a_quantity()
    {
        var (pool, holder) = await _f.WithDbAsync(async db =>
        {
            await NotificationSeed.SetItMailboxAsync(db, "it-fd13@example.test");
            var site = await Seed.SiteAsync(db, "FD13");
            return (await Seed.PoolAsync(db, "FD13-HS", site, 10), await Seed.PersonAsync(db, "fd13.holder", site));
        });
        var it = _f.CreateClientAs(TestUser.It);

        var form = $"/Loans/Checkout?tag=FD13-HS&poolId={pool.Id}&holderId={holder.Id}&quantity=3";
        var html = await it.GetStringAsync(form);
        Assert.Contains($"name=\"Input.PoolId\" value=\"{pool.Id}\"", html);
        Assert.Contains("name=\"Input.Quantity\"", html);
        var res = await AntiforgeryHelper.PostFormAsync(it, form, new Dictionary<string, string>
        {
            ["Input.PoolId"] = pool.Id.ToString(), ["Input.HolderId"] = holder.Id.ToString(), ["Input.Quantity"] = "3",
        }, postUrl: "/Loans/Checkout");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal((7, 3), await _f.WithDbAsync(async db =>
        {
            var p = await db.Pools.AsNoTracking().SingleAsync(x => x.Id == pool.Id);
            return (p.Available, p.Outstanding);
        }));
    }
}
