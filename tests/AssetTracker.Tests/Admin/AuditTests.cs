using System.Net;
using AssetTracker.Core.Auditing;
using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Admin;

public class AuditTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public AuditTests(AppFactory f) => _f = f;

    private Task<AuditEvent> AddAuditAsync(string action, string actor = "CORP\\someone", DateTimeOffset? at = null) =>
        _f.WithDbAsync(async db =>
        {
            var e = new AuditEvent
            {
                OccurredAt = at ?? _f.Clock.UtcNow, Actor = actor, Action = action, EntityType = "Test",
                EntityId = Guid.NewGuid().ToString("N")[..8], CorrelationId = "c",
            };
            db.AuditEvents.Add(e);
            await db.SaveChangesAsync();
            return e;
        });

    [Fact]
    public async Task Audit_page_is_it_only()
    {
        Assert.Equal(HttpStatusCode.OK, (await _f.CreateClientAs(TestUser.It).GetAsync("/Audit")).StatusCode);
        foreach (var user in new[] { TestUser.Admin, TestUser.Manager, TestUser.Nobody })
            Assert.Equal(HttpStatusCode.Forbidden, (await _f.CreateClientAs(user).GetAsync("/Audit")).StatusCode);

        await Assert.ThrowsAsync<AccessDeniedException>(() => _f.RunAsAsync(TestUser.Admin,
            sp => sp.GetRequiredService<AuditQueryService>().SearchAsync(new AuditFilter(), 1)));
    }

    [Fact]
    public async Task Audit_page_filters_by_action_actor_and_date()
    {
        await AddAuditAsync("filtertest.alpha", "CORP\\alice.test", new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero));
        await AddAuditAsync("filtertest.beta", "CORP\\bob.test", new DateTimeOffset(2026, 1, 20, 12, 0, 0, TimeSpan.Zero));

        var c = _f.CreateClientAs(TestUser.It);
        var html = await (await c.GetAsync("/Audit?action=filtertest.alpha")).Content.ReadAsStringAsync();
        Assert.Contains("filtertest.alpha", html);
        Assert.DoesNotContain("filtertest.beta", html);

        html = await (await c.GetAsync("/Audit?action=filtertest.*&actor=CORP%5Cbob.test")).Content.ReadAsStringAsync();
        Assert.Contains("filtertest.beta", html);
        Assert.DoesNotContain("filtertest.alpha", html);

        var page = await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<AuditQueryService>()
            .SearchAsync(new AuditFilter(Action: "filtertest.*", From: new DateOnly(2026, 1, 15), To: new DateOnly(2026, 1, 20)), 1));
        Assert.Equal(["filtertest.beta"], page.Items.Select(i => i.Action));
    }

    [Fact]
    public async Task Audit_page_is_paged()
    {
        for (var i = 0; i < 7; i++) await AddAuditAsync("pagetest.item");
        var p1 = await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<AuditQueryService>()
            .SearchAsync(new AuditFilter(Action: "pagetest.item"), 1, pageSize: 5));
        var p2 = await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<AuditQueryService>()
            .SearchAsync(new AuditFilter(Action: "pagetest.item"), 2, pageSize: 5));
        Assert.Equal((7, 5, 2, 2), (p1.TotalCount, p1.Items.Count, p2.Items.Count, p1.PageCount));
        Assert.True(p1.Items[0].Id > p1.Items[^1].Id);
        Assert.Empty(p1.Items.Select(i => i.Id).Intersect(p2.Items.Select(i => i.Id)));
    }

    [Fact]
    public async Task Audit_events_cannot_be_modified_or_deleted()
    {
        var e = await AddAuditAsync("guard.test");
        await _f.WithDbAsync(async db =>
        {
            var tracked = await db.AuditEvents.SingleAsync(a => a.Id == e.Id);
            tracked.Reason = "tampered";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        });
        await _f.WithDbAsync(async db =>
        {
            db.AuditEvents.Remove(await db.AuditEvents.SingleAsync(a => a.Id == e.Id));
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        });
        var stored = await _f.WithDbAsync(db => db.AuditEvents.AsNoTracking().SingleAsync(a => a.Id == e.Id));
        Assert.Null(stored.Reason);
    }

    [Fact]
    public async Task Stock_events_cannot_be_modified()
    {
        var id = await _f.WithDbAsync(async db =>
        {
            var site = await Seed.SiteAsync(db, "AUD1");
            var pool = await Seed.PoolAsync(db, "AUD-HS", site, 1);
            var se = new StockEvent { PoolId = pool.Id, Type = StockEventType.Receipt, QuantityDelta = 1, AvailableAfter = 1, Reason = "r", Actor = "a", OccurredAt = DateTimeOffset.UtcNow };
            db.StockEvents.Add(se);
            await db.SaveChangesAsync();
            return se.Id;
        });
        await _f.WithDbAsync(async db =>
        {
            var se = await db.StockEvents.SingleAsync(s => s.Id == id);
            se.Reason = "changed";
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        });
    }

    [Fact]
    public async Task Transfer_counterpart_link_can_be_set_once_but_never_changed()
    {
        var (a, b) = await _f.WithDbAsync(async db =>
        {
            var site = await Seed.SiteAsync(db, "AUD2");
            var pool = await Seed.PoolAsync(db, "AUD-LNK", site, 1);
            StockEvent New() => new() { PoolId = pool.Id, Type = StockEventType.TransferOut, QuantityDelta = -1, Reason = "r", Actor = "a", OccurredAt = DateTimeOffset.UtcNow };
            var x = New();
            var y = New();
            db.StockEvents.AddRange(x, y);
            await db.SaveChangesAsync();
            return (x.Id, y.Id);
        });
        await _f.WithDbAsync(async db =>
        {
            var se = await db.StockEvents.SingleAsync(s => s.Id == a);
            se.CounterpartEventId = b;
            await db.SaveChangesAsync();
        });
        await _f.WithDbAsync(async db =>
        {
            var se = await db.StockEvents.SingleAsync(s => s.Id == a);
            se.CounterpartEventId = a;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        });
    }
}
