using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Directory;
using AssetTracker.Core.Domain;
using AssetTracker.Infrastructure.Demo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Demo;

/// <summary>The seeded demo data: expected counts and edge cases, idempotent and deterministic.</summary>
public class DemoSeedAndDirectoryTests : IClassFixture<DemoAppFactory>
{
    private readonly DemoAppFactory _f;

    public DemoSeedAndDirectoryTests(DemoAppFactory f)
    {
        _f = f;
        _f.CreateDemoClient(); // starts the host, which creates and seeds the database
    }

    [Fact]
    public async Task Demo_directory_is_fixed_and_has_the_documented_edge_cases()
    {
        var a = DemoDirectorySource.Entries();
        var b = await new DemoDirectorySource().FetchAllUsersAsync(CancellationToken.None);

        Assert.Equal(a, b);
        Assert.Equal(151, a.Count);
        Assert.Equal(3, a.Count(e => !e.Enabled));
        Assert.Single(a, e => e.Email is null);
        Assert.Equal(2, a.Count(e => e.DirectoryId == "demo-duplicate-0001"));
        Assert.Equal(2, a.Count(e => e.SiteValue is "Utopia Office" or "Remote"));
        Assert.All(a.Where(e => e.Email is not null), e => Assert.EndsWith("@demo.example", e.Email));
        Assert.Contains(a, e => e.Username == DemoDefaults.YouUsername && e.Sid == DemoDefaults.YouSid);
        Assert.Contains(a, e => e.Username == DemoDefaults.ManagerUsername && e.Sid == DemoDefaults.ManagerSid);
        // The historical snapshot differs only in the leaver being enabled.
        var historical = DemoDirectorySource.Entries(historical: true);
        Assert.Equal(2, historical.Count(e => !e.Enabled));
        Assert.True(historical.Single(e => e.Username == DemoDirectorySource.LeaverUsername).Enabled);
    }

    [Fact]
    public async Task Seeded_data_has_the_expected_counts_and_edge_cases()
    {
        await _f.WithDbAsync(async db =>
        {
            Assert.Equal(["AMS", "NYC", "SIN"], await db.Sites.OrderBy(s => s.Key).Select(s => s.Key).ToListAsync());
            Assert.Equal(149, await db.People.CountAsync()); // 151 entries minus the quarantined duplicate pair
            Assert.Equal(146, await db.People.CountAsync(p => p.IsActive));
            Assert.Equal(2, await db.People.CountAsync(p => p.SiteId == null));

            Assert.Equal(42, await db.Assets.CountAsync());
            Assert.Equal(2, await db.Assets.CountAsync(a => a.State == AssetState.Retired));
            Assert.Equal(1, await db.Assets.CountAsync(a => a.State == AssetState.Unavailable));
            Assert.All(await db.Assets.Where(a => a.Imei != null).Select(a => a.Imei!).ToListAsync(),
                imei => Assert.Equal(imei, AssetTracker.Core.Common.Normalization.Imei(imei)));
            Assert.StartsWith("=", (await db.Assets.SingleAsync(a => a.Tag == DemoSeeder.FormulaAssetTag)).Description);
            Assert.Equal(12, await db.Pools.CountAsync());

            Assert.Equal(25, await db.Loans.CountAsync());
            Assert.Equal(20, await db.Loans.CountAsync(l => l.ClosedAt == null));
            Assert.Single(await db.Loans.Where(l => l.ClosedAt == null && l.ReturnedQuantity > 0).ToListAsync());
            Assert.Single(await db.LoanEvents.Where(e => e.Type == LoanEventType.Corrected).ToListAsync());

            // The leaver holds an open loan but is inactive now.
            var leaver = await db.People.SingleAsync(p => p.Username == DemoDirectorySource.LeaverUsername);
            Assert.False(leaver.IsActive);
            Assert.True(await db.Loans.AnyAsync(l => l.HolderId == leaver.Id && l.ClosedAt == null));

            // The asset that moved from NYC to AMS keeps its NYC holder history.
            var moved = await db.Assets.Include(a => a.Site).SingleAsync(a => a.Tag == DemoSeeder.TransferredAssetTag);
            Assert.Equal("AMS", moved.Site!.Key);
            var priorHolder = await db.Loans.Where(l => l.AssetId == moved.Id).Select(l => l.Holder!.Site!.Key).SingleAsync();
            Assert.Equal("NYC", priorHolder);

            // Manager grant: the demo manager → AMS only.
            var grant = await db.ManagerSiteGrants.Include(g => g.Site).SingleAsync();
            Assert.Equal("AMS", grant.Site!.Key);
            Assert.Equal("demo-manager-ams", grant.PrincipalDirectoryId);

            Assert.Equal(DemoDefaults.ItMailbox, (await db.Settings.SingleAsync(s => s.Key == AppSetting.ItMailbox)).Value);
            // Mail for seeded history counts as already sent; nothing is pending from seeding.
            Assert.Equal(0, await db.Outbox.CountAsync(o => o.Status == NotificationStatus.Pending));
            return 0;
        });
    }

    [Fact]
    public async Task Sync_history_reports_quarantined_unmapped_and_disabled_entries()
    {
        await _f.WithDbAsync(async db =>
        {
            var last = await db.SyncRuns.OrderByDescending(r => r.Id).FirstAsync();
            Assert.Equal(SyncRunStatus.Succeeded, last.Status);
            Assert.Equal(151, last.Fetched);
            Assert.Equal(2, last.Quarantined);
            var issues = DirectorySyncService.ParseIssues(last.IssuesJson);
            Assert.Contains(issues, i => i.Code == "duplicate-id" && i.DirectoryId == "demo-duplicate-0001");
            Assert.Equal(2, issues.Count(i => i.Code == "unmapped-site"));
            Assert.Equal(3, await db.People.CountAsync(p => !p.IsActive));
            return 0;
        });
    }

    [Fact]
    public async Task Seeding_again_does_nothing_and_a_reset_reproduces_the_same_data()
    {
        var before = await FingerprintAsync();

        Assert.False(await _f.Services.GetRequiredService<DemoSeeder>().SeedIfEmptyAsync());
        Assert.Equal(before, await FingerprintAsync());

        await _f.Services.GetRequiredService<DemoDataManager>().ResetAsync();
        Assert.Equal(before, await FingerprintAsync());
    }

    /// <summary>Everything except timestamps (which follow the current date).</summary>
    private Task<List<string>> FingerprintAsync() => _f.WithDbAsync(async db =>
    {
        var rows = new List<string>();
        rows.AddRange(await db.Sites.Select(s => $"site {s.Id} {s.Key} {s.DirectoryValue} {s.TimeZoneId}").ToListAsync());
        rows.AddRange(await db.People.Select(p => $"person {p.Id} {p.DirectoryId} {p.Username} {p.Email} {p.SiteId} {p.IsActive}").ToListAsync());
        rows.AddRange(await db.Assets.Select(a => $"asset {a.Id} {a.Tag} {a.Type} {a.SiteId} {a.State} {a.Imei} {a.PhoneNumber} {a.Description}").ToListAsync());
        rows.AddRange(await db.Pools.Select(p => $"pool {p.Id} {p.Sku} {p.SiteId} {p.Available} {p.Outstanding}").ToListAsync());
        rows.AddRange(await db.Loans.Select(l => $"loan {l.Id} {l.AssetId} {l.PoolId} {l.HolderId} {l.Quantity} {l.ReturnedQuantity} {l.DueDate} {l.ClosedAt != null}").ToListAsync());
        rows.AddRange(await db.ManagerSiteGrants.Select(g => $"grant {g.PrincipalDirectoryId} {g.SiteId}").ToListAsync());
        rows.Add($"events {await db.LoanEvents.CountAsync()} stock {await db.StockEvents.CountAsync()} sync {await db.SyncRuns.CountAsync()}");
        rows.Sort(StringComparer.Ordinal);
        return rows;
    });
}
