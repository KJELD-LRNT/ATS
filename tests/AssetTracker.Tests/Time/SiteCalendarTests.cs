using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Time;
using AssetTracker.Tests.Infrastructure;
using AssetTracker.Tests.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AssetTracker.Tests.Time;

/// <summary>Per-site calendar (WP4.2): site timezone, default timezone fallback and the loan's site.</summary>
public class SiteCalendarTests : IClassFixture<AppFactory>
{
    private static readonly DateTimeOffset Instant = new(2026, 3, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly AppFactory _f;
    public SiteCalendarTests(AppFactory f) => _f = f;

    private sealed class FixedOptions(string timeZoneId) : IOptionsMonitor<ClockOptions>
    {
        public ClockOptions CurrentValue { get; } = new() { TimeZoneId = timeZoneId };
        public ClockOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<ClockOptions, string?> listener) => null;
    }

    private async Task<T> WithCalendarAsync<T>(string defaultZone, Func<SiteCalendar, AppDbContext, Task<T>> action)
    {
        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var clock = new FakeClock { UtcNow = Instant };
        return await action(new SiteCalendar(db, clock, new FixedOptions(defaultZone)), db);
    }

    [Fact]
    public async Task Site_timezone_overrides_the_default_and_empty_uses_the_default()
    {
        var (own, plain) = await _f.WithDbAsync(async db =>
            (await Seed.SiteAsync(db, "SC1A", timeZoneId: "Pacific/Kiritimati"), await Seed.SiteAsync(db, "SC1B")));

        var (ownToday, plainToday, nyToday, ownTime) = await WithCalendarAsync("America/New_York", async (c, _) =>
            (await c.TodayForAsync(own.Id), await c.TodayForAsync(plain.Id), c.DefaultToday, await c.LocalTimeForAsync(own.Id)));

        Assert.Equal(new DateOnly(2026, 3, 3), ownToday); // UTC+14: 02:00 on the 3rd
        Assert.Equal(new DateOnly(2026, 3, 2), plainToday); // default New York: 07:00 on the 2nd
        Assert.Equal(new DateOnly(2026, 3, 2), nyToday);
        Assert.Equal(new TimeOnly(2, 0), TimeOnly.FromDateTime(ownTime.DateTime));
    }

    [Fact]
    public async Task Missing_default_timezone_fails_clearly_but_sites_with_their_own_zone_still_work()
    {
        var (own, plain) = await _f.WithDbAsync(async db =>
            (await Seed.SiteAsync(db, "SC2A", timeZoneId: "W. Europe Standard Time"), await Seed.SiteAsync(db, "SC2B")));

        var ownToday = await WithCalendarAsync("", (c, _) => c.TodayForAsync(own.Id));
        Assert.Equal(new DateOnly(2026, 3, 2), ownToday);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WithCalendarAsync("", (c, _) => c.TodayForAsync(plain.Id)));
        Assert.Contains("Clock:TimeZoneId is not configured", ex.Message);

        ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WithCalendarAsync("Not/AZone", (c, _) => Task.FromResult(c.DefaultToday)));
        Assert.Contains("Not/AZone", ex.Message);
    }

    [Fact]
    public async Task Tagged_loan_uses_the_assets_current_site_and_pool_loan_the_pools_site()
    {
        var (tagged, pooled, kiritimati) = await _f.WithDbAsync(async db =>
        {
            var utc = await Seed.SiteAsync(db, "SC3U");
            var k = await Seed.SiteAsync(db, "SC3K", timeZoneId: "Pacific/Kiritimati");
            var holder = await Seed.PersonAsync(db, "sc3.holder", utc);
            var t = await NotificationSeed.TaggedLoanAsync(db, utc, holder, "SC3-T", new DateOnly(2026, 3, 3));
            var p = await NotificationSeed.PoolLoanAsync(db, k, holder, "SC3-P", 2, 0, new DateOnly(2026, 3, 3));
            return (t, p, k);
        });

        Assert.Equal(new DateOnly(2026, 3, 2), await WithCalendarAsync("UTC", (c, _) => c.TodayForAsync(new Loan { AssetId = tagged.AssetId })));
        Assert.Equal(new DateOnly(2026, 3, 3), await WithCalendarAsync("UTC", (c, _) => c.TodayForAsync(new Loan { PoolId = pooled.PoolId })));

        // Moving the asset moves the loan's calendar with it.
        await _f.WithDbAsync(async db =>
        {
            var asset = await db.Assets.FindAsync(tagged.AssetId!.Value);
            asset!.SiteId = kiritimati.Id;
            await db.SaveChangesAsync();
        });
        Assert.Equal(new DateOnly(2026, 3, 3), await WithCalendarAsync("UTC", (c, _) => c.TodayForAsync(new Loan { AssetId = tagged.AssetId })));
    }

    [Theory]
    [InlineData("Europe/Amsterdam", true)]
    [InlineData("W. Europe Standard Time", true)]
    [InlineData("UTC", true)]
    [InlineData("Mars/Olympus_Mons", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Accepts_iana_and_windows_ids(string? id, bool known) =>
        Assert.Equal(known, SiteCalendar.TryFindTimeZone(id, out _));
}
