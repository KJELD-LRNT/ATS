using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Inventory;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Inventory;

public class PoolServiceTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public PoolServiceTests(AppFactory f) => _f = f;

    private Task<Site> SiteAsync(string key) => _f.WithDbAsync(db => Seed.SiteAsync(db, key));

    private Task<T> AsIt<T>(Func<PoolService, Task<T>> action) =>
        _f.RunAsAsync(TestUser.It, sp => action(sp.GetRequiredService<PoolService>()));

    private Task<Pool> ReloadAsync(int id) => _f.WithDbAsync(db => db.Pools.AsNoTracking().SingleAsync(p => p.Id == id));

    private Task<List<StockEvent>> EventsAsync(int poolId) =>
        _f.WithDbAsync(db => db.StockEvents.AsNoTracking().Where(e => e.PoolId == poolId).OrderBy(e => e.Id).ToListAsync());

    [Fact]
    public async Task Create_normalizes_sku_records_initial_stock_and_audits()
    {
        var site = await SiteAsync("PS01");
        var pool = await AsIt(s => s.CreateAsync(new CreatePoolInput(AssetType.Headset, " hs-100 ", site.Id, "Headset", 4)));

        Assert.Equal("HS-100", pool.Sku);
        var stored = await ReloadAsync(pool.Id);
        Assert.Equal(4, stored.Available);
        var ev = Assert.Single(await EventsAsync(pool.Id));
        Assert.Equal(StockEventType.Receipt, ev.Type);
        Assert.Equal(4, ev.AvailableAfter);
        Assert.True(await _f.WithDbAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "pool.create" && e.EntityId == pool.Id.ToString())));
    }

    [Theory]
    [InlineData(AssetType.Laptop)]
    [InlineData(AssetType.Phone)]
    [InlineData(AssetType.PhoneNumber)]
    [InlineData(AssetType.Desktop)]
    public async Task Tagged_only_types_cannot_be_pooled(AssetType type)
    {
        var site = await SiteAsync("PS02" + (int)type);
        await Assert.ThrowsAsync<DomainException>(() =>
            AsIt(s => s.CreateAsync(new CreatePoolInput(type, "SKU-" + type, site.Id, "Not poolable"))));
    }

    [Fact]
    public async Task Sku_is_unique_per_site()
    {
        var a = await SiteAsync("PS03");
        var b = await SiteAsync("PS04");
        await AsIt(s => s.CreateAsync(new CreatePoolInput(AssetType.Webcam, "CAM-1", a.Id, "Webcam")));
        await AsIt(s => s.CreateAsync(new CreatePoolInput(AssetType.Webcam, "CAM-1", b.Id, "Webcam")));
        await Assert.ThrowsAsync<ConflictException>(() =>
            AsIt(s => s.CreateAsync(new CreatePoolInput(AssetType.Webcam, "cam-1", a.Id, "Webcam"))));
    }

    [Fact]
    public async Task Receipt_loss_and_count_write_stock_events_and_audit()
    {
        var site = await SiteAsync("PS05");
        var pool = await _f.WithDbAsync(db => Seed.PoolAsync(db, "HS-PS05", site, 2));

        await AsIt(s => s.ReceiveAsync(pool.Id, 10, "PO 1234"));
        await AsIt(s => s.RecordLossAsync(pool.Id, 3, "Broken in transit"));
        await AsIt(s => s.AdjustCountAsync(pool.Id, 7, "Quarterly count"));

        Assert.Equal(7, (await ReloadAsync(pool.Id)).Available);
        var events = await EventsAsync(pool.Id);
        Assert.Equal([StockEventType.Receipt, StockEventType.Loss, StockEventType.CountAdjustment], events.Select(e => e.Type));
        Assert.Equal([10, -3, -2], events.Select(e => e.QuantityDelta));
        Assert.Equal([12, 9, 7], events.Select(e => e.AvailableAfter));
        Assert.Equal(["PO 1234", "Broken in transit", "Quarterly count"], events.Select(e => e.Reason));
        Assert.All(events, e => Assert.Equal(TestUser.It.Name, e.Actor));

        var actions = await _f.WithDbAsync(db => db.AuditEvents.Where(e => e.EntityType == nameof(Pool) && e.EntityId == pool.Id.ToString())
            .OrderBy(e => e.Id).Select(e => e.Action).ToListAsync());
        Assert.Equal(["pool.receipt", "pool.loss", "pool.count_adjustment"], actions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Adjustments_require_a_reason(string reason)
    {
        var site = await SiteAsync("PS06" + reason.Length);
        var pool = await _f.WithDbAsync(db => Seed.PoolAsync(db, "HS-PS06", site, 5));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(s => s.ReceiveAsync(pool.Id, 1, reason)));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(s => s.RecordLossAsync(pool.Id, 1, reason)));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(s => s.AdjustCountAsync(pool.Id, 1, reason)));
        Assert.Empty(await EventsAsync(pool.Id));
    }

    [Fact]
    public async Task Loss_beyond_available_is_rejected_even_with_outstanding_units()
    {
        var site = await SiteAsync("PS07");
        var pool = await _f.WithDbAsync(async db =>
        {
            var p = await Seed.PoolAsync(db, "HS-PS07", site, 3);
            Assert.True(await PoolBalance.TryTakeAsync(db, p.Id, 2));
            return p;
        });

        await Assert.ThrowsAsync<ConflictException>(() => AsIt(s => s.RecordLossAsync(pool.Id, 2, "Lost")));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(s => s.AdjustCountAsync(pool.Id, -1, "Bad count")));
        var stored = await ReloadAsync(pool.Id);
        Assert.Equal((1, 2), (stored.Available, stored.Outstanding));
        Assert.Empty(await EventsAsync(pool.Id));
    }

    [Fact]
    public async Task Transfer_moves_available_units_with_linked_events()
    {
        var a = await SiteAsync("PS08");
        var b = await SiteAsync("PS09");
        var (src, dst) = await _f.WithDbAsync(async db =>
            (await Seed.PoolAsync(db, "HS-XFER", a, 5), await Seed.PoolAsync(db, "HS-XFER", b, 1)));

        var (outEv, inEv) = await AsIt(s => s.TransferAsync(src.Id, dst.Id, 3, "Rebalance"));

        Assert.Equal(2, (await ReloadAsync(src.Id)).Available);
        Assert.Equal(4, (await ReloadAsync(dst.Id)).Available);
        var o = Assert.Single(await EventsAsync(src.Id));
        var i = Assert.Single(await EventsAsync(dst.Id));
        Assert.Equal((StockEventType.TransferOut, -3, 2), (o.Type, o.QuantityDelta, o.AvailableAfter));
        Assert.Equal((StockEventType.TransferIn, 3, 4), (i.Type, i.QuantityDelta, i.AvailableAfter));
        Assert.Equal(i.Id, o.CounterpartEventId);
        Assert.Equal(o.Id, i.CounterpartEventId);
        Assert.Equal((outEv.Id, inEv.Id), (o.Id, i.Id));
        Assert.Equal(2, await _f.WithDbAsync(db => db.AuditEvents.CountAsync(e => e.Action.StartsWith("pool.transfer") && e.Reason == "Rebalance")));
    }

    [Fact]
    public async Task Transfer_rules_are_enforced()
    {
        var a = await SiteAsync("PS10");
        var b = await SiteAsync("PS11");
        var (src, dst, otherSku) = await _f.WithDbAsync(async db => (
            await Seed.PoolAsync(db, "HS-RULE", a, 2),
            await Seed.PoolAsync(db, "HS-RULE", b, 0),
            await Seed.PoolAsync(db, "HS-OTHER", b, 0)));

        await Assert.ThrowsAsync<ConflictException>(() => AsIt(s => s.TransferAsync(src.Id, dst.Id, 3, "Too many")));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(s => s.TransferAsync(src.Id, otherSku.Id, 1, "Wrong SKU")));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(s => s.TransferAsync(src.Id, src.Id, 1, "Same pool")));
        await Assert.ThrowsAsync<DomainException>(() => AsIt(s => s.TransferAsync(src.Id, dst.Id, 1, " ")));

        Assert.Equal(2, (await ReloadAsync(src.Id)).Available);
        Assert.Equal(0, (await ReloadAsync(dst.Id)).Available);
        Assert.Empty(await EventsAsync(src.Id));
        Assert.Equal(dst.Id, Assert.Single(await AsIt(s => s.GetTransferTargetsAsync(src.Id))).Id);
    }

    [Fact]
    public async Task Concurrent_takes_never_drive_a_pool_negative()
    {
        var site = await SiteAsync("PS12");
        var pool = await _f.WithDbAsync(db => Seed.PoolAsync(db, "HS-RACE", site, 5));

        using var start = new ManualResetEventSlim();
        var attempts = Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            start.Wait();
            using var scope = _f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await PoolBalance.TryTakeAsync(db, pool.Id, 1);
        })).ToList();
        start.Set();
        var results = await Task.WhenAll(attempts);

        Assert.Equal(5, results.Count(ok => ok));
        var stored = await ReloadAsync(pool.Id);
        Assert.Equal((0, 5), (stored.Available, stored.Outstanding));
    }

    [Fact]
    public async Task Concurrent_losses_never_drive_a_pool_negative()
    {
        var site = await SiteAsync("PS13");
        var pool = await _f.WithDbAsync(db => Seed.PoolAsync(db, "HS-RACE2", site, 4));

        var attempts = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            try { await AsIt(s => s.RecordLossAsync(pool.Id, 1, "Concurrent loss")); return true; }
            catch (ConflictException) { return false; }
        })).ToList();
        var results = await Task.WhenAll(attempts);

        Assert.Equal(4, results.Count(ok => ok));
        Assert.Equal(0, (await ReloadAsync(pool.Id)).Available);
        Assert.Equal([3, 2, 1, 0], (await EventsAsync(pool.Id)).Select(e => e.AvailableAfter).OrderDescending());
    }

    [Fact]
    public async Task Return_cannot_exceed_outstanding()
    {
        var site = await SiteAsync("PS14");
        await _f.WithDbAsync(async db =>
        {
            var p = await Seed.PoolAsync(db, "HS-RET", site, 3);
            Assert.True(await PoolBalance.TryTakeAsync(db, p.Id, 2));
            Assert.False(await PoolBalance.TryTakeAsync(db, p.Id, 2));
            await PoolBalance.ReturnAsync(db, p.Id, 1);
            await Assert.ThrowsAsync<ConflictException>(() => PoolBalance.ReturnAsync(db, p.Id, 2));
            Assert.Equal(new PoolBalanceSnapshot(2, 1), await PoolBalance.ReadAsync(db, p.Id));
        });
    }

    [Fact]
    public async Task Manager_sees_only_granted_pools_and_cannot_adjust()
    {
        var granted = await SiteAsync("PS15");
        var other = await SiteAsync("PS16");
        var (mine, theirs) = await _f.WithDbAsync(async db =>
            (await Seed.PoolAsync(db, "HS-MGR", granted, 1), await Seed.PoolAsync(db, "HS-MGR", other, 1)));
        var manager = new TestUser(@"CORP\mgr.ps15", "S-1-5-21-1000-1000-1000-3015", TestGroups.Manager);
        await _f.WithDbAsync(db => Seed.ManagerWithGrantsAsync(db, manager, granted));

        var seen = await _f.RunAsAsync(manager, sp => sp.GetRequiredService<PoolService>().SearchAsync(new PoolSearch("HS-MGR")));
        Assert.Equal(mine.Id, Assert.Single(seen.Items).Id);
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            _f.RunAsAsync(manager, sp => sp.GetRequiredService<PoolService>().GetStockEventsAsync(theirs.Id)));
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            _f.RunAsAsync(manager, sp => sp.GetRequiredService<PoolService>().ReceiveAsync(mine.Id, 1, "Not allowed")));
    }
}
