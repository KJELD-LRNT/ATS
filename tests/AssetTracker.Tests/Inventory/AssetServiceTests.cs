using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Inventory;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Inventory;

public class AssetServiceTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public AssetServiceTests(AppFactory f) => _f = f;

    private Task<Site> SiteAsync(string key) => _f.WithDbAsync(db => Seed.SiteAsync(db, key));

    private Task<T> AsIt<T>(Func<AssetService, Task<T>> action) =>
        _f.RunAsAsync(TestUser.It, sp => action(sp.GetRequiredService<AssetService>()));

    private Task AsIt(Func<AssetService, Task> action) =>
        _f.RunAsAsync(TestUser.It, sp => action(sp.GetRequiredService<AssetService>()));

    private Task OpenLoanAsync(int assetId, string username) => _f.WithDbAsync(async db =>
    {
        var person = await Seed.PersonAsync(db, username);
        db.Loans.Add(new Loan
        {
            AssetId = assetId, HolderId = person.Id, Quantity = 1, StartedAt = DateTimeOffset.UtcNow,
            HolderNameSnapshot = person.DisplayName, CreatedBy = "test",
        });
        await db.SaveChangesAsync();
    });

    [Fact]
    public async Task Create_normalizes_tag_and_writes_audit()
    {
        var site = await SiteAsync("AS01");
        var asset = await AsIt(s => s.CreateAsync(new CreateAssetInput("  lt-1001 ", BuiltIn.Laptop, site.Id, "Laptop model X")));

        Assert.Equal("LT-1001", asset.Tag);
        Assert.Equal(AssetState.Available, asset.State);
        var audit = await _f.WithDbAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "asset.create" && e.EntityId == asset.Id.ToString()));
        Assert.Equal(TestUser.It.Name, audit.Actor);
        Assert.Equal(site.Id, audit.SiteId);
    }

    [Fact]
    public async Task Duplicate_tag_is_rejected_case_insensitively()
    {
        var site = await SiteAsync("AS02");
        await AsIt(s => s.CreateAsync(new CreateAssetInput("DT-2001", BuiltIn.Desktop, site.Id, "Desktop")));
        await Assert.ThrowsAsync<ConflictException>(() =>
            AsIt(s => s.CreateAsync(new CreateAssetInput("dt-2001", BuiltIn.Desktop, site.Id, "Another"))));
    }

    [Fact]
    public async Task Tag_and_type_are_immutable()
    {
        Assert.Null(typeof(EditAssetInput).GetProperty("Tag"));
        Assert.Null(typeof(EditAssetInput).GetProperty("Type"));

        var site = await SiteAsync("AS03");
        var asset = await AsIt(s => s.CreateAsync(new CreateAssetInput("LT-3001", BuiltIn.Laptop, site.Id, "Old")));
        var edited = await AsIt(s => s.EditAsync(asset.Id, new EditAssetInput("New model", "Good", "SN-1")));

        Assert.Equal("LT-3001", edited.Tag);
        Assert.Equal(BuiltIn.Laptop, edited.TypeId);
        Assert.Equal("New model", edited.Description);
        Assert.Equal("SN-1", edited.SerialNumber);
        Assert.True(await _f.WithDbAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "asset.edit" && e.EntityId == asset.Id.ToString())));
    }

    [Fact]
    public async Task Phone_numbers_are_normalized_and_unique_among_active_assets()
    {
        var site = await SiteAsync("AS04");
        var first = await AsIt(s => s.CreateAsync(new CreateAssetInput("PN-4001", BuiltIn.PhoneNumber, site.Id,
            Fields: Line("0031 6 1234 0001"))));
        Assert.Equal(["+31612340001"], await ValuesAsync(first.Id, BuiltIn.LinePhoneNumberField));

        await Assert.ThrowsAsync<ConflictException>(() => AsIt(s => s.CreateAsync(
            new CreateAssetInput("PN-4002", BuiltIn.PhoneNumber, site.Id, Fields: Line("+31 6-1234-0001")))));

        // Retiring frees the number for reuse.
        await AsIt(s => s.RetireAsync(first.Id, "Line ported out"));
        var reuse = await AsIt(s => s.CreateAsync(
            new CreateAssetInput("PN-4003", BuiltIn.PhoneNumber, site.Id, Fields: Line("+31612340001"))));
        Assert.Equal(["+31612340001"], await ValuesAsync(reuse.Id, BuiltIn.LinePhoneNumberField));
    }

    /// <summary>Values of the "Phone number" field of the phone-number type, which is required there.</summary>
    private static IReadOnlyDictionary<int, IReadOnlyList<string?>> Line(string number) =>
        Seed.Fields((BuiltIn.LinePhoneNumberField, [number]));

    /// <summary>A valid fictional IMEI. Every test uses its own numbers because the class shares one database.</summary>
    private static string Imei(int n) => AssetTracker.Infrastructure.Demo.DemoSeeder.DemoImei(n);

    private static string BadImei(int n)
    {
        var imei = Imei(n);
        return imei[..14] + (imei[14] - '0' + 1) % 10;
    }

    private static string Dashed(string imei) => $"{imei[..2]}-{imei[2..8]}-{imei[8..14]}-{imei[14]}";

    private Task<List<string>> ValuesAsync(int assetId, int fieldId) => _f.WithDbAsync(db =>
        db.FieldValues.Where(v => v.AssetId == assetId && v.FieldId == fieldId).OrderBy(v => v.Position).Select(v => v.Value).ToListAsync());

    [Fact]
    public async Task Imei_is_validated_normalized_and_unique()
    {
        var site = await SiteAsync("AS05");
        var phone = await AsIt(s => s.CreateAsync(new CreateAssetInput("PH-5001", BuiltIn.Phone, site.Id, "Phone model",
            Fields: Seed.Fields((BuiltIn.PhoneImeiField, [Dashed(Imei(1))]), (BuiltIn.PhonePhoneNumberField, ["+31612340005"])))));
        Assert.Equal([Imei(1)], await ValuesAsync(phone.Id, BuiltIn.PhoneImeiField));

        await Assert.ThrowsAsync<ConflictException>(() => AsIt(s => s.CreateAsync(new CreateAssetInput("PH-5002", BuiltIn.Phone,
            site.Id, "Phone", Fields: Seed.Fields((BuiltIn.PhoneImeiField, [Imei(1)]))))));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(s => s.CreateAsync(new CreateAssetInput("PH-5003", BuiltIn.Phone,
            site.Id, "Phone", Fields: Seed.Fields((BuiltIn.PhoneImeiField, [BadImei(1)]))))));
    }

    [Fact]
    public async Task A_dual_sim_phone_takes_two_different_imeis_in_order()
    {
        var site = await SiteAsync("AS05B");
        var phone = await AsIt(s => s.CreateAsync(new CreateAssetInput("PH-5101", BuiltIn.Phone, site.Id,
            Fields: Seed.Fields((BuiltIn.PhoneImeiField, [Imei(10), "", Imei(11)])))));

        // Blank entries are dropped, the rest keep their order.
        Assert.Equal([Imei(10), Imei(11)], await ValuesAsync(phone.Id, BuiltIn.PhoneImeiField));
        // The second IMEI is as unique as the first.
        await Assert.ThrowsAsync<ConflictException>(() => AsIt(s => s.CreateAsync(new CreateAssetInput("PH-5102", BuiltIn.Phone,
            site.Id, Fields: Seed.Fields((BuiltIn.PhoneImeiField, [Imei(11)]))))));
        // The same IMEI twice, or a third IMEI, is refused.
        await Assert.ThrowsAsync<DomainException>(() => AsIt(s => s.CreateAsync(new CreateAssetInput("PH-5103", BuiltIn.Phone,
            site.Id, Fields: Seed.Fields((BuiltIn.PhoneImeiField, [Imei(12), Imei(12)]))))));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(s => s.CreateAsync(new CreateAssetInput("PH-5104", BuiltIn.Phone,
            site.Id, Fields: Seed.Fields((BuiltIn.PhoneImeiField, [Imei(12), Imei(13), Imei(14)]))))));
    }

    [Fact]
    public async Task Required_fields_and_fields_of_other_types_are_enforced()
    {
        var site = await SiteAsync("AS06");
        // A phone number is required on the phone-number type.
        await Assert.ThrowsAsync<DomainException>(() => AsIt(s => s.CreateAsync(
            new CreateAssetInput("PN-6003", BuiltIn.PhoneNumber, site.Id))));
        // Values for a field that belongs to another type are ignored, not stored on a laptop.
        var laptop = await AsIt(s => s.CreateAsync(new CreateAssetInput("LT-6001", BuiltIn.Laptop, site.Id, "Laptop",
            Fields: Seed.Fields((BuiltIn.PhonePhoneNumberField, ["+31612340006"])))));
        Assert.Empty(await _f.WithDbAsync(db => db.FieldValues.Where(v => v.AssetId == laptop.Id).ToListAsync()));
        Assert.False(await _f.WithDbAsync(db => db.Assets.AnyAsync(a => a.Tag == "PN-6003")));
    }

    [Fact]
    public async Task Choice_fields_only_accept_their_options_and_the_phone_number_type_hides_the_standard_fields()
    {
        var site = await SiteAsync("AS06B");
        await Assert.ThrowsAsync<DomainException>(() => AsIt(s => s.CreateAsync(new CreateAssetInput("PN-6101",
            BuiltIn.PhoneNumber, site.Id, Fields: Seed.Fields(
                (BuiltIn.LinePhoneNumberField, ["+31612340061"]), (BuiltIn.LineSimTypeField, ["Satellite"]))))));

        var line = await AsIt(s => s.CreateAsync(new CreateAssetInput("PN-6102", BuiltIn.PhoneNumber, site.Id,
            Description: "ignored", SerialNumber: "ignored too", Fields: Seed.Fields(
                (BuiltIn.LinePhoneNumberField, ["+31612340062"]), (BuiltIn.LineProviderField, ["Demo Telecom"]),
                (BuiltIn.LineSimTypeField, ["esim"]), (BuiltIn.LineSimStatusField, ["Active"]),
                (BuiltIn.LineSimNumberField, ["8931000000000062"]), (BuiltIn.LinePucField, ["12345678"])))));

        // The phone-number type uses none of the standard fields, so those inputs are dropped.
        Assert.Equal(("", null), (line.Description, line.SerialNumber));
        Assert.Equal(["eSIM"], await ValuesAsync(line.Id, BuiltIn.LineSimTypeField)); // canonical option
        Assert.Equal(["8931000000000062"], await ValuesAsync(line.Id, BuiltIn.LineSimNumberField));
    }

    [Fact]
    public async Task Sensitive_values_are_hidden_from_managers_and_kept_out_of_the_audit_log()
    {
        var site = await SiteAsync("AS06C");
        var line = await AsIt(s => s.CreateAsync(new CreateAssetInput("PN-6201", BuiltIn.PhoneNumber, site.Id,
            Fields: Seed.Fields((BuiltIn.LinePhoneNumberField, ["+31612340063"]), (BuiltIn.LinePucField, ["87654321"])))));

        var asIt = await AsIt(s => s.GetAsync(line.Id));
        Assert.Contains(asIt.Values, v => v.Value == "87654321");

        var manager = new TestUser(@"CORP\mgr.as06c", "S-1-5-21-1000-1000-1000-3066", TestGroups.Manager);
        await _f.WithDbAsync(db => Seed.ManagerWithGrantsAsync(db, manager, site));
        var seen = await _f.RunAsAsync(manager, sp => sp.GetRequiredService<AssetService>().GetAsync(line.Id));
        Assert.DoesNotContain(seen.Values, v => v.Value == "87654321");
        Assert.Contains(seen.Values, v => v.Value == "+31612340063");
        // Searching for a sensitive value finds nothing for a manager (no oracle) but works for IT.
        Assert.Empty((await _f.RunAsAsync(manager, sp => sp.GetRequiredService<AssetService>().SearchAsync(new AssetSearch("87654321")))).Items);
        Assert.Single((await AsIt(s => s.SearchAsync(new AssetSearch("87654321")))).Items);

        var audit = await _f.WithDbAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "asset.create" && e.EntityId == line.Id.ToString()));
        Assert.DoesNotContain("87654321", audit.DetailsJson);
        Assert.Contains("(sensitive)", audit.DetailsJson);
    }

    [Fact]
    public async Task Editing_replaces_changed_values_clears_blank_ones_and_keeps_fields_not_mentioned()
    {
        var site = await SiteAsync("AS06D");
        var phone = await AsIt(s => s.CreateAsync(new CreateAssetInput("PH-6301", BuiltIn.Phone, site.Id, "Phone",
            Fields: Seed.Fields((BuiltIn.PhoneImeiField, [Imei(20), Imei(21)]), (BuiltIn.PhonePhoneNumberField, ["+31612340064"])))));

        // Swapping the two IMEIs must not trip over the unique index; the phone number is not mentioned, so it stays.
        await AsIt(s => s.EditAsync(phone.Id, new EditAssetInput(Fields: Seed.Fields((BuiltIn.PhoneImeiField, [Imei(21), Imei(20)])))));
        Assert.Equal([Imei(21), Imei(20)], await ValuesAsync(phone.Id, BuiltIn.PhoneImeiField));
        Assert.Equal(["+31612340064"], await ValuesAsync(phone.Id, BuiltIn.PhonePhoneNumberField));

        // An explicitly blank field is cleared, which frees its value.
        await AsIt(s => s.EditAsync(phone.Id, new EditAssetInput(Fields: Seed.Fields((BuiltIn.PhoneImeiField, [Imei(21), ""])))));
        Assert.Equal([Imei(21)], await ValuesAsync(phone.Id, BuiltIn.PhoneImeiField));
        var other = await AsIt(s => s.CreateAsync(new CreateAssetInput("PH-6302", BuiltIn.Phone, site.Id,
            Fields: Seed.Fields((BuiltIn.PhoneImeiField, [Imei(20)])))));
        Assert.Equal([Imei(20)], await ValuesAsync(other.Id, BuiltIn.PhoneImeiField));
    }

    [Fact]
    public async Task Retirement_is_blocked_by_an_open_loan()
    {
        var site = await SiteAsync("AS07");
        var asset = await AsIt(s => s.CreateAsync(new CreateAssetInput("LT-7001", BuiltIn.Laptop, site.Id, "Laptop")));
        await OpenLoanAsync(asset.Id, "holder.as07");

        await Assert.ThrowsAnyAsync<DomainException>(() => AsIt(s => s.RetireAsync(asset.Id)));
        var stored = await _f.WithDbAsync(db => db.Assets.AsNoTracking().SingleAsync(a => a.Id == asset.Id));
        Assert.Equal(AssetState.Available, stored.State);
        Assert.Null(stored.RetiredAt);
    }

    [Fact]
    public async Task Retirement_sets_state_and_timestamp_and_blocks_edits()
    {
        var site = await SiteAsync("AS08");
        var asset = await AsIt(s => s.CreateAsync(new CreateAssetInput("LT-8001", BuiltIn.Laptop, site.Id, "Laptop")));
        await AsIt(s => s.RetireAsync(asset.Id, "End of life"));

        var stored = await _f.WithDbAsync(db => db.Assets.AsNoTracking().SingleAsync(a => a.Id == asset.Id));
        Assert.Equal(AssetState.Retired, stored.State);
        Assert.Equal(_f.Clock.UtcNow, stored.RetiredAt);
        var audit = await _f.WithDbAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "asset.retire" && e.EntityId == asset.Id.ToString()));
        Assert.Equal("End of life", audit.Reason);

        await Assert.ThrowsAsync<DomainException>(() => AsIt(s => s.EditAsync(asset.Id, new EditAssetInput("x"))));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(s => s.MarkAvailableAsync(asset.Id)));
    }

    [Fact]
    public async Task Unavailable_and_available_are_explicit_transitions_not_allowed_while_checked_out()
    {
        var site = await SiteAsync("AS09");
        var asset = await AsIt(s => s.CreateAsync(new CreateAssetInput("LT-9001", BuiltIn.Laptop, site.Id, "Laptop")));

        await AsIt(s => s.MarkUnavailableAsync(asset.Id, "Repair"));
        Assert.Equal(AssetState.Unavailable, (await AsIt(s => s.GetAsync(asset.Id))).State);
        await AsIt(s => s.MarkAvailableAsync(asset.Id));
        Assert.Equal(AssetState.Available, (await AsIt(s => s.GetAsync(asset.Id))).State);

        var out1 = await _f.WithDbAsync(db => Seed.AssetAsync(db, "LT-9002", site, state: AssetState.CheckedOut));
        await Assert.ThrowsAnyAsync<DomainException>(() => AsIt(s => s.MarkUnavailableAsync(out1.Id)));
        await Assert.ThrowsAnyAsync<DomainException>(() => AsIt(s => s.MarkAvailableAsync(out1.Id)));
    }

    [Fact]
    public async Task Site_transfer_is_blocked_by_open_loan_and_audited_otherwise()
    {
        var from = await SiteAsync("AS10");
        var to = await SiteAsync("AS11");
        var free = await AsIt(s => s.CreateAsync(new CreateAssetInput("LT-1101", BuiltIn.Laptop, from.Id, "Laptop")));
        var lent = await AsIt(s => s.CreateAsync(new CreateAssetInput("LT-1102", BuiltIn.Laptop, from.Id, "Laptop")));
        await OpenLoanAsync(lent.Id, "holder.as10");

        await Assert.ThrowsAsync<ConflictException>(() => AsIt(s => s.TransferSiteAsync(lent.Id, to.Id)));
        Assert.Equal(from.Id, (await AsIt(s => s.GetAsync(lent.Id))).SiteId);

        await AsIt(s => s.TransferSiteAsync(free.Id, to.Id, "Office move"));
        Assert.Equal(to.Id, (await AsIt(s => s.GetAsync(free.Id))).SiteId);
        var audit = await _f.WithDbAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "asset.transfer_site" && e.EntityId == free.Id.ToString()));
        Assert.Contains($"\"fromSiteId\":{from.Id}", audit.DetailsJson);
        Assert.Contains($"\"toSiteId\":{to.Id}", audit.DetailsJson);
    }

    [Fact]
    public async Task Search_matches_identifiers_and_respects_manager_grants()
    {
        var granted = await SiteAsync("AS12");
        var other = await SiteAsync("AS13");
        await AsIt(s => s.CreateAsync(new CreateAssetInput("PH-1201", BuiltIn.Phone, granted.Id, "Searchable phone",
            SerialNumber: "SER-AS12-1", Fields: Seed.Fields((BuiltIn.PhoneImeiField, ["350000000000048"]),
                (BuiltIn.PhonePhoneNumberField, ["+31612341201"])))));
        var hidden = await AsIt(s => s.CreateAsync(new CreateAssetInput("PH-1301", BuiltIn.Phone, other.Id, "Searchable phone",
            Fields: Seed.Fields((BuiltIn.PhonePhoneNumberField, ["+31612341301"])))));

        Assert.Single((await AsIt(s => s.SearchAsync(new AssetSearch("+31 6 1234 1201")))).Items);
        Assert.Single((await AsIt(s => s.SearchAsync(new AssetSearch("ser-as12")))).Items);
        Assert.Single((await AsIt(s => s.SearchAsync(new AssetSearch("0000000000048")))).Items);
        Assert.Equal(2, (await AsIt(s => s.SearchAsync(new AssetSearch("Searchable phone")))).TotalCount);
        Assert.Empty((await AsIt(s => s.SearchAsync(new AssetSearch("100%_")))).Items);
        await AsIt(s => s.CreateAsync(new CreateAssetInput("BR-1201", BuiltIn.Desktop, granted.Id, "Headset [spare]")));
        Assert.Equal("BR-1201", Assert.Single((await AsIt(s => s.SearchAsync(new AssetSearch("[spare]")))).Items).Tag);

        var manager = new TestUser(@"CORP\mgr.as12", "S-1-5-21-1000-1000-1000-3012", TestGroups.Manager);
        await _f.WithDbAsync(db => Seed.ManagerWithGrantsAsync(db, manager, granted));
        var seen = await _f.RunAsAsync(manager, sp => sp.GetRequiredService<AssetService>().SearchAsync(new AssetSearch("Searchable")));
        Assert.Equal("PH-1201", Assert.Single(seen.Items).Tag);
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            _f.RunAsAsync(manager, sp => sp.GetRequiredService<AssetService>().GetAsync(hidden.Id)));
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            _f.RunAsAsync(manager, sp => sp.GetRequiredService<AssetService>().CreateAsync(
                new CreateAssetInput("LT-1202", BuiltIn.Laptop, granted.Id, "Laptop"))));
    }

    [Fact]
    public async Task Search_is_paged()
    {
        var site = await SiteAsync("AS14");
        for (var i = 1; i <= 5; i++)
        {
            var n = i;
            await AsIt(s => s.CreateAsync(new CreateAssetInput($"PG-14{n:00}", BuiltIn.Desktop, site.Id, "Paged headset")));
        }

        var page2 = await AsIt(s => s.SearchAsync(new AssetSearch(SiteId: site.Id, Page: 2, PageSize: 2)));
        Assert.Equal(5, page2.TotalCount);
        Assert.Equal(3, page2.PageCount);
        Assert.Equal(["PG-1403", "PG-1404"], page2.Items.Select(a => a.Tag));
    }
}
