using AssetTracker.Core.Security;
using System.Net;
using System.Text.Json;
using AssetTracker.Core.Catalog;
using AssetTracker.Core.Domain;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Catalog;

/// <summary>The admin pages for types, fields, vendors and models, and the asset forms built from them.</summary>
public class CatalogPagesTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public CatalogPagesTests(AppFactory f) => _f = f;

    public static TheoryData<string> AdminPages => ["/Admin/Types", $"/Admin/TypeEdit/{BuiltIn.PhoneNumber}", "/Admin/Vendors", "/Admin/Models"];

    [Theory]
    [MemberData(nameof(AdminPages))]
    public async Task Catalog_pages_are_for_admins_only(string url)
    {
        foreach (var user in new[] { TestUser.It, TestUser.Manager, TestUser.Nobody })
            Assert.Equal(HttpStatusCode.Forbidden, (await _f.CreateClientAs(user).GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _f.CreateClientAs(TestUser.Admin).GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Admin_menu_lists_the_catalog_pages_for_admins_only()
    {
        var admin = await _f.CreateClientAs(TestUser.Admin).GetStringAsync("/");
        Assert.Contains("href=\"/Admin/Types\"", admin);
        Assert.Contains("href=\"/Admin/Vendors\"", admin);
        Assert.Contains("href=\"/Admin/Models\"", admin);
        Assert.DoesNotContain("href=\"/Admin/Types\"", await _f.CreateClientAs(TestUser.It).GetStringAsync("/"));
    }

    [Fact]
    public async Task Admin_defines_a_type_with_a_tag_pattern_and_custom_fields_through_the_forms()
    {
        var c = _f.CreateClientAs(TestUser.Admin);
        var res = await FormHelper.PostFormAsync(c, "/Admin/Types", "/Admin/Types",
            ("Input.Name", "Monitor"), ("Input.Mode", "Tagged"), ("Input.UsesModel", "true"), ("Input.UsesSerialNumber", "true"),
            // The checkbox tag helper sends "false" for a box left unchecked; a hand-made post has to say so.
            ("Input.UsesDescription", "false"), ("Input.UsesCondition", "false"),
            ("Input.TagPattern", "{SITE}M{000}OMA"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var type = await _f.WithDbAsync(db => db.AssetTypes.SingleAsync(t => t.Name == "Monitor"));
        Assert.Equal("{SITE}M{000}OMA", type.TagPattern);
        Assert.False(type.UsesDescription); // unchecked boxes are not sent
        Assert.EndsWith($"/Admin/TypeEdit/{type.Id}", res.Headers.Location!.OriginalString);

        var edit = $"/Admin/TypeEdit/{type.Id}";
        res = await FormHelper.PostFormAsync(c, edit, $"{edit}?handler=AddField",
            ("Field.Name", "Screen size"), ("Field.Kind", "Number"), ("Field.MaxValues", "1"), ("Field.IsRequired", "true"), ("Field.IsActive", "true"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        res = await FormHelper.PostFormAsync(c, edit, $"{edit}?handler=AddField",
            ("Field.Name", "Panel"), ("Field.Kind", "Choice"), ("Field.Options", "IPS\nVA\nTN"), ("Field.MaxValues", "1"), ("Field.IsActive", "true"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var html = await c.GetStringAsync(edit);
        Assert.Contains("Screen size", html);
        Assert.Contains("IPS, VA, TN", html);
        Assert.Contains("RM001OMA", html); // the example rendered from the pattern

        var fields = await _f.WithDbAsync(db => db.FieldDefinitions.Where(f => f.TypeId == type.Id).OrderBy(f => f.SortOrder).ToListAsync());
        Assert.Equal(["Screen size", "Panel"], fields.Select(f => f.Name));

        // Move the second field up, then edit it.
        res = await FormHelper.PostFormAsync(c, edit, $"{edit}?handler=Move", ("fieldId", fields[1].Id.ToString()), ("up", "true"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("Panel", (await _f.WithDbAsync(db => db.FieldDefinitions.Where(f => f.TypeId == type.Id).OrderBy(f => f.SortOrder).FirstAsync())).Name);

        Assert.Contains("Edit field Panel", await c.GetStringAsync($"{edit}?fieldId={fields[1].Id}"));
        res = await FormHelper.PostFormAsync(c, $"{edit}?fieldId={fields[1].Id}", $"{edit}?handler=UpdateField&fieldId={fields[1].Id}",
            ("Field.Name", "Panel type"), ("Field.Kind", "Choice"), ("Field.Options", "IPS\nVA"), ("Field.MaxValues", "1"), ("Field.IsActive", "true"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("IPS\nVA", (await _f.WithDbAsync(db => db.FieldDefinitions.SingleAsync(f => f.Id == fields[1].Id))).Options);
    }

    [Fact]
    public async Task Type_form_errors_are_shown()
    {
        var c = _f.CreateClientAs(TestUser.Admin);
        var res = await FormHelper.PostFormAsync(c, "/Admin/Types", "/Admin/Types",
            ("Input.Name", "Bad tag type"), ("Input.Mode", "Tagged"), ("Input.TagPattern", "NO-NUMBER"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("number token", await res.Content.ReadAsStringAsync());

        res = await FormHelper.PostFormAsync(c, "/Admin/Types", "/Admin/Types", ("Input.Name", "Laptop"), ("Input.Mode", "Tagged"));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("already exists", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Admin_manages_vendors_and_models_through_the_forms()
    {
        var c = _f.CreateClientAs(TestUser.Admin);
        var res = await FormHelper.PostFormAsync(c, "/Admin/Vendors", "/Admin/Vendors?handler=Create", ("Input.Name", "Page Vendor"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var vendor = await _f.WithDbAsync(db => db.Vendors.SingleAsync(v => v.Name == "Page Vendor"));

        res = await FormHelper.PostFormAsync(c, "/Admin/Vendors", "/Admin/Vendors?handler=Create", ("Input.Name", "page vendor"));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);

        res = await FormHelper.PostFormAsync(c, "/Admin/Models", "/Admin/Models?handler=Create",
            ("Input.TypeId", BuiltIn.Laptop.ToString()), ("Input.VendorId", vendor.Id.ToString()), ("Input.Name", "Page 14"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var model = await _f.WithDbAsync(db => db.Models.SingleAsync(m => m.Name == "Page 14"));
        Assert.Equal((BuiltIn.Laptop, vendor.Id), (model.TypeId, model.VendorId));

        var list = await c.GetStringAsync($"/Admin/Models?type={BuiltIn.Laptop}&vendor={vendor.Id}");
        Assert.Contains("Page 14", list);
        Assert.DoesNotContain("Page 14", await c.GetStringAsync($"/Admin/Models?type={BuiltIn.Desktop}"));

        res = await FormHelper.PostFormAsync(c, $"/Admin/Models?editId={model.Id}", $"/Admin/Models?handler=Update&id={model.Id}",
            ("Input.Name", "Page 14 G2"), ("Input.IsActive", "false"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var updated = await _f.WithDbAsync(db => db.Models.SingleAsync(m => m.Id == model.Id));
        Assert.Equal(("Page 14 G2", false), (updated.Name, updated.IsActive));
    }

    [Fact]
    public async Task Non_admins_cannot_post_to_the_catalog_pages()
    {
        var it = _f.CreateClientAs(TestUser.It);
        foreach (var url in new[] { "/Admin/Types", "/Admin/Vendors?handler=Create", "/Admin/Models?handler=Create" })
            Assert.Equal(HttpStatusCode.Forbidden, (await it.PostAsync(url, new FormUrlEncodedContent([new("Input.Name", "Hacked")]))).StatusCode);
        Assert.False(await _f.WithDbAsync(db => db.Vendors.AnyAsync(v => v.Name == "Hacked")));
    }

    // ---- the asset forms -----------------------------------------------------------------------------------

    [Fact]
    public async Task The_new_asset_form_shows_the_fields_of_the_chosen_type()
    {
        var site = await _f.WithDbAsync(db => Seed.SiteAsync(db, "CP01"));
        var it = _f.CreateClientAs(TestUser.It);

        // A phone: standard fields, vendor and model dropdowns, a phone number and two IMEI inputs.
        var phone = await it.GetStringAsync($"/Assets/Create?typeId={BuiltIn.Phone}&siteId={site.Id}");
        Assert.Contains("Vendor", phone);
        Assert.Contains("data-model-select", phone);
        Assert.Contains($"name=\"Input.Fields[{BuiltIn.PhoneImeiField}]\"", phone);
        Assert.Contains($"id=\"fld-{BuiltIn.PhoneImeiField}-1\"", phone); // the second IMEI
        Assert.DoesNotContain($"id=\"fld-{BuiltIn.PhoneImeiField}-2\"", phone);
        Assert.Contains("Serial number", phone);

        // A phone number: only its own fields; the PUC is marked IT only.
        var line = await it.GetStringAsync($"/Assets/Create?typeId={BuiltIn.PhoneNumber}&siteId={site.Id}");
        foreach (var label in new[] { "Phone number", "Provider", "SIM type", "SIM status", "SIM number", "PUC" })
            Assert.Contains($">{label}", line);
        Assert.Contains("(IT only)", line);
        Assert.DoesNotContain("Serial number", line);
        Assert.DoesNotContain("Vendor", line);
        Assert.DoesNotContain("Condition", line);
        Assert.DoesNotContain("name=\"Input.Description\"", line);
        Assert.Contains("<option value=\"eSIM\"", line);
    }

    [Fact]
    public async Task The_new_asset_form_previews_the_generated_tag_and_creates_the_asset_with_it()
    {
        var site = await _f.WithDbAsync(async db =>
        {
            var s = await Seed.SiteAsync(db, "CP02");
            s.TagCode = "CP2";
            await db.SaveChangesAsync();
            return s;
        });
        var type = await _f.RunAsAsync(TestUser.Admin, sp => sp.GetRequiredService<CatalogService>()
            .CreateTypeAsync(new TypeInput("CP Desktop", InventoryMode.Tagged, TagPattern: "{SITE}D{000}OMA")));
        var it = _f.CreateClientAs(TestUser.It);

        var form = $"/Assets/Create?typeId={type.Id}&siteId={site.Id}";
        var html = await it.GetStringAsync(form);
        Assert.Contains("CP2D001OMA", html);

        var res = await AntiforgeryHelper.PostFormAsync(it, form, new Dictionary<string, string>
        {
            ["Input.Tag"] = "", ["Input.TypeId"] = type.Id.ToString(), ["Input.SiteId"] = site.Id.ToString(),
            ["Input.Description"] = "Front desk",
        }, postUrl: "/Assets/Create");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("CP2D001OMA", (await _f.WithDbAsync(db => db.Assets.SingleAsync(a => a.TypeId == type.Id))).Tag);
        Assert.Contains("CP2D002OMA", await it.GetStringAsync(form));
    }

    [Fact]
    public async Task The_new_asset_form_asks_for_a_tag_when_the_type_has_no_pattern()
    {
        var site = await _f.WithDbAsync(db => Seed.SiteAsync(db, "CP03"));
        var html = await _f.CreateClientAs(TestUser.It).GetStringAsync($"/Assets/Create?typeId={BuiltIn.Laptop}&siteId={site.Id}");
        Assert.Contains("no tag pattern", html);
        Assert.Matches("<input[^>]*name=\"Input.Tag\"[^>]*>", html);
        var tagInput = System.Text.RegularExpressions.Regex.Match(html, "<input[^>]*name=\"Input.Tag\"[^>]*>").Value;
        Assert.Contains("required", tagInput);
    }

    [Fact]
    public async Task Edit_form_changes_model_and_clears_a_field_that_was_emptied()
    {
        var site = await _f.WithDbAsync(db => Seed.SiteAsync(db, "CP04"));
        var (vendor, model) = await _f.RunAsAsync(TestUser.Admin, async sp =>
        {
            var catalog = sp.GetRequiredService<CatalogService>();
            var v = await catalog.CreateVendorAsync("Edit form vendor");
            return (v, await catalog.CreateModelAsync(BuiltIn.Phone, v.Id, "EF1"));
        });
        var phone = await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<AssetTracker.Core.Inventory.AssetService>().CreateAsync(
            new AssetTracker.Core.Inventory.CreateAssetInput("CP-PH-1", BuiltIn.Phone, site.Id,
                Fields: Seed.Fields((BuiltIn.PhonePhoneNumberField, ["+31612340071"])))));
        var it = _f.CreateClientAs(TestUser.It);

        var html = await it.GetStringAsync($"/Assets/Edit/{phone.Id}");
        Assert.Contains("value=\"+31612340071\"", WebUtility.HtmlDecode(html));

        var res = await AntiforgeryHelper.PostFormAsync(it, $"/Assets/Edit/{phone.Id}", new Dictionary<string, string>
        {
            ["Input.ModelId"] = model.Id.ToString(), ["Input.Description"] = "With model",
            [$"Input.Fields[{BuiltIn.PhonePhoneNumberField}]"] = "", // emptied in the form
        });
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var stored = await _f.WithDbAsync(db => db.Assets.Include(a => a.Values).SingleAsync(a => a.Id == phone.Id));
        Assert.Equal((vendor.Id, model.Id, "With model"), (stored.VendorId, stored.ModelId, stored.Description));
        Assert.Empty(stored.Values);
    }

    [Fact]
    public async Task Asset_details_and_list_show_vendor_model_description_and_custom_fields()
    {
        var site = await _f.WithDbAsync(db => Seed.SiteAsync(db, "CP05"));
        var model = await _f.RunAsAsync(TestUser.Admin, async sp =>
        {
            var catalog = sp.GetRequiredService<CatalogService>();
            return await catalog.CreateModelAsync(BuiltIn.Phone, (await catalog.CreateVendorAsync("Details vendor")).Id, "Details 9");
        });
        var phone = await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<AssetTracker.Core.Inventory.AssetService>().CreateAsync(
            new AssetTracker.Core.Inventory.CreateAssetInput("CP-PH-2", BuiltIn.Phone, site.Id, "Sales phone", ModelId: model.Id,
                Fields: Seed.Fields((BuiltIn.PhoneImeiField, [AssetTracker.Infrastructure.Demo.DemoSeeder.DemoImei(900), AssetTracker.Infrastructure.Demo.DemoSeeder.DemoImei(901)])))));
        var line = await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<AssetTracker.Core.Inventory.AssetService>().CreateAsync(
            new AssetTracker.Core.Inventory.CreateAssetInput("CP-PN-1", BuiltIn.PhoneNumber, site.Id,
                Fields: Seed.Fields((BuiltIn.LinePhoneNumberField, ["+31612340072"]), (BuiltIn.LinePucField, ["55501234"])))));

        var it = _f.CreateClientAs(TestUser.It);
        var details = await it.GetStringAsync($"/Assets/Details/{phone.Id}");
        Assert.Contains("Details vendor", details);
        Assert.Contains("Details 9", details);
        Assert.Contains("Sales phone", details);
        Assert.Contains(AssetTracker.Infrastructure.Demo.DemoSeeder.DemoImei(900) + ", " + AssetTracker.Infrastructure.Demo.DemoSeeder.DemoImei(901), details);

        var list = await it.GetStringAsync("/Assets?q=Details+9");
        Assert.Contains("CP-PH-2", list);
        Assert.Contains(AssetTracker.Infrastructure.Demo.DemoSeeder.DemoImei(901), list);

        // The PUC is shown to IT and hidden from a manager of the site.
        Assert.Contains("55501234", WebUtility.HtmlDecode(await it.GetStringAsync($"/Assets/Details/{line.Id}")));
        var manager = new TestUser(@"CORP\mgr.cp05", "S-1-5-21-1000-1000-1000-3405", AppRoles.Manager);
        await _f.WithDbAsync(db => Seed.ManagerWithGrantsAsync(db, manager, site));
        var seen = WebUtility.HtmlDecode(await _f.CreateClientAs(manager).GetStringAsync($"/Assets/Details/{line.Id}"));
        Assert.Contains("+31612340072", seen);
        Assert.DoesNotContain("55501234", seen);
        Assert.DoesNotContain("PUC", seen);
    }

    [Fact]
    public async Task The_pool_form_lists_only_pooled_types()
    {
        var html = await _f.CreateClientAs(TestUser.It).GetStringAsync("/Pools/Create");
        var types = System.Text.RegularExpressions.Regex.Match(html, @"<select[^>]*id=""Input_TypeId""[^>]*>([\s\S]*?)</select>").Groups[1].Value;
        Assert.Contains("Headset", types);
        Assert.Contains("Webcam", types);
        Assert.DoesNotContain("Laptop", types);
        Assert.DoesNotContain("Phone", types);
    }

    // ---- checkout typeahead --------------------------------------------------------------------------------

    private async Task<JsonElement> LookupAsync(HttpClient client, string q)
    {
        var res = await client.GetAsync("/Loans/Checkout?handler=People&q=" + Uri.EscapeDataString(q));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("application/json", res.Content.Headers.ContentType!.MediaType);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task Checkout_people_lookup_suggests_people_with_their_site()
    {
        var (amsterdam, rotterdam) = await _f.WithDbAsync(async db =>
        {
            var a = await Seed.SiteAsync(db, "CK01");
            var r = await Seed.SiteAsync(db, "CK02");
            await Seed.PersonAsync(db, "ckanna.vos", a);
            await Seed.PersonAsync(db, "ckanna.jansen", r);
            await Seed.PersonAsync(db, "ckanna.nosite");
            await Seed.PersonAsync(db, "ckanna.inactive", a, active: false);
            await Seed.PersonAsync(db, "ckanna.noemail", a, email: null);
            return (a, r);
        });
        var it = _f.CreateClientAs(TestUser.It);

        var found = await LookupAsync(it, "ckanna");
        var bySite = found.EnumerateArray().ToDictionary(p => p.GetProperty("username").GetString()!);
        Assert.Equal(["ckanna.jansen", "ckanna.nosite", "ckanna.vos"], bySite.Keys.Order());
        Assert.Equal(("CK01", "Site CK01"), (bySite["ckanna.vos"].GetProperty("siteKey").GetString(), bySite["ckanna.vos"].GetProperty("siteName").GetString()));
        Assert.Equal("CK02", bySite["ckanna.jansen"].GetProperty("siteKey").GetString());
        Assert.Equal(JsonValueKind.Null, bySite["ckanna.nosite"].GetProperty("siteKey").ValueKind);
        Assert.True(bySite["ckanna.vos"].GetProperty("id").GetInt32() > 0);
        Assert.Contains("ckanna.vos@example.test", bySite["ckanna.vos"].GetProperty("email").GetString());
        _ = (amsterdam, rotterdam);

        // Matches by username, display name or email, and narrows while typing.
        Assert.Single((await LookupAsync(it, "ckanna.vos")).EnumerateArray());
        Assert.Single((await LookupAsync(it, "ckanna.jansen@example")).EnumerateArray());
    }

    [Fact]
    public async Task Checkout_people_lookup_needs_two_characters_and_is_for_it_only()
    {
        await _f.WithDbAsync(db => Seed.PersonAsync(db, "cz.lookup"));
        Assert.Equal(0, (await LookupAsync(_f.CreateClientAs(TestUser.It), "c")).GetArrayLength());
        Assert.Equal(0, (await LookupAsync(_f.CreateClientAs(TestUser.It), " ")).GetArrayLength());
        foreach (var user in new[] { TestUser.Manager, TestUser.Admin, TestUser.Nobody })
            Assert.Equal(HttpStatusCode.Forbidden,
                (await _f.CreateClientAs(user).GetAsync("/Loans/Checkout?handler=People&q=cz.lookup")).StatusCode);
    }

    [Fact]
    public async Task Checkout_page_has_the_typeahead_and_shows_each_persons_site()
    {
        var (asset, near, far) = await _f.WithDbAsync(async db =>
        {
            var here = await Seed.SiteAsync(db, "CK03");
            var there = await Seed.SiteAsync(db, "CK04");
            return (await Seed.AssetAsync(db, "CK-LT-1", here), await Seed.PersonAsync(db, "ckpick.near", here), await Seed.PersonAsync(db, "ckpick.far", there));
        });
        var it = _f.CreateClientAs(TestUser.It);

        var start = await it.GetStringAsync("/Loans/Checkout");
        Assert.Contains("data-people-lookup", start);
        Assert.Contains("role=\"combobox\"", start);
        Assert.Contains("name=\"holderId\"", start);

        // Without JavaScript the typed text is still searched; the list names each person's site and flags other sites.
        var searched = await it.GetStringAsync("/Loans/Checkout?tag=CK-LT-1&who=ckpick");
        Assert.Matches(@"ckpick\.near[\s\S]*?badge--info"">CK03", searched);
        Assert.Matches(@"ckpick\.far[\s\S]*?badge--info"">CK04", searched);
        Assert.Contains("Other site than the item (CK03)", searched);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(searched, "Other site than the item"));

        // A person picked in the typeahead arrives as holderId: only they are offered, already selected.
        var picked = await it.GetStringAsync($"/Loans/Checkout?tag=CK-LT-1&who=ckpick.far&holderId={far.Id}");
        Assert.Contains($"value=\"{far.Id}\" required checked", picked);
        Assert.DoesNotContain("ckpick.near", picked);
        _ = (asset, near);
    }

    [Fact]
    public async Task Checkout_with_a_picked_person_completes()
    {
        var (asset, holder) = await _f.WithDbAsync(async db =>
        {
            await AssetTracker.Tests.Notifications.NotificationSeed.SetItMailboxAsync(db, "it-ck@example.test");
            var site = await Seed.SiteAsync(db, "CK05");
            return (await Seed.AssetAsync(db, "CK-LT-2", site), await Seed.PersonAsync(db, "ckdone.holder", site));
        });
        var it = _f.CreateClientAs(TestUser.It);

        var form = $"/Loans/Checkout?tag=CK-LT-2&who=ckdone&holderId={holder.Id}";
        var res = await AntiforgeryHelper.PostFormAsync(it, form, new Dictionary<string, string>
        {
            ["Input.AssetId"] = asset.Id.ToString(), ["Input.HolderId"] = holder.Id.ToString(),
        }, postUrl: "/Loans/Checkout");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal(AssetState.CheckedOut, (await _f.WithDbAsync(db => db.Assets.AsNoTracking().SingleAsync(a => a.Id == asset.Id))).State);
    }
}
