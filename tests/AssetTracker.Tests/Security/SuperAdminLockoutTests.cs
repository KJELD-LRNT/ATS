using System.Net;
using AssetTracker.Core.Security;
using AssetTracker.Tests.Infrastructure;
using AssetTracker.Web.Pages.Local;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static AssetTracker.Tests.Security.SuperAdminTestSupport;

namespace AssetTracker.Tests.Security;

/// <summary>Brute-force protection of the super admin: atomic counter, 5 failures → 15-minute lock, per-IP limit, alerts.</summary>
public class SuperAdminLockoutTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public SuperAdminLockoutTests(AppFactory f) => _f = f;

    private const string Wrong = "definitely not the password";

    private Task<int> CountAsync(string action, int afterId) =>
        _f.WithDbAsync(db => db.AuditEvents.CountAsync(a => a.Action == action && a.Id > afterId));

    private Task<int> LastAuditIdAsync() => _f.WithDbAsync(async db => await db.AuditEvents.MaxAsync(a => (int?)a.Id) ?? 0);

    private Task<int> LockMailsAsync() => _f.WithDbAsync(db => db.Outbox.CountAsync(o => o.DedupeKey.StartsWith("superadmin-lockout:")));

    [Fact]
    public async Task Five_failures_lock_the_account_for_fifteen_minutes()
    {
        // A lock period is identified by its end time; start away from the other tests' lock periods.
        _f.Clock.Advance(TimeSpan.FromHours(1));
        await ResetAccountAsync(_f);
        var since = await LastAuditIdAsync();
        var mails = await LockMailsAsync();
        var client = _f.CreateAnonymousClient();
        for (var i = 1; i <= 5; i++)
        {
            var res = await PostSignInAsync(client, Wrong);
            Assert.Contains(System.Net.WebUtility.HtmlEncode(SuperAdminModel.FailedMessage), await res.Content.ReadAsStringAsync());
        }
        var account = await AccountAsync(_f);
        Assert.Equal(_f.Clock.UtcNow + TimeSpan.FromMinutes(15), account.LockedUntil);
        Assert.Equal(5, await CountAsync("superadmin.signin-failed", since));
        Assert.Equal(1, await CountAsync("superadmin.lockout", since));
        Assert.Equal(mails + 1, await LockMailsAsync());

        // Locked: even the right password is refused, with the same message; attempts are counted, not audited.
        var other = _f.CreateAnonymousClient();
        var locked = await PostSignInAsync(other, Password);
        Assert.Contains(System.Net.WebUtility.HtmlEncode(SuperAdminModel.FailedMessage), await locked.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(other, "/Admin/Settings"));
        await PostSignInAsync(other, Wrong);
        Assert.Equal(5, await CountAsync("superadmin.signin-failed", since));
        Assert.Equal(1, await CountAsync("superadmin.lockout", since));
        Assert.Equal(mails + 1, await LockMailsAsync());
        Assert.Equal(7, (await AccountAsync(_f)).FailedCount);

        // The lock is stored in the database (survives a restart) and runs out after 15 minutes.
        _f.Clock.Advance(TimeSpan.FromMinutes(14));
        Assert.Equal(HttpStatusCode.OK, (await PostSignInAsync(other, Password)).StatusCode);
        _f.Clock.Advance(TimeSpan.FromMinutes(1));
        await SignedInClientAsync(_f);
        account = await AccountAsync(_f);
        Assert.Equal((0, (DateTimeOffset?)null), (account.FailedCount, account.LockedUntil));
    }

    [Fact]
    public async Task A_new_lock_period_mails_again_and_a_lock_that_ran_out_starts_a_new_count()
    {
        _f.Clock.Advance(TimeSpan.FromHours(1));
        await ResetAccountAsync(_f);
        var mails = await LockMailsAsync();
        var service = (IServiceProvider sp) => sp.GetRequiredService<SuperAdminService>();
        async Task<SuperAdminSignInResult> AttemptAsync(string password) =>
            await _f.RunAsAsync(TestUser.Nobody, async sp => (await service(sp).SignInAsync(SuperAdmin.UserName, password, "10.9.9.9")).Result);

        for (var i = 0; i < 4; i++) Assert.Equal(SuperAdminSignInResult.Failed, await AttemptAsync(Wrong));
        Assert.Equal(SuperAdminSignInResult.LockedOut, await AttemptAsync(Wrong));
        Assert.Equal(mails + 1, await LockMailsAsync());

        _f.Clock.Advance(TimeSpan.FromMinutes(16));
        // After the lock: the count starts again at 1, so four more failures do not lock.
        for (var i = 0; i < 4; i++) Assert.Equal(SuperAdminSignInResult.Failed, await AttemptAsync(Wrong));
        Assert.Null((await AccountAsync(_f)).LockedUntil);
        Assert.Equal(SuperAdminSignInResult.LockedOut, await AttemptAsync(Wrong));
        Assert.Equal(mails + 2, await LockMailsAsync());
    }

    [Fact]
    public async Task Parallel_attempts_cannot_get_past_the_limit()
    {
        _f.Clock.Advance(TimeSpan.FromHours(1));
        await ResetAccountAsync(_f);
        // SQLite only (production is SQL Server): write-ahead logging, so 20 concurrent writers wait for each other
        // instead of failing with "database is locked" while upgrading a read lock (the journal mode stays with this
        // class's database file).
        if (!AppFactory.UsingSqlServer)
            await _f.WithDbAsync(async db => { await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;"); });
        var since = await LastAuditIdAsync();
        var attempts = Enumerable.Range(0, 20).Select(i => Task.Run(() => _f.RunAsAsync(TestUser.Nobody, async sp =>
            (await sp.GetRequiredService<SuperAdminService>().SignInAsync(SuperAdmin.UserName, Wrong, $"10.8.0.{i}")).Result)));
        var results = await Task.WhenAll(attempts);

        // Only 5 attempts ever reached the password check; the rest were refused as locked.
        Assert.Equal(5, await CountAsync("superadmin.signin-failed", since));
        Assert.DoesNotContain(SuperAdminSignInResult.Success, results);
        Assert.True(results.Count(r => r == SuperAdminSignInResult.LockedOut) >= 16, string.Join(", ", results));
        Assert.Equal(1, await CountAsync("superadmin.lockout", since));
        Assert.NotNull((await AccountAsync(_f)).LockedUntil);
        Assert.Equal(SuperAdminSignInResult.LockedOut, await _f.RunAsAsync(TestUser.Nobody, async sp =>
            (await sp.GetRequiredService<SuperAdminService>().SignInAsync(SuperAdmin.UserName, Password, "10.8.1.1")).Result));
    }

    [Fact]
    public async Task One_ip_address_gets_ten_attempts_per_fifteen_minutes()
    {
        await ResetAccountAsync(_f);
        var ip = AppFactory.NextIp();
        var client = _f.CreateAnonymousClient(ip);
        for (var i = 0; i < SuperAdmin.RateLimitPermits; i++)
            Assert.Equal(HttpStatusCode.OK, (await PostSignInAsync(client, Wrong)).StatusCode);
        var limited = await PostSignInAsync(client, Password);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Contains("Too many sign-in attempts", await limited.Content.ReadAsStringAsync());
        // Reading the page is not limited, and another address can still try (and the owner can unlock).
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(client, "/superadmin"));
        await ResetAccountAsync(_f);
        await SignedInClientAsync(_f);
    }
}
