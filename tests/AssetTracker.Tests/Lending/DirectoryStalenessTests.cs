using System.Text.Json;
using AssetTracker.Core.Domain;
using AssetTracker.Tests.Infrastructure;

namespace AssetTracker.Tests.Lending;

/// <summary>Own fixture: this test controls the sync-run history of its database.</summary>
public class DirectoryStalenessTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public DirectoryStalenessTests(AppFactory f) => _f = f;

    [Fact]
    public async Task Stale_directory_warns_and_is_recorded_but_does_not_block_checkout()
    {
        var site = await _f.SiteAsync("DS01");
        var holder = await _f.PersonAsync("ds01.holder", site);
        var first = await _f.AssetAsync("DS-0001", site);
        var second = await _f.AssetAsync("DS-0002", site);

        // No successful sync at all: stale.
        var stale = await _f.CheckoutTaggedAsync(first, holder);
        Assert.True(stale.DirectoryStale);
        Assert.True((await _f.EventsAsync(stale.Loan.Id)).Single().DirectoryStale);
        var audit = Assert.Single(await _f.AuditAsync("loan.checkout", stale.Loan.Id));
        Assert.True(JsonDocument.Parse(audit.DetailsJson!).RootElement.GetProperty("DirectoryStale").GetBoolean());

        // A recent successful sync: fresh.
        await _f.WithDbAsync(async db =>
        {
            db.SyncRuns.Add(new SyncRun
            {
                StartedAt = _f.Clock.UtcNow.AddMinutes(-10), FinishedAt = _f.Clock.UtcNow.AddMinutes(-9),
                Status = SyncRunStatus.Succeeded, TriggeredBy = "test",
            });
            await db.SaveChangesAsync();
        });
        var fresh = await _f.CheckoutTaggedAsync(second, holder);
        Assert.False(fresh.DirectoryStale);
        Assert.False((await _f.EventsAsync(fresh.Loan.Id)).Single().DirectoryStale);

        // Two days later without another successful sync it is stale again.
        _f.Clock.Advance(TimeSpan.FromDays(2));
        Assert.True(await _f.AsItLending(s => s.IsDirectoryStaleAsync()));
    }
}
