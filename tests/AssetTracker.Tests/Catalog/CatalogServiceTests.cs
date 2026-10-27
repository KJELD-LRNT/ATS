using AssetTracker.Core.Catalog;
using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Inventory;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Catalog;

/// <summary>Admin-defined asset types, custom fields, vendors and models. Names are unique per test (shared database).</summary>
public class CatalogServiceTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public CatalogServiceTests(AppFactory f) => _f = f;

    private Task<T> AsAdmin<T>(Func<CatalogService, Task<T>> action) =>
        _f.RunAsAsync(TestUser.Admin, sp => action(sp.GetRequiredService<CatalogService>()));

    private Task<T> AsIt<T>(Func<AssetService, Task<T>> action) =>
        _f.RunAsAsync(TestUser.It, sp => action(sp.GetRequiredService<AssetService>()));

    private Task<Site> SiteAsync(string key) => _f.WithDbAsync(db => Seed.SiteAsync(db, key));

    private Task<AssetType> TaggedAsync(string name) => AsAdmin(c => c.CreateTypeAsync(new TypeInput(name, InventoryMode.Tagged)));

    // ---- authorization -------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_admins_change_the_catalog_but_everyone_can_read_it()
    {
        foreach (var user in new[] { TestUser.It, TestUser.Manager, TestUser.Nobody })
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => _f.RunAsAsync(user, sp =>
                sp.GetRequiredService<CatalogService>().CreateTypeAsync(new TypeInput("Nope", InventoryMode.Tagged))));
            await Assert.ThrowsAsync<AccessDeniedException>(() => _f.RunAsAsync(user, sp =>
                sp.GetRequiredService<CatalogService>().CreateVendorAsync("Nope")));
            await Assert.ThrowsAsync<AccessDeniedException>(() => _f.RunAsAsync(user, sp =>
                sp.GetRequiredService<CatalogService>().AddFieldAsync(BuiltIn.Laptop, new FieldInput("Nope", FieldKind.Text))));
            await Assert.ThrowsAsync<AccessDeniedException>(() => _f.RunAsAsync(user, sp =>
                sp.GetRequiredService<CatalogService>().CreateModelAsync(BuiltIn.Laptop, 1, "Nope")));
        }
        var types = await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<CatalogService>().ListTypesAsync());
        Assert.Contains(types, t => t.Name == "Laptop");
    }

    // ---- types ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_default_types_are_there_and_the_pooled_ones_are_pooled()
    {
        var all = await AsAdmin(c => c.ListTypesAsync(includeInactive: true));
        Assert.Equal(["Desktop", "Headset", "Laptop", "Peripheral", "Phone", "Phone number", "Webcam"],
            all.Where(t => t.Id <= BuiltIn.Headset).Select(t => t.Name).Order());
        Assert.All(all.Where(t => t.Id is BuiltIn.Peripheral or BuiltIn.Webcam or BuiltIn.Headset), t => Assert.Equal(InventoryMode.Pooled, t.Mode));

        // The phone number type asks for the number, provider, SIM type/status/number and the PUC, and nothing else.
        var line = await AsAdmin(c => c.GetTypeAsync(BuiltIn.PhoneNumber));
        Assert.Equal(["Phone number", "Provider", "SIM type", "SIM status", "SIM number", "PUC"], line.Fields.Select(f => f.Name));
        Assert.False(line.UsesModel || line.UsesDescription || line.UsesSerialNumber || line.UsesCondition);
        Assert.True(line.Fields.Single(f => f.Name == "PUC").IsSensitive);
        // A phone has up to two IMEIs.
        Assert.Equal(2, (await AsAdmin(c => c.GetTypeAsync(BuiltIn.Phone))).Fields.Single(f => f.Kind == FieldKind.Imei).MaxValues);
    }

    [Fact]
    public async Task Admin_creates_and_edits_a_type_with_audit()
    {
        var type = await AsAdmin(c => c.CreateTypeAsync(new TypeInput("  Tablet ", InventoryMode.Tagged, UsesModel: true,
            UsesSerialNumber: true, UsesDescription: false, UsesCondition: false, TagPattern: "{SITE}T{000}")));
        Assert.Equal("Tablet", type.Name);
        Assert.Equal("{SITE}T{000}", type.TagPattern);
        Assert.False(type.UsesDescription);

        var edited = await AsAdmin(c => c.UpdateTypeAsync(type.Id, new TypeInput("Tablet pro", InventoryMode.Tagged, TagPattern: "TAB{000}", IsActive: false)));
        Assert.Equal(("Tablet pro", "TAB{000}", false), (edited.Name, edited.TagPattern, edited.IsActive));

        var actions = await _f.WithDbAsync(db => db.AuditEvents.Where(e => e.EntityType == nameof(AssetType) && e.EntityId == type.Id.ToString())
            .OrderBy(e => e.Id).Select(e => e.Action).ToListAsync());
        Assert.Equal(["type.create", "type.update"], actions);
    }

    [Fact]
    public async Task Type_names_are_unique_ignoring_case_and_patterns_are_validated()
    {
        await TaggedAsync("Docking station");
        await Assert.ThrowsAsync<ConflictException>(() => TaggedAsync("docking STATION"));
        await Assert.ThrowsAsync<DomainException>(() => AsAdmin(c => c.CreateTypeAsync(new TypeInput("", InventoryMode.Tagged))));
        await Assert.ThrowsAsync<DomainException>(() => AsAdmin(c => c.CreateTypeAsync(
            new TypeInput("Bad pattern", InventoryMode.Tagged, TagPattern: "NO-NUMBER"))));
        Assert.False(await _f.WithDbAsync(db => db.AssetTypes.AnyAsync(t => t.Name == "Bad pattern")));
    }

    [Fact]
    public async Task A_pooled_type_has_no_tag_pattern_standard_fields_custom_fields_or_models()
    {
        var pooled = await AsAdmin(c => c.CreateTypeAsync(new TypeInput("Cable", InventoryMode.Pooled, TagPattern: "CAB{000}")));
        Assert.Null(pooled.TagPattern);
        Assert.False(pooled.UsesModel || pooled.UsesDescription || pooled.UsesSerialNumber || pooled.UsesCondition);

        await Assert.ThrowsAsync<DomainException>(() => AsAdmin(c => c.AddFieldAsync(pooled.Id, new FieldInput("Length", FieldKind.Number))));
        var vendor = await AsAdmin(c => c.CreateVendorAsync("Cable vendor"));
        await Assert.ThrowsAsync<DomainException>(() => AsAdmin(c => c.CreateModelAsync(pooled.Id, vendor.Id, "Cat6")));

        // Pools can be made for it, assets cannot, and tagged types cannot be pooled.
        var site = await SiteAsync("CT01");
        var pool = await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<PoolService>().CreateAsync(new CreatePoolInput(pooled.Id, "CAB-1", site.Id, "Cable", 5)));
        Assert.Equal(pooled.Id, pool.TypeId);
        var ex = await Assert.ThrowsAsync<DomainException>(() => AsIt(s => s.CreateAsync(new CreateAssetInput("CAB-X", pooled.Id, site.Id))));
        Assert.Equal("asset.type_pooled", ex.Code);
    }

    [Fact]
    public async Task How_a_type_is_tracked_cannot_change_once_it_is_in_use()
    {
        var site = await SiteAsync("CT02");
        var free = await TaggedAsync("Unused thing");
        var changed = await AsAdmin(c => c.UpdateTypeAsync(free.Id, new TypeInput(free.Name, InventoryMode.Pooled)));
        Assert.Equal(InventoryMode.Pooled, changed.Mode);

        var used = await TaggedAsync("Used thing");
        await AsIt(s => s.CreateAsync(new CreateAssetInput("USED-1", used.Id, site.Id)));
        var ex = await Assert.ThrowsAsync<DomainException>(() => AsAdmin(c => c.UpdateTypeAsync(used.Id, new TypeInput(used.Name, InventoryMode.Pooled))));
        Assert.Equal("type.mode_locked", ex.Code);
    }

    [Fact]
    public async Task An_inactive_type_cannot_get_new_assets_but_its_assets_stay()
    {
        var site = await SiteAsync("CT03");
        var type = await TaggedAsync("Retiring type");
        var asset = await AsIt(s => s.CreateAsync(new CreateAssetInput("RT-1", type.Id, site.Id)));
        await AsAdmin(c => c.UpdateTypeAsync(type.Id, new TypeInput(type.Name, type.Mode, IsActive: false)));

        var ex = await Assert.ThrowsAsync<DomainException>(() => AsIt(s => s.CreateAsync(new CreateAssetInput("RT-2", type.Id, site.Id))));
        Assert.Equal("asset.type_inactive", ex.Code);
        Assert.Equal("RT-1", (await AsIt(s => s.GetAsync(asset.Id))).Tag);
        Assert.DoesNotContain(await AsAdmin(c => c.ListTypesAsync()), t => t.Id == type.Id);
        Assert.Contains(await AsAdmin(c => c.ListTypesAsync(includeInactive: true)), t => t.Id == type.Id);
    }

    // ---- custom fields -------------------------------------------------------------------------------------

    [Fact]
    public async Task Fields_are_validated_when_defined()
    {
        var type = await TaggedAsync("Field rules");
        await Assert.ThrowsAsync<DomainException>(() => AsAdmin(c => c.AddFieldAsync(type.Id, new FieldInput("", FieldKind.Text))));
        await Assert.ThrowsAsync<DomainException>(() => AsAdmin(c => c.AddFieldAsync(type.Id, new FieldInput("Choice", FieldKind.Choice, Options: "  \n "))));
        await Assert.ThrowsAsync<DomainException>(() => AsAdmin(c => c.AddFieldAsync(type.Id, new FieldInput("Many", FieldKind.Text, MaxValues: 6))));
        await Assert.ThrowsAsync<DomainException>(() => AsAdmin(c => c.AddFieldAsync(type.Id, new FieldInput("Dated", FieldKind.Date, IsUnique: true))));

        await AsAdmin(c => c.AddFieldAsync(type.Id, new FieldInput("Colour", FieldKind.Choice, Options: "Red\nGreen\nred\n Blue ")));
        await Assert.ThrowsAsync<ConflictException>(() => AsAdmin(c => c.AddFieldAsync(type.Id, new FieldInput("colour", FieldKind.Text))));

        var stored = (await AsAdmin(c => c.GetTypeAsync(type.Id))).Fields.Single();
        Assert.Equal("Red\nGreen\nBlue", stored.Options); // trimmed, case-insensitive duplicates dropped
    }

    [Fact]
    public async Task Field_kind_uniqueness_and_value_count_are_locked_once_assets_have_values()
    {
        var site = await SiteAsync("CT04");
        var type = await TaggedAsync("Locked fields");
        var field = await AsAdmin(c => c.AddFieldAsync(type.Id, new FieldInput("Asset code", FieldKind.Text, MaxValues: 2)));
        await AsAdmin(c => c.UpdateFieldAsync(field.Id, new FieldInput("Asset code", FieldKind.Number, IsUnique: true, MaxValues: 2))); // free while unused
        await AsIt(s => s.CreateAsync(new CreateAssetInput("LF-1", type.Id, site.Id, Fields: Seed.Fields((field.Id, ["42"])))));

        Assert.Equal("field.kind_locked", (await Assert.ThrowsAsync<DomainException>(() => AsAdmin(c =>
            c.UpdateFieldAsync(field.Id, new FieldInput("Asset code", FieldKind.Text, IsUnique: true, MaxValues: 2))))).Code);
        Assert.Equal("field.unique_locked", (await Assert.ThrowsAsync<DomainException>(() => AsAdmin(c =>
            c.UpdateFieldAsync(field.Id, new FieldInput("Asset code", FieldKind.Number, IsUnique: false, MaxValues: 2))))).Code);
        Assert.Equal("field.max_values_locked", (await Assert.ThrowsAsync<DomainException>(() => AsAdmin(c =>
            c.UpdateFieldAsync(field.Id, new FieldInput("Asset code", FieldKind.Number, IsUnique: true, MaxValues: 1))))).Code);
        // Renaming, requiring, hiding and allowing more values stay possible.
        var ok = await AsAdmin(c => c.UpdateFieldAsync(field.Id, new FieldInput("Inventory code", FieldKind.Number, IsRequired: true, IsUnique: true, MaxValues: 3)));
        Assert.Equal(("Inventory code", 3), (ok.Name, ok.MaxValues));
    }

    [Fact]
    public async Task Fields_can_be_reordered_and_deactivated_fields_leave_the_form()
    {
        var site = await SiteAsync("CT05");
        var type = await TaggedAsync("Ordered fields");
        var a = await AsAdmin(c => c.AddFieldAsync(type.Id, new FieldInput("A", FieldKind.Text)));
        var b = await AsAdmin(c => c.AddFieldAsync(type.Id, new FieldInput("B", FieldKind.Text, IsRequired: true)));
        var c3 = await AsAdmin(c => c.AddFieldAsync(type.Id, new FieldInput("C", FieldKind.Text)));

        await AsAdmin(async c => { await c.MoveFieldAsync(c3.Id, up: true); return 0; });
        Assert.Equal(["A", "C", "B"], (await AsAdmin(c => c.GetTypeAsync(type.Id))).Fields.Select(f => f.Name));
        await AsAdmin(async c => { await c.MoveFieldAsync(a.Id, up: true); return 0; }); // already first: nothing happens
        Assert.Equal(["A", "C", "B"], (await AsAdmin(c => c.GetTypeAsync(type.Id))).Fields.Select(f => f.Name));

        // A required field that is deactivated is no longer asked for.
        await Assert.ThrowsAsync<DomainException>(() => AsIt(s => s.CreateAsync(new CreateAssetInput("OF-1", type.Id, site.Id))));
        await AsAdmin(c => c.UpdateFieldAsync(b.Id, new FieldInput("B", FieldKind.Text, IsRequired: true, IsActive: false)));
        var asset = await AsIt(s => s.CreateAsync(new CreateAssetInput("OF-1", type.Id, site.Id)));
        Assert.Equal("OF-1", asset.Tag);
    }

    [Fact]
    public async Task Field_values_follow_the_kind_of_the_field()
    {
        var site = await SiteAsync("CT06");
        var type = await TaggedAsync("Kinds");
        var number = await AsAdmin(c => c.AddFieldAsync(type.Id, new FieldInput("Memory GB", FieldKind.Number)));
        var date = await AsAdmin(c => c.AddFieldAsync(type.Id, new FieldInput("Warranty until", FieldKind.Date)));
        var text = await AsAdmin(c => c.AddFieldAsync(type.Id, new FieldInput("Licence", FieldKind.Text, IsUnique: true)));

        var asset = await AsIt(s => s.CreateAsync(new CreateAssetInput("KD-1", type.Id, site.Id, Fields: Seed.Fields(
            (number.Id, ["16,5"]), (date.Id, ["2027-03-01"]), (text.Id, ["  abc-1 "])))));
        var values = await _f.WithDbAsync(db => db.FieldValues.Where(v => v.AssetId == asset.Id).ToDictionaryAsync(v => v.FieldId, v => v.Value));
        Assert.Equal(("16.5", "2027-03-01", "abc-1"), (values[number.Id], values[date.Id], values[text.Id]));

        await Assert.ThrowsAsync<DomainException>(() => AsIt(s => s.CreateAsync(new CreateAssetInput("KD-2", type.Id, site.Id,
            Fields: Seed.Fields((number.Id, ["lots"]))))));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(s => s.CreateAsync(new CreateAssetInput("KD-3", type.Id, site.Id,
            Fields: Seed.Fields((date.Id, ["01-03-2027"]))))));
        // Unique text fields compare case-insensitively.
        await Assert.ThrowsAsync<ConflictException>(() => AsIt(s => s.CreateAsync(new CreateAssetInput("KD-4", type.Id, site.Id,
            Fields: Seed.Fields((text.Id, ["ABC-1"]))))));
    }

    [Fact]
    public async Task A_removed_choice_option_stays_valid_for_assets_that_have_it()
    {
        var site = await SiteAsync("CT07");
        var type = await TaggedAsync("Choice drift");
        var status = await AsAdmin(c => c.AddFieldAsync(type.Id, new FieldInput("Status", FieldKind.Choice, Options: "Old\nNew")));
        var asset = await AsIt(s => s.CreateAsync(new CreateAssetInput("CD-1", type.Id, site.Id, Fields: Seed.Fields((status.Id, ["Old"])))));

        await AsAdmin(c => c.UpdateFieldAsync(status.Id, new FieldInput("Status", FieldKind.Choice, Options: "New\nNewer")));
        // Saving the asset again with its old value works; picking the removed option for another asset does not.
        await AsIt(s => s.EditAsync(asset.Id, new EditAssetInput(Condition: "ok", Fields: Seed.Fields((status.Id, ["Old"])))));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(s => s.CreateAsync(new CreateAssetInput("CD-2", type.Id, site.Id,
            Fields: Seed.Fields((status.Id, ["Old"]))))));
    }

    // ---- vendors and models --------------------------------------------------------------------------------

    [Fact]
    public async Task Vendors_are_unique_renamable_and_can_be_deactivated()
    {
        var vendor = await AsAdmin(c => c.CreateVendorAsync("  Vendor One "));
        Assert.Equal("Vendor One", vendor.Name);
        await Assert.ThrowsAsync<ConflictException>(() => AsAdmin(c => c.CreateVendorAsync("vendor ONE")));
        await Assert.ThrowsAsync<DomainException>(() => AsAdmin(c => c.CreateVendorAsync(" ")));

        await AsAdmin(c => c.UpdateVendorAsync(vendor.Id, "Vendor Uno", isActive: false));
        Assert.DoesNotContain(await AsAdmin(c => c.ListVendorsAsync()), v => v.Id == vendor.Id);
        Assert.Contains(await AsAdmin(c => c.ListVendorsAsync(includeInactive: true)), v => v.Name == "Vendor Uno");
    }

    [Fact]
    public async Task Models_belong_to_a_vendor_and_type_and_are_unique_within_them()
    {
        var vendor = await AsAdmin(c => c.CreateVendorAsync("Model vendor"));
        var laptop = await AsAdmin(c => c.CreateModelAsync(BuiltIn.Laptop, vendor.Id, "Pro 14"));
        Assert.Equal("Model vendor Pro 14", laptop.DisplayName);

        await Assert.ThrowsAsync<ConflictException>(() => AsAdmin(c => c.CreateModelAsync(BuiltIn.Laptop, vendor.Id, "pro 14")));
        // The same name for another type is a different model.
        await AsAdmin(c => c.CreateModelAsync(BuiltIn.Desktop, vendor.Id, "Pro 14"));

        var forLaptops = await AsAdmin(c => c.ListModelsAsync(typeId: BuiltIn.Laptop, vendorId: vendor.Id));
        Assert.Equal(["Pro 14"], forLaptops.Select(m => m.Name));

        await AsAdmin(c => c.UpdateModelAsync(laptop.Id, "Pro 14 G2", isActive: false));
        Assert.Empty(await AsAdmin(c => c.ListModelsAsync(typeId: BuiltIn.Laptop, vendorId: vendor.Id)));
        Assert.Equal(["Pro 14 G2"], (await AsAdmin(c => c.ListModelsAsync(typeId: BuiltIn.Laptop, vendorId: vendor.Id, includeInactive: true))).Select(m => m.Name));
        // An inactive vendor hides its models from the dropdown as well.
        var other = await AsAdmin(c => c.CreateVendorAsync("Hidden model vendor"));
        await AsAdmin(c => c.CreateModelAsync(BuiltIn.Laptop, other.Id, "X1"));
        await AsAdmin(c => c.UpdateVendorAsync(other.Id, other.Name, isActive: false));
        Assert.Empty(await AsAdmin(c => c.ListModelsAsync(typeId: BuiltIn.Laptop, vendorId: other.Id)));
    }

    [Fact]
    public async Task An_asset_gets_its_vendor_from_the_model_and_only_accepts_models_of_its_type()
    {
        var site = await SiteAsync("CT08");
        var vendor = await AsAdmin(c => c.CreateVendorAsync("Asset vendor"));
        var other = await AsAdmin(c => c.CreateVendorAsync("Asset other vendor"));
        var laptopModel = await AsAdmin(c => c.CreateModelAsync(BuiltIn.Laptop, vendor.Id, "L1"));
        var desktopModel = await AsAdmin(c => c.CreateModelAsync(BuiltIn.Desktop, vendor.Id, "D1"));

        var asset = await AsIt(s => s.CreateAsync(new CreateAssetInput("AM-1", BuiltIn.Laptop, site.Id, "Notes", ModelId: laptopModel.Id)));
        Assert.Equal((vendor.Id, laptopModel.Id), (asset.VendorId, asset.ModelId));
        var loaded = await AsIt(s => s.GetAsync(asset.Id));
        Assert.Equal("Asset vendor L1", loaded.Title);
        Assert.Equal("Notes", loaded.Description); // description and model are separate properties

        // Wrong type, or a vendor that does not match the model.
        Assert.Equal("asset.model_type", (await Assert.ThrowsAsync<DomainException>(() => AsIt(s =>
            s.CreateAsync(new CreateAssetInput("AM-2", BuiltIn.Laptop, site.Id, ModelId: desktopModel.Id))))).Code);
        Assert.Equal("asset.model_vendor", (await Assert.ThrowsAsync<DomainException>(() => AsIt(s =>
            s.CreateAsync(new CreateAssetInput("AM-3", BuiltIn.Laptop, site.Id, VendorId: other.Id, ModelId: laptopModel.Id))))).Code);

        // A vendor without a model is fine; the model can be added later by editing.
        var vendorOnly = await AsIt(s => s.CreateAsync(new CreateAssetInput("AM-4", BuiltIn.Laptop, site.Id, VendorId: vendor.Id)));
        Assert.Equal((vendor.Id, (int?)null), (vendorOnly.VendorId, vendorOnly.ModelId));
        var edited = await AsIt(s => s.EditAsync(vendorOnly.Id, new EditAssetInput(VendorId: vendor.Id, ModelId: laptopModel.Id)));
        Assert.Equal(laptopModel.Id, edited.ModelId);
    }

    [Fact]
    public async Task An_inactive_model_cannot_be_chosen_again_but_stays_on_assets_that_have_it()
    {
        var site = await SiteAsync("CT09");
        var vendor = await AsAdmin(c => c.CreateVendorAsync("Fading vendor"));
        var model = await AsAdmin(c => c.CreateModelAsync(BuiltIn.Laptop, vendor.Id, "Old model"));
        var asset = await AsIt(s => s.CreateAsync(new CreateAssetInput("IM-1", BuiltIn.Laptop, site.Id, ModelId: model.Id)));
        await AsAdmin(c => c.UpdateModelAsync(model.Id, model.Name, isActive: false));

        Assert.Equal("asset.model_inactive", (await Assert.ThrowsAsync<DomainException>(() => AsIt(s =>
            s.CreateAsync(new CreateAssetInput("IM-2", BuiltIn.Laptop, site.Id, ModelId: model.Id))))).Code);
        // Editing the asset keeps its (now inactive) model.
        var edited = await AsIt(s => s.EditAsync(asset.Id, new EditAssetInput(Condition: "fine", ModelId: model.Id)));
        Assert.Equal(model.Id, edited.ModelId);
    }

    [Fact]
    public async Task A_type_that_does_not_use_models_ignores_them()
    {
        var site = await SiteAsync("CT10");
        var vendor = await AsAdmin(c => c.CreateVendorAsync("Ignored vendor"));
        var model = await AsAdmin(c => c.CreateModelAsync(BuiltIn.PhoneNumber, vendor.Id, "Line"));
        var line = await AsIt(s => s.CreateAsync(new CreateAssetInput("IG-1", BuiltIn.PhoneNumber, site.Id, ModelId: model.Id,
            Fields: Seed.Fields((BuiltIn.LinePhoneNumberField, ["+31612340099"])))));
        Assert.Null(line.ModelId);
        Assert.Null(line.VendorId);
    }

    [Fact]
    public async Task Catalog_changes_are_audited()
    {
        var vendor = await AsAdmin(c => c.CreateVendorAsync("Audited vendor"));
        var model = await AsAdmin(c => c.CreateModelAsync(BuiltIn.Laptop, vendor.Id, "Audited model"));
        var type = await TaggedAsync("Audited type");
        var field = await AsAdmin(c => c.AddFieldAsync(type.Id, new FieldInput("Audited field", FieldKind.Text)));
        await AsAdmin(c => c.UpdateFieldAsync(field.Id, new FieldInput("Audited field", FieldKind.Text, IsRequired: true)));

        var actions = await _f.WithDbAsync(db => db.AuditEvents
            .Where(e => (e.EntityType == nameof(Vendor) && e.EntityId == vendor.Id.ToString()) ||
                        (e.EntityType == nameof(AssetModel) && e.EntityId == model.Id.ToString()) ||
                        (e.EntityType == nameof(AssetFieldDefinition) && e.EntityId == field.Id.ToString()))
            .OrderBy(e => e.Id).Select(e => e.Action).ToListAsync());
        Assert.Equal(["vendor.create", "model.create", "field.create", "field.update"], actions);
    }
}
