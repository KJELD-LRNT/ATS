using AssetTracker.Core.Admin;
using AssetTracker.Core.Directory;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Notifications;
using AssetTracker.Core.Security;
using AssetTracker.Tests.Infrastructure;
using AssetTracker.Tests.Lending;
using AssetTracker.Tests.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Directory;

/// <summary>
/// Directory-related sponsor decisions (<c>docs/decisions.md</c>): D6 sync failure alert, D8 unmapped-site people
/// can borrow, D9 manager grants survive deactivation but have no effect. One factory per test (a full sync
/// deactivates everyone else in the database).
/// </summary>
public class SponsorDecisionSyncTests
{
    private const string ItMailbox = "it-sync@example.test";

    private static async Task<SyncFactory> NewFactoryAsync(string? itMailbox = ItMailbox)
    {
        var f = new SyncFactory(new() { ["DirectorySync:MaxDropFraction"] = "0.5" });
        await f.WithDbAsync(async db =>
        {
            await Seed.SiteAsync(db, "HQ", "Head Office");
            await NotificationSeed.SetItMailboxAsync(db, itMailbox);
        });
        return f;
    }

    private static Task<List<NotificationOutbox>> AlertsAsync(AppFactory f) =>
        f.WithDbAsync(db => db.Outbox.AsNoTracking().Where(o => o.Kind == NotificationKind.SystemAlert)
            .OrderBy(o => o.Id).ToListAsync());

    [Fact]
    public async Task D6_failed_sync_stages_one_sanitized_alert_to_the_it_mailbox()
    {
        using var f = await NewFactoryAsync();
        f.Directory.FailWith = new InvalidOperationException(
            "LDAP server unavailable\n   at Fake.Ldap.Connect() in C:\\src\\Ldap.cs:line 42");

        var run = await f.SyncAsync("worker");

        Assert.Equal(SyncRunStatus.Failed, run.Status);
        var alert = Assert.Single(await AlertsAsync(f));
        Assert.Equal(DirectorySyncService.AlertDedupeKey(run.Id), alert.DedupeKey);
        Assert.Equal($"sync-alert:{run.Id}", alert.DedupeKey);
        Assert.Equal(NotificationStatus.Pending, alert.Status);
        Assert.Equal(ItMailbox, alert.RecipientEmail);
        Assert.Contains("AD sync failed", alert.Subject);
        Assert.Contains($"run {run.Id}", alert.Body);
        Assert.Contains("Failed", alert.Body);
        Assert.Contains("LDAP server unavailable", alert.Body);
        Assert.DoesNotContain("Fake.Ldap.Connect", alert.Body);
        Assert.DoesNotContain(":line", alert.Body);
        Assert.Null(alert.LoanId);

        // Exactly one alert per run; a later successful run adds none.
        f.Directory.FailWith = null;
        f.Directory.Entries = Entries.Many(3);
        Assert.Equal(SyncRunStatus.Succeeded, (await f.SyncAsync()).Status);
        Assert.Single(await AlertsAsync(f));
    }

    [Fact]
    public async Task D6_rejected_sync_alert_is_skipped_with_reason_when_the_it_mailbox_is_not_configured()
    {
        using var f = await NewFactoryAsync(itMailbox: null);
        f.Directory.Entries = [];

        var run = await f.SyncAsync();

        Assert.Equal(SyncRunStatus.Rejected, run.Status);
        var alert = Assert.Single(await AlertsAsync(f));
        Assert.Equal($"sync-alert:{run.Id}", alert.DedupeKey);
        Assert.Equal(NotificationStatus.Skipped, alert.Status);
        Assert.Equal(NotificationService.ItMailboxNotConfigured, alert.SkipReason);
        Assert.Null(alert.RecipientEmail);
        Assert.Contains("AD sync rejected", alert.Subject);
        Assert.Contains("low-count", alert.Body);
        Assert.Contains("Rejected", alert.Body);
    }

    [Fact]
    public async Task D8_person_with_an_unmapped_site_can_be_checked_out_to()
    {
        using var f = await NewFactoryAsync();
        f.Directory.Entries = [.. Entries.Many(3), Entries.Person("id-x", "unmapped.user", site: "Nowhere Office")];
        await f.SyncAsync();
        var person = await f.WithDbAsync(db => db.People.AsNoTracking().SingleAsync(p => p.DirectoryId == "id-x"));
        Assert.Null(person.SiteId);
        Assert.True(person.IsActive);

        var site = await f.SiteAsync("D8");
        var result = await f.CheckoutTaggedAsync(await f.AssetAsync("D8-0001", site), person);

        Assert.Equal(person.Id, result.Loan.HolderId);
        Assert.True(result.Loan.IsOpen);
    }

    [Fact]
    public async Task D9_grant_is_kept_but_has_no_effect_after_the_manager_turns_inactive_or_leaves_the_group()
    {
        using var f = await NewFactoryAsync();
        var managerEntry = Entries.Person("id-mgr", "manager.user", sid: TestUser.Manager.Sid);
        f.Directory.Entries = [.. Entries.Many(4), managerEntry];
        await f.SyncAsync();
        var site = await f.WithDbAsync(db => db.Sites.AsNoTracking().SingleAsync(s => s.Key == "HQ"));
        var person = await f.WithDbAsync(db => db.People.AsNoTracking().SingleAsync(p => p.DirectoryId == "id-mgr"));
        await f.RunAsAsync(TestUser.Admin, sp => sp.GetRequiredService<AdminService>().AddGrantAsync(person.Id, site.Id));

        Task<SiteAccess> AccessAsync(TestUser user) => f.RunAsAsync(user, sp => sp.GetRequiredService<AccessScope>().GetAsync());
        Assert.True((await AccessAsync(TestUser.Manager)).CanRead(site.Id));

        // Leaves the manager AD group (same account, no manager group): the grant row stays but grants nothing.
        var formerManager = TestUser.Manager with { GroupSids = [] };
        Assert.False((await AccessAsync(formerManager)).CanReadInventory);

        // Turns inactive in a sync (absent from the result): the grant row is kept, and grants nothing.
        f.Directory.Entries = Entries.Many(4);
        Assert.Equal(SyncRunStatus.Succeeded, (await f.SyncAsync()).Status);
        Assert.False((await f.WithDbAsync(db => db.People.AsNoTracking().SingleAsync(p => p.Id == person.Id))).IsActive);
        Assert.True(await f.WithDbAsync(db => db.ManagerSiteGrants.AnyAsync(g => g.PrincipalDirectoryId == "id-mgr")));
        Assert.False((await AccessAsync(TestUser.Manager)).CanRead(site.Id));

        // Back in the directory: the kept grant applies again without an admin re-granting it.
        f.Directory.Entries = [.. Entries.Many(4), managerEntry];
        await f.SyncAsync();
        Assert.True((await AccessAsync(TestUser.Manager)).CanRead(site.Id));
    }
}
