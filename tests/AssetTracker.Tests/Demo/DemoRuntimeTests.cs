using System.Net;
using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Lending;
using AssetTracker.Core.Notifications;
using AssetTracker.Core.Time;
using AssetTracker.Infrastructure.Demo;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace AssetTracker.Tests.Demo;

/// <summary>Demo mail pickup, time travel and the role switcher, through the real app in demo mode.</summary>
public class DemoRuntimeTests : IClassFixture<DemoAppFactory>
{
    private readonly DemoAppFactory _f;

    public DemoRuntimeTests(DemoAppFactory f)
    {
        _f = f;
        _f.CreateDemoClient();
    }

    [Fact]
    public async Task Pickup_relay_writes_a_readable_eml_and_the_mailbox_shows_it_encoded()
    {
        var relay = _f.Services.GetRequiredService<IMailRelay>();
        Assert.IsType<PickupDirectoryMailRelay>(relay);
        var subject = "Demo test " + Guid.NewGuid().ToString("N")[..8];
        await relay.SendAsync(new MailMessage("someone@demo.example", subject, "Line 1\n<script>alert(1)</script>"), default);

        var mailbox = _f.Services.GetRequiredService<DemoMailbox>();
        var summary = Assert.Single(mailbox.List(), m => m.Subject == subject);
        var file = Path.Combine(mailbox.Directory, summary.Id);
        var mime = await MimeMessage.LoadAsync(file);
        Assert.Equal(subject, mime.Subject);
        Assert.Equal("someone@demo.example", mime.To.Mailboxes.Single().Address);
        Assert.Equal(DemoDefaults.FromAddress, mime.From.Mailboxes.Single().Address);
        Assert.Contains("<script>alert(1)</script>", mime.TextBody);

        var client = _f.CreateDemoClient();
        var page = await client.GetAsync("/Demo/Mailbox?id=" + Uri.EscapeDataString(summary.Id));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);

        // Only bare file names from the pickup folder can be opened.
        Assert.Null(await mailbox.ReadAsync("../demo.db", default));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Demo/Mailbox?id=..%2Fdemo.db")).StatusCode);
    }

    [Fact]
    public async Task Time_travel_moves_every_sites_calendar_and_reset_returns_to_now()
    {
        var data = _f.Services.GetRequiredService<DemoDataManager>();
        Task<DateOnly> AmsToday() => WithScope(async sp =>
        {
            var ams = await sp.GetRequiredService<AssetTracker.Core.Data.AppDbContext>().Sites.SingleAsync(s => s.Key == "AMS");
            return await sp.GetRequiredService<SiteCalendar>().TodayForAsync(ams.Id);
        });

        var today = await AmsToday();
        try
        {
            await data.TravelAsync(TimeSpan.FromDays(7));
            Assert.Equal(today.AddDays(7), await AmsToday());
            var stored = await _f.WithDbAsync(db => db.Settings.SingleAsync(s => s.Key == DemoClock.OffsetKey));
            Assert.Equal("7.00:00:00", stored.Value);
            // The time-travel sync keeps the snapshot fresh at the new date.
            await _f.WithDbAsync(async db =>
            {
                Assert.Equal("system:demo-time-travel", (await db.SyncRuns.OrderByDescending(r => r.Id).FirstAsync()).TriggeredBy);
                return 0;
            });
        }
        finally
        {
            await data.TravelAsync(null);
        }
        Assert.Equal(today, await AmsToday());
    }

    [Fact]
    public async Task Time_travel_reminders_are_once_per_site_day_and_it_only_for_the_inactive_leaver()
    {
        var data = _f.Services.GetRequiredService<DemoDataManager>();
        Task<int> Reminders() => _f.WithDbAsync(db => db.Outbox.CountAsync(o => o.Kind == NotificationKind.OverdueReminder));
        try
        {
            // A week ahead, at about noon in Amsterdam (past the 08:00 reminder time whenever the test runs).
            var ams = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
            var target = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow.AddDays(7), ams);
            await data.TravelAsync(TimeSpan.FromDays(7) + TimeSpan.FromHours((12 - target.Hour + 24) % 24));
            var first = await Reminders();
            // Another pass on the same site days queues nothing new.
            await WithScope(sp => sp.GetRequiredService<OverdueReminderService>().QueueRemindersAsync(CancellationToken.None));
            Assert.Equal(first, await Reminders());

            await data.TravelAsync(TimeSpan.FromDays(1));
            Assert.True(await Reminders() > first, "a new site day queues new reminders");

            await _f.WithDbAsync(async db =>
            {
                var leaver = await db.People.SingleAsync(p => p.Username == DemoDirectorySource.LeaverUsername);
                var rows = await db.Outbox.Where(o => o.Kind == NotificationKind.OverdueReminder &&
                                                      o.Loan!.HolderId == leaver.Id).ToListAsync();
                Assert.NotEmpty(rows);
                Assert.All(rows, r =>
                {
                    Assert.Equal(DemoDefaults.ItMailbox, r.RecipientEmail);
                    Assert.Contains("was not emailed", r.Body);
                });
                return 0;
            });
        }
        finally
        {
            await data.TravelAsync(null);
        }
    }

    [Fact]
    public async Task Correction_that_overlaps_a_later_loan_is_rejected_in_preview()
    {
        // Guide scenario: AMS-LT-007 has two closed loans; moving the first return past the second start conflicts.
        var (firstLoan, secondStart) = await _f.WithDbAsync(async db =>
        {
            var loans = await db.Loans.Where(l => l.Asset!.Tag == "AMS-LT-007").ToListAsync();
            var ordered = loans.OrderBy(l => l.StartedAt).ToList();
            return (ordered[0].Id, ordered[1].StartedAt);
        });
        var preview = await AmbientCurrentUser.RunAsAsync("test", AssetTracker.Core.Security.AppRoles.It, () =>
            WithScope(sp => sp.GetRequiredService<CorrectionService>()
                .PreviewAsync(firstLoan, new CorrectionRequest { ReturnedAt = secondStart.AddDays(2) })));
        Assert.True(preview.HasConflicts);
    }

    [Fact]
    public async Task Default_demo_user_is_it_and_admin_and_sees_the_banner()
    {
        var html = await (await _f.CreateDemoClient().GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Contains("DEMO MODE — fictional data", html);
        Assert.Contains(@"DEMO\you", html);
        Assert.Contains("id=\"demo-roles\">It, Admin<", html);
    }

    [Fact]
    public async Task Role_switcher_cookie_drives_claims_roles_and_manager_site_scope()
    {
        var client = _f.CreateDemoClient();
        var res = await FormHelper.PostFormAsync(client, "/Demo", "/Demo?handler=Identity",
            ("manager", "true"), ("personSid", DemoDefaults.ManagerSid));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var setCookie = string.Join(";", res.Headers.GetValues("Set-Cookie"));
        Assert.Contains("ats_demo_identity=", setCookie);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);

        var home = await (await client.GetAsync("/")).Content.ReadAsStringAsync();
        Assert.Contains(@"DEMO\manager.ams", home);
        Assert.Contains("id=\"demo-roles\">Manager<", home);

        // Manager with a grant for AMS only: AMS inventory, nothing from NYC or SIN.
        var assets = await (await client.GetAsync("/Assets")).Content.ReadAsStringAsync();
        Assert.Contains("AMS-DT-001", assets);
        // (the dense floor's desktops fill page 1 of the list, so laptops are looked up)
        Assert.Contains("AMS-LT-001", await (await client.GetAsync("/Assets?q=AMS-LT-001")).Content.ReadAsStringAsync());
        // moved to AMS (last by tag, so on page 2 of the list)
        Assert.Contains(DemoSeeder.TransferredAssetTag, await (await client.GetAsync("/Assets?q=" + DemoSeeder.TransferredAssetTag)).Content.ReadAsStringAsync());
        Assert.DoesNotContain("NYC-LT-001", assets);
        Assert.DoesNotContain("SIN-LT-001", assets);
        // D2: audit is IT only.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/Audit")).StatusCode);

        // No roles at all: no inventory.
        await FormHelper.PostFormAsync(client, "/Demo", "/Demo?handler=Identity", ("personSid", DemoDefaults.YouSid));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/Assets")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Demo")).StatusCode); // the switcher stays reachable

        // Admin only: settings yes, inventory no.
        await FormHelper.PostFormAsync(client, "/Demo", "/Demo?handler=Identity", ("admin", "true"), ("personSid", DemoDefaults.YouSid));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin/Settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/Loans")).StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Demo")]
    [InlineData("/Demo/Mailbox")]
    [InlineData("/Loans/Checkout?tag=AMS-LT-008&who=Alex")]
    [InlineData("/Admin/Sites")]
    [InlineData("/Admin/Grants")]
    [InlineData("/Pools/Create")]
    public async Task Pages_have_no_inline_styles_or_scripts_blocked_by_the_csp(string url)
    {
        var res = await _f.CreateDemoClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain(" style=", html);
        Assert.DoesNotContain("<style", html);
        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public async Task Demo_posts_require_the_antiforgery_token()
    {
        var client = _f.CreateDemoClient();
        var res = await client.PostAsync("/Demo?handler=Identity",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["it"] = "true" }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    private async Task<T> WithScope<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = _f.Services.CreateScope();
        return await action(scope.ServiceProvider);
    }
}
