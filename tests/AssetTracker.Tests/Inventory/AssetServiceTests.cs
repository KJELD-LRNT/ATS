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
        var asset = await AsIt(s => s.CreateAsync(new CreateAssetInput("  lt-1001 ", AssetType.Laptop, site.Id, "Laptop model X")));

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
        await AsIt(s => s.CreateAsync(new CreateAssetInput("DT-2001", AssetType.Desktop, site.Id, "Desktop")));
        await Assert.ThrowsAsync<ConflictException>(() =>
            AsIt(s => s.CreateAsync(new CreateAssetInput("dt-2001", AssetType.Desktop, site.Id, "Another"))));
    }

    [Fact]
    public async Task Tag_and_type_are_immutable()
    {
        Assert.Null(typeof(EditAssetInput).GetProperty("Tag"));
        Assert.Null(typeof(EditAssetInput).GetProperty("Type"));

        var site = await SiteAsync("AS03");
        var asset = await AsIt(s => s.CreateAsync(new CreateAssetInput("LT-3001", AssetType.Laptop, site.Id, "Old")));
        var edited = await AsIt(s => s.EditAsync(asset.Id, new EditAssetInput("New model", "Good", "SN-1")));

        Assert.Equal("LT-3001", edited.Tag);
        Assert.Equal(AssetType.Laptop, edited.Type);
        Assert.Equal("New model", edited.Description);
        Assert.Equal("SN-1", edited.SerialNumber);
        Assert.True(await _f.WithDbAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "asset.edit" && e.EntityId == asset.Id.ToString())));
    }

    [Fact]
    public async Task Phone_numbers_are_normalized_and_unique_among_active_assets()
    {
        var site = await SiteAsync("AS04");
        var first = await AsIt(s => s.CreateAsync(new CreateAssetInput("PN-4001", AssetType.PhoneNumber, site.Id, "Mobile line",
            PhoneNumber: "0031 6 1234 0001")));
        Assert.Equal("+31612340001", first.PhoneNumber);

        await Assert.ThrowsAsync<ConflictException>(() => AsIt(s => s.CreateAsync(
            new CreateAssetInput("PN-4002", AssetType.PhoneNumber, site.Id, "Dup line", PhoneNumber: "+31 6-1234-0001"))));

        // Retiring frees the number for reuse.
        await AsIt(s => s.RetireAsync(first.Id, "Line ported out"));
        var reuse = await AsIt(s => s.CreateAsync(
            new CreateAssetInput("PN-4003", AssetType.PhoneNumber, site.Id, "Reused line", PhoneNumber: "+31612340001")));
        Assert.Equal("+31612340001", reuse.PhoneNumber);
    }

    [Fact]
    public async Task Imei_is_validated_normalized_and_unique()
    {
        var site = await SiteAsync("AS05");
        var phone = await AsIt(s => s.CreateAsync(new CreateAssetInput("PH-5001", AssetType.Phone, site.Id, "Phone model",
            Imei: "35-000000-000002-2", PhoneNumber: "+31612340005")));
        Assert.Equal("350000000000022", phone.Imei);

        await Assert.ThrowsAsync<ConflictException>(() => AsIt(s => s.CreateAsync(
            new CreateAssetInput("PH-5002", AssetType.Phone, site.Id, "Phone", Imei: "350000000000022"))));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(s => s.CreateAsync(
            new CreateAssetInput("PH-5003", AssetType.Phone, site.Id, "Phone", Imei: "350000000000023"))));
    }

    [Fact]
    public async Task Identifiers_only_apply_to_matching_types()
    {
        var site = await SiteAsync("AS06");
        await Assert.ThrowsAsync<DomainException>(() => AsIt(s => s.CreateAsync(
            new CreateAssetInput("LT-6001", AssetType.Laptop, site.Id, "Laptop", PhoneNumber: "+31612340006"))));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(s => s.CreateAsync(
            new CreateAssetInput("PN-6002", AssetType.PhoneNumber, site.Id, "Line", Imei: "350000000000030", PhoneNumber: "+31612340007"))));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(s => s.CreateAsync(
            new CreateAssetInput("PN-6003", AssetType.PhoneNumber, site.Id, "Line without number"))));
        Assert.False(await _f.WithDbAsync(db => db.Assets.AnyAsync(a => a.Tag.StartsWith("LT-6") || a.Tag.StartsWith("PN-6"))));
    }

    [Fact]
    public async Task Retirement_is_blocked_by_an_open_loan()
    {
        var site = await SiteAsync("AS07");
        var asset = await AsIt(s => s.CreateAsync(new CreateAssetInput("LT-7001", AssetType.Laptop, site.Id, "Laptop")));
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
        var asset = await AsIt(s => s.CreateAsync(new CreateAssetInput("LT-8001", AssetType.Laptop, site.Id, "Laptop")));
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
        var asset = await AsIt(s => s.CreateAsync(new CreateAssetInput("LT-9001", AssetType.Laptop, site.Id, "Laptop")));

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
        var free = await AsIt(s => s.CreateAsync(new CreateAssetInput("LT-1101", AssetType.Laptop, from.Id, "Laptop")));
        var lent = await AsIt(s => s.CreateAsync(new CreateAssetInput("LT-1102", AssetType.Laptop, from.Id, "Laptop")));
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
        await AsIt(s => s.CreateAsync(new CreateAssetInput("PH-1201", AssetType.Phone, granted.Id, "Searchable phone",
            SerialNumber: "SER-AS12-1", Imei: "350000000000048", PhoneNumber: "+31612341201")));
        var hidden = await AsIt(s => s.CreateAsync(new CreateAssetInput("PH-1301", AssetType.Phone, other.Id, "Searchable phone",
            PhoneNumber: "+31612341301")));

        Assert.Single((await AsIt(s => s.SearchAsync(new AssetSearch("+31 6 1234 1201")))).Items);
        Assert.Single((await AsIt(s => s.SearchAsync(new AssetSearch("ser-as12")))).Items);
        Assert.Single((await AsIt(s => s.SearchAsync(new AssetSearch("0000000000048")))).Items);
        Assert.Equal(2, (await AsIt(s => s.SearchAsync(new AssetSearch("Searchable phone")))).TotalCount);
        Assert.Empty((await AsIt(s => s.SearchAsync(new AssetSearch("100%_")))).Items);
        await AsIt(s => s.CreateAsync(new CreateAssetInput("BR-1201", AssetType.Headset, granted.Id, "Headset [spare]")));
        Assert.Equal("BR-1201", Assert.Single((await AsIt(s => s.SearchAsync(new AssetSearch("[spare]")))).Items).Tag);

        var manager = new TestUser(@"CORP\mgr.as12", "S-1-5-21-1000-1000-1000-3012", TestGroups.Manager);
        await _f.WithDbAsync(db => Seed.ManagerWithGrantsAsync(db, manager, granted));
        var seen = await _f.RunAsAsync(manager, sp => sp.GetRequiredService<AssetService>().SearchAsync(new AssetSearch("Searchable")));
        Assert.Equal("PH-1201", Assert.Single(seen.Items).Tag);
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            _f.RunAsAsync(manager, sp => sp.GetRequiredService<AssetService>().GetAsync(hidden.Id)));
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            _f.RunAsAsync(manager, sp => sp.GetRequiredService<AssetService>().CreateAsync(
                new CreateAssetInput("LT-1202", AssetType.Laptop, granted.Id, "Laptop"))));
    }

    [Fact]
    public async Task Search_is_paged()
    {
        var site = await SiteAsync("AS14");
        for (var i = 1; i <= 5; i++)
        {
            var n = i;
            await AsIt(s => s.CreateAsync(new CreateAssetInput($"PG-14{n:00}", AssetType.Headset, site.Id, "Paged headset")));
        }

        var page2 = await AsIt(s => s.SearchAsync(new AssetSearch(SiteId: site.Id, Page: 2, PageSize: 2)));
        Assert.Equal(5, page2.TotalCount);
        Assert.Equal(3, page2.PageCount);
        Assert.Equal(["PG-1403", "PG-1404"], page2.Items.Select(a => a.Tag));
    }
}
