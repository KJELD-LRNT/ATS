using AssetTracker.Core.Admin;
using AssetTracker.Core.Data;
using AssetTracker.Core.Directory;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Inventory;
using AssetTracker.Core.Lending;
using AssetTracker.Core.Security;
using AssetTracker.Core.Settings;
using AssetTracker.Core.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AssetTracker.Infrastructure.Demo;

/// <summary>
/// Fills an empty demo database with fictional data through the real services (as the IT + admin actor
/// <see cref="DemoDefaults.SeedActor"/>), so audit, stock ledger, loan events and outbox rows are consistent.
/// History is created by pinning the demo clock to past instants (relative to now), so due dates, overdue loans and
/// returns look natural. Mail generated for that history is marked as already sent: the demo mailbox starts empty
/// and only receives what happens after start-up (for example today's overdue reminders).
/// <para>The content is fixed; only timestamps follow the current date. Seeding an already seeded database does nothing.</para>
/// </summary>
public sealed class DemoSeeder(IServiceScopeFactory scopes, DemoClock clock, ILogger<DemoSeeder> logger)
{
    public const string FormulaAssetTag = "AMS-LT-009";
    public const string TransferredAssetTag = "NYC-LT-007";
    public const string CorrectedAssetTag = "AMS-LT-006";

    /// <summary>Seeds when the database has no sites yet. Returns true when it seeded.</summary>
    public async Task<bool> SeedIfEmptyAsync(CancellationToken ct = default)
    {
        if (await WithDbAsync(db => db.Sites.AnyAsync(ct))) return false;
        await AmbientCurrentUser.RunAsAsync(DemoDefaults.SeedActor, AppRoles.It | AppRoles.Admin, () => SeedAsync(ct));
        return true;
    }

    private async Task SeedAsync(CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var now = clock.UtcNow;
        DateTimeOffset Ago(double days) => now.AddDays(-days);

        // 1. Settings shown on Admin → Settings (relay and directory are replaced by demo adapters regardless).
        await RunAsync(Ago(120), async sp =>
        {
            var settings = sp.GetRequiredService<SettingsService>();
            await settings.SaveSectionAsync(SettingSections.Clock,
                new Dictionary<string, string?> { ["Clock:TimeZoneId"] = DemoDefaults.TimeZoneId }, ct: ct);
            await settings.SaveSectionAsync(SettingSections.Directory, new Dictionary<string, string?>
            {
                ["ActiveDirectory:SearchRoot"] = "LDAP://OU=People,DC=demo,DC=example",
                ["ActiveDirectory:SiteAttribute"] = "physicalDeliveryOfficeName",
            }, ct: ct);
            await settings.SaveSectionAsync(SettingSections.Mail, new Dictionary<string, string?>
            {
                ["Mail:Host"] = "demo-pickup",
                ["Mail:SecureSocket"] = "None",
                ["Mail:FromAddress"] = DemoDefaults.FromAddress,
            }, ct: ct);
            await settings.SaveSectionAsync(SettingSections.Notifications, new Dictionary<string, string?>
            {
                [SettingCatalog.ItMailboxField] = DemoDefaults.ItMailbox,
            }, ct: ct);
            return 0;
        });

        // 2. Sites, each with its own timezone and the directory value the demo directory uses.
        var sites = await RunAsync(Ago(120), async sp =>
        {
            var admin = sp.GetRequiredService<AdminService>();
            var ams = await admin.CreateSiteAsync("AMS", "Amsterdam (demo)", DemoDirectorySource.AmsValue, true, "Europe/Amsterdam", ct);
            var nyc = await admin.CreateSiteAsync("NYC", "New York (demo)", DemoDirectorySource.NycValue, true, "America/New_York", ct);
            var sin = await admin.CreateSiteAsync("SIN", "Singapore (demo)", DemoDirectorySource.SinValue, true, "Asia/Singapore", ct);
            return new SiteIds(ams.Id, nyc.Id, sin.Id);
        });

        // 3. People: a directory sync from the historical snapshot (the leaver is still enabled), then the manager grant.
        await DemoDirectorySource.RunWithHistoricalSnapshotAsync(() =>
            RunAsync(Ago(120), sp => sp.GetRequiredService<DirectorySyncService>().RunAsync(DemoDefaults.SeedActor, ct)));
        var people = await WithDbAsync(db => db.People.AsNoTracking().ToDictionaryAsync(p => p.Username, p => p.Id, ct));
        int P(int index) => people[DemoDirectorySource.Username(index)];
        await RunAsync(Ago(119), sp =>
            sp.GetRequiredService<AdminService>().AddGrantAsync(people[DemoDefaults.ManagerUsername], sites.Ams, ct));

        // 4. Tagged assets and pools.
        var assets = await RunAsync(Ago(118), sp => CreateAssetsAsync(sp.GetRequiredService<AssetService>(), sites, ct));
        var pools = await RunAsync(Ago(118), sp => CreatePoolsAsync(sp.GetRequiredService<PoolService>(), sites, ct));

        // Site-local "today" now, for due dates relative to today.
        var (tA, tN, tS) = await RunAsync(now, async sp =>
        {
            var cal = sp.GetRequiredService<SiteCalendar>();
            return (await cal.TodayForAsync(sites.Ams, ct), await cal.TodayForAsync(sites.Nyc, ct),
                await cal.TodayForAsync(sites.Sin, ct));
        });

        // 5. Everything that happened since, executed in time order. Before each step the directory is synced again
        // if the last sync is older than a day, so the history shows a fresh snapshot (as the hourly job would).
        var plan = new List<Step>();
        var loans = new Dictionary<string, int>();
        void At(double daysAgo, Func<IServiceProvider, Task> act) => plan.Add(new Step(daysAgo, act));
        void Checkout(double daysAgo, string loan, int holderId, string? asset = null, (string, string)? pool = null,
            int quantity = 1, DateOnly? due = null, string? note = null) =>
            At(daysAgo, async sp => loans[loan] = (await sp.GetRequiredService<LendingService>().CheckoutAsync(
                new CheckoutRequest(holderId, asset is null ? null : assets[asset], pool is null ? null : pools[pool.Value],
                    quantity, due, "Good", note), ct)).Loan.Id);
        void Return(double daysAgo, string loan, int? quantity = null, string? condition = null, string? note = null,
            bool markUnavailable = false) =>
            At(daysAgo, sp => sp.GetRequiredService<LendingService>()
                .ReturnAsync(loans[loan], quantity, null, condition, note, markUnavailable, ct));

        // Closed history.
        Checkout(90, "h1", P(33), "AMS-LT-007", due: tA.AddDays(-82));
        Return(80, "h1", condition: "Good");
        Checkout(70, "h2", P(36), "AMS-LT-007");
        Return(50, "h2", note: "Returned after project end");
        // Loaned at New York, returned, then moved to Amsterdam: the AMS manager sees the NYC holder in its history.
        Checkout(100, "h3", P(1), TransferredAssetTag, note: "Project laptop");
        Return(75, "h3", condition: "Good");
        At(60, sp => sp.GetRequiredService<AssetService>()
            .TransferSiteAsync(assets[TransferredAssetTag], sites.Ams, "Moved to the Amsterdam office", ct));
        // Returned damaged and marked unavailable (repair).
        Checkout(45, "h4", P(4), "NYC-LT-008");
        Return(30, "h4", condition: "Cracked screen", note: "Sent for repair", markUnavailable: true);
        Checkout(30, "h5", P(14), pool: ("SIN", "MOUSE-W"));
        Return(20, "h5");

        // Stock movements: a delivery, a loss and a transfer between sites.
        At(60, sp => sp.GetRequiredService<PoolService>().ReceiveAsync(pools[("AMS", "HS-STEREO")], 10, "Quarterly delivery (demo)", ct));
        At(40, sp => sp.GetRequiredService<PoolService>().RecordLossAsync(pools[("NYC", "CAM-HD")], 1, "Damaged in transit (demo)", ct));
        At(35, sp => sp.GetRequiredService<PoolService>()
            .TransferAsync(pools[("AMS", "MOUSE-W")], pools[("SIN", "MOUSE-W")], 5, "Rebalancing stock (demo)", ct));

        // Retired assets (no open loan).
        At(95, sp => sp.GetRequiredService<AssetService>().RetireAsync(assets["AMS-LT-010"], "End of life (demo)", ct));
        At(95, sp => sp.GetRequiredService<AssetService>().RetireAsync(assets["SIN-DT-003"], "End of life (demo)", ct));

        // Open loans: overdue, due today, due later and undated. Amsterdam:
        Checkout(30, "a1", P(0), "AMS-LT-001", due: tA.AddDays(-10));              // overdue 10 days
        Checkout(7, "a2", P(3), "AMS-LT-002", due: tA);                            // due today
        Checkout(20, "a3", P(6), "AMS-LT-003", note: "Undated loan");              // undated
        Checkout(14, "a4", P(9), "AMS-PH-001", due: tA.AddDays(3));                // due in 3 days
        Checkout(40, "a5", P(12), "AMS-DT-001", due: tA.AddDays(-2));              // overdue 2 days
        // Held by the leaver, who is disabled in the current directory: reminders go to IT only.
        Checkout(60, "a6", people[DemoDirectorySource.LeaverUsername], "AMS-LT-004", due: tA.AddDays(-5));
        Checkout(10, "a7", P(15), pool: ("AMS", "HS-STEREO"), quantity: 3, due: tA.AddDays(5), note: "Team onboarding");
        Return(4, "a7", quantity: 1, note: "One headset back");                    // partial return
        Checkout(25, "a8", P(18), pool: ("AMS", "HS-STEREO"), quantity: 2, due: tA.AddDays(-5));
        Checkout(3, "a9", people[DemoDefaults.YouUsername], "AMS-LT-005", due: tA.AddDays(14));
        // Checked out to the wrong person, then corrected (superseding event, mail to old and new holder and IT).
        Checkout(12, "a10", P(21), CorrectedAssetTag, due: tA.AddDays(6));
        At(11, sp => sp.GetRequiredService<CorrectionService>().CommitAsync(loans["a10"],
            new CorrectionRequest { HolderId = P(24), Reason = "Recorded on the wrong person at the service desk (demo)" }, ct));
        // New York:
        Checkout(20, "n1", P(1), "NYC-LT-001", due: tN.AddDays(-3));
        Checkout(10, "n2", P(4), "NYC-PH-001", due: tN);
        Checkout(5, "n3", P(7), "NYC-LT-002");
        Checkout(7, "n4", P(13), pool: ("NYC", "CAM-HD"), due: tN);
        Checkout(15, "n5", P(16), "NYC-DT-001", due: tN.AddDays(2));
        // D8: a person whose directory site maps to no site can still borrow.
        Checkout(9, "n6", people[DemoDirectorySource.UnmappedUsernames[0]], "NYC-LT-003");
        // Singapore:
        Checkout(15, "s1", P(2), "SIN-LT-001", due: tS.AddDays(-1));
        Checkout(8, "s2", P(5), "SIN-PH-001");
        Checkout(6, "s3", P(8), pool: ("SIN", "KB-US"), quantity: 2, due: tS.AddDays(4));
        Checkout(25, "s4", P(11), "SIN-LT-002", due: tS.AddDays(-7));

        var lastSync = Ago(120);
        foreach (var step in plan.OrderByDescending(p => p.DaysAgo))
        {
            var at = Ago(step.DaysAgo);
            if (at - lastSync > TimeSpan.FromHours(20))
            {
                lastSync = at.AddMinutes(-30);
                await DemoDirectorySource.RunWithHistoricalSnapshotAsync(() => RunAsync(lastSync,
                    sp => sp.GetRequiredService<DirectorySyncService>().RunAsync("system:schedule", ct)));
            }
            await RunAsync(at, async sp => { await step.Act(sp); return 0; });
        }

        // 7. Current directory snapshot (the leaver is now disabled), then mark the historical mail as sent.
        await RunAsync(now, sp => sp.GetRequiredService<DirectorySyncService>().RunAsync(DemoDefaults.SeedActor, ct));
        await WithDbAsync(async db =>
        {
            var pending = await db.Outbox.Where(o => o.Status == NotificationStatus.Pending).ToListAsync(ct);
            foreach (var o in pending)
            {
                o.Status = NotificationStatus.Sent;
                o.Attempts = 1;
                o.LastAttemptAt = o.CreatedAt;
                o.SentAt = o.CreatedAt;
            }
            await db.SaveChangesAsync(ct);
            return 0;
        });

        logger.LogInformation("Demo data seeded in {Elapsed:0.0} s.", (DateTimeOffset.UtcNow - started).TotalSeconds);
    }

    private sealed record Step(double DaysAgo, Func<IServiceProvider, Task> Act);

    private sealed record SiteIds(int Ams, int Nyc, int Sin)
    {
        public int Of(string key) => key switch { "AMS" => Ams, "NYC" => Nyc, _ => Sin };
    }

    private static readonly string[] SiteKeys = ["AMS", "NYC", "SIN"];

    private static async Task<Dictionary<string, int>> CreateAssetsAsync(AssetService svc, SiteIds sites, CancellationToken ct)
    {
        var result = new Dictionary<string, int>();
        // Per site: laptops, desktops, phones, phone numbers.
        var counts = new Dictionary<string, (int Lt, int Dt, int Ph, int Pn)>
        {
            ["AMS"] = (10, 3, 3, 2),
            ["NYC"] = (8, 2, 2, 1),
            ["SIN"] = (5, 3, 2, 1),
        };
        for (var s = 0; s < SiteKeys.Length; s++)
        {
            var key = SiteKeys[s];
            var site = sites.Of(key);
            var (lt, dt, ph, pn) = counts[key];
            for (var i = 1; i <= lt; i++)
            {
                var tag = $"{key}-LT-{i:000}";
                var description = tag == FormulaAssetTag
                    ? "=HYPERLINK(\"http://demo.example\",\"click\") formula test laptop (demo)"
                    : $"Demo L{14 + i % 3} laptop";
                await Create(new CreateAssetInput(tag, AssetType.Laptop, site, description, "Good", $"DEMO-SN-{key}-L{i:000}"));
            }
            for (var i = 1; i <= dt; i++)
                await Create(new CreateAssetInput($"{key}-DT-{i:000}", AssetType.Desktop, site, "Demo D5 desktop", "Good",
                    $"DEMO-SN-{key}-D{i:000}"));
            for (var i = 1; i <= ph; i++)
                await Create(new CreateAssetInput($"{key}-PH-{i:000}", AssetType.Phone, site, "Demo P8 phone", "Good",
                    Imei: DemoImei(s * 100 + i), PhoneNumber: DemoPhone(key, i)));
            for (var i = 1; i <= pn; i++)
                await Create(new CreateAssetInput($"{key}-PN-{i:000}", AssetType.PhoneNumber, site, "Demo mobile subscription",
                    PhoneNumber: DemoPhone(key, 50 + i)));
        }
        return result;

        async Task Create(CreateAssetInput input) => result[input.Tag] = (await svc.CreateAsync(input, ct)).Id;
    }

    /// <summary>A 15-digit IMEI with a valid Luhn check digit (fictional TAC 35999...).</summary>
    public static string DemoImei(int n)
    {
        var body = $"35999{n:000000000}";
        var sum = 0;
        for (var i = 0; i < body.Length; i++)
        {
            // Double every second digit counting from the right of the full 15-digit number (the check digit is position 0).
            var d = body[body.Length - 1 - i] - '0';
            if (i % 2 == 0)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
        }
        return body + (10 - sum % 10) % 10;
    }

    /// <summary>Numbers from ranges reserved for fiction: +1 212 555 01xx (US) and +44 7700 900xxx (UK).</summary>
    private static string DemoPhone(string siteKey, int n) => siteKey switch
    {
        "NYC" => $"+1212555{100 + n:0000}",
        "AMS" => $"+447700900{100 + n:000}",
        _ => $"+447700900{500 + n:000}",
    };

    private static async Task<Dictionary<(string, string), int>> CreatePoolsAsync(PoolService svc, SiteIds sites, CancellationToken ct)
    {
        var result = new Dictionary<(string, string), int>();
        var defs = new (AssetType Type, string Sku, string Description, int Quantity)[]
        {
            (AssetType.Headset, "HS-STEREO", "Stereo USB headset (demo)", 20),
            (AssetType.Webcam, "CAM-HD", "HD webcam (demo)", 8),
            (AssetType.Peripheral, "KB-US", "USB keyboard, US layout (demo)", 15),
            (AssetType.Peripheral, "MOUSE-W", "Wireless mouse (demo)", 25),
        };
        foreach (var key in SiteKeys)
            foreach (var d in defs)
                result[(key, d.Sku)] = (await svc.CreateAsync(
                    new CreatePoolInput(d.Type, d.Sku, sites.Of(key), d.Description, d.Quantity, "Initial stock (demo)"), ct)).Id;
        return result;
    }

    /// <summary>Runs in a fresh DI scope with the demo clock fixed at <paramref name="at"/>.</summary>
    private Task<T> RunAsync<T>(DateTimeOffset at, Func<IServiceProvider, Task<T>> action) =>
        clock.RunAtAsync(at, async () =>
        {
            using var scope = scopes.CreateScope();
            return await action(scope.ServiceProvider);
        });

    private async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = scopes.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}
