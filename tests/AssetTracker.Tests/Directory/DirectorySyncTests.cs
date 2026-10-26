using AssetTracker.Core.Common;
using AssetTracker.Core.Directory;
using AssetTracker.Core.Domain;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AssetTracker.Tests.Directory;

public class DirectorySyncTests
{
    private static async Task<SyncFactory> NewFactoryAsync(Dictionary<string, string?>? config = null)
    {
        var f = new SyncFactory(config);
        await f.WithDbAsync(db => Seed.SiteAsync(db, "HQ", "Head Office"));
        return f;
    }

    private static Task<Person> PersonAsync(AppFactory f, string directoryId) =>
        f.WithDbAsync(db => db.People.AsNoTracking().SingleAsync(p => p.DirectoryId == directoryId));

    [Fact]
    public async Task First_sync_creates_people_with_mapped_site_and_audits_the_run()
    {
        using var f = await NewFactoryAsync();
        f.Directory.Entries = Entries.Many(3);
        f.Directory.Entries[0] = Entries.Person("id-0", "user0", sid: "S-1-5-21-9-9-9-1001", title: "Engineer");

        var run = await f.SyncAsync();

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal((3, 3, 0, 3, 0, 0), (run.Fetched, run.Mapped, run.Quarantined, run.Created, run.Updated, run.Deactivated));
        Assert.NotNull(run.FinishedAt);
        var p = await PersonAsync(f, "id-0");
        Assert.Equal("user0", p.Username);
        Assert.Equal("Test user0", p.DisplayName);
        Assert.Equal("Engineer", p.JobTitle);
        Assert.Equal("user0@example.test", p.Email);
        Assert.Equal("S-1-5-21-9-9-9-1001", p.Sid);
        Assert.True(p.IsActive);
        Assert.NotNull(p.SiteId);

        var audit = await f.WithDbAsync(db => db.AuditEvents.SingleAsync(a => a.Action == "directory.sync"));
        Assert.Equal(run.Id.ToString(), audit.EntityId);
        Assert.Contains("\"Created\":3", audit.DetailsJson);
    }

    [Fact]
    public async Task Rename_updates_the_same_person_instead_of_creating_a_second()
    {
        using var f = await NewFactoryAsync();
        f.Directory.Entries = Entries.Many(5);
        await f.SyncAsync();
        var before = await PersonAsync(f, "id-1");

        f.Directory.Entries[1] = Entries.Person("id-1", "user1.renamed");
        var run = await f.SyncAsync();

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal((0, 1), (run.Created, run.Updated));
        var after = await PersonAsync(f, "id-1");
        Assert.Equal(before.Id, after.Id);
        Assert.Equal("user1.renamed", after.Username);
        Assert.Equal(5, await f.WithDbAsync(db => db.People.CountAsync()));
    }

    [Fact]
    public async Task Disabled_account_becomes_inactive_and_cannot_receive_new_loans()
    {
        using var f = await NewFactoryAsync();
        f.Directory.Entries = Entries.Many(10);
        await f.SyncAsync();

        f.Directory.Entries[2] = Entries.Person("id-2", "user2", enabled: false);
        var run = await f.SyncAsync();

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(1, run.Deactivated);
        var p = await PersonAsync(f, "id-2");
        Assert.False(p.IsActive);
        Assert.False(p.CanReceiveNewLoan);
    }

    [Fact]
    public async Task Person_absent_from_a_successful_full_result_is_deactivated_not_deleted_and_can_return()
    {
        using var f = await NewFactoryAsync();
        f.Directory.Entries = Entries.Many(10);
        await f.SyncAsync();
        var original = await PersonAsync(f, "id-9");

        f.Directory.Entries.RemoveAt(9);
        var run = await f.SyncAsync();

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(1, run.Deactivated);
        var absent = await PersonAsync(f, "id-9");
        Assert.Equal(original.Id, absent.Id);
        Assert.False(absent.IsActive);
        Assert.Equal(10, await f.WithDbAsync(db => db.People.CountAsync()));

        f.Directory.Entries.Add(Entries.Person("id-9", "user9"));
        await f.SyncAsync();
        Assert.True((await PersonAsync(f, "id-9")).IsActive);
    }

    [Fact]
    public async Task Failed_fetch_marks_run_failed_and_preserves_the_snapshot()
    {
        using var f = await NewFactoryAsync();
        f.Directory.Entries = Entries.Many(4);
        var good = await f.SyncAsync();

        f.Directory.FailWith = new InvalidOperationException("LDAP server unavailable");
        f.Clock.Advance(TimeSpan.FromHours(1));
        var run = await f.SyncAsync();

        Assert.Equal(SyncRunStatus.Failed, run.Status);
        Assert.Contains("LDAP server unavailable", run.Error);
        Assert.Equal(4, await f.WithDbAsync(db => db.People.CountAsync(p => p.IsActive)));

        var status = await f.StatusAsync();
        Assert.Equal(good.FinishedAt, status.LastSuccessAt);
        Assert.True(status.LastRunUnsuccessful);
        Assert.Equal(SyncRunStatus.Failed, status.LastRunStatus);
        Assert.Contains("LDAP server unavailable", status.LastRunError);
        Assert.Equal(2, await f.WithDbAsync(db => db.AuditEvents.CountAsync(a => a.Action == "directory.sync")));
    }

    [Fact]
    public async Task Unexpectedly_small_result_is_rejected_and_snapshot_preserved()
    {
        using var f = await NewFactoryAsync();
        f.Directory.Entries = Entries.Many(10);
        await f.SyncAsync();

        f.Directory.Entries = Entries.Many(5); // would deactivate 50% > default 20%
        var run = await f.SyncAsync();

        Assert.Equal(SyncRunStatus.Rejected, run.Status);
        Assert.Contains("deactivate 5 of 10", run.Error);
        Assert.Equal(0, run.Deactivated);
        Assert.Equal(10, await f.WithDbAsync(db => db.People.CountAsync(p => p.IsActive)));
        Assert.Contains(DirectorySyncService.ParseIssues(run.IssuesJson), i => i.Code == "low-count");
    }

    [Fact]
    public async Task Empty_result_is_always_rejected()
    {
        using var f = await NewFactoryAsync(new() { ["DirectorySync:MaxDropFraction"] = "1" });
        f.Directory.Entries = Entries.Many(3);
        await f.SyncAsync();

        f.Directory.Entries = [];
        var run = await f.SyncAsync();

        Assert.Equal(SyncRunStatus.Rejected, run.Status);
        Assert.Equal(3, await f.WithDbAsync(db => db.People.CountAsync(p => p.IsActive)));
    }

    [Fact]
    public async Task Result_below_configured_minimum_count_is_rejected()
    {
        using var f = await NewFactoryAsync(new() { ["DirectorySync:MinimumExpectedCount"] = "5" });
        f.Directory.Entries = Entries.Many(4);

        var run = await f.SyncAsync();

        Assert.Equal(SyncRunStatus.Rejected, run.Status);
        Assert.Equal(0, await f.WithDbAsync(db => db.People.CountAsync()));
        Assert.True((await f.StatusAsync()).IsStale);
    }

    [Fact]
    public async Task Entries_without_id_or_username_and_duplicate_ids_are_quarantined()
    {
        using var f = await NewFactoryAsync();
        f.Directory.Entries = Entries.Many(10);
        await f.SyncAsync();

        f.Directory.Entries = Entries.Many(10);
        f.Directory.Entries.Add(Entries.Person(null!, "no.id"));
        f.Directory.Entries.Add(Entries.Person("  ", "blank.id"));
        f.Directory.Entries.Add(Entries.Person("id-new", null!));
        // id-3 now appears twice: both copies are quarantined and the existing person is left unchanged.
        f.Directory.Entries[3] = Entries.Person("id-3", "user3.changed");
        f.Directory.Entries.Add(Entries.Person("ID-3", "user3.copy"));

        var run = await f.SyncAsync();

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(14, run.Fetched);
        Assert.Equal(5, run.Quarantined);
        Assert.Equal(10 - 1, run.Mapped);
        Assert.Equal(0, run.Deactivated);
        var p3 = await PersonAsync(f, "id-3");
        Assert.True(p3.IsActive);
        Assert.Equal("user3", p3.Username);
        Assert.False(await f.WithDbAsync(db => db.People.AnyAsync(p => p.DirectoryId == "id-new")));

        var issues = DirectorySyncService.ParseIssues(run.IssuesJson);
        Assert.Equal(2, issues.Count(i => i.Code == "missing-id"));
        Assert.Contains(issues, i => i.Code == "missing-username" && i.DirectoryId == "id-new");
        Assert.Contains(issues, i => i.Code == "duplicate-id" && i.Count == 2);
    }

    [Fact]
    public async Task Unmapped_site_value_syncs_person_without_site_and_is_reported()
    {
        using var f = await NewFactoryAsync();
        f.Directory.Entries = Entries.Many(3);
        f.Directory.Entries.Add(Entries.Person("id-x", "user.x", site: "Branch Nowhere"));
        f.Directory.Entries.Add(Entries.Person("id-y", "user.y", site: "branch nowhere"));
        f.Directory.Entries.Add(Entries.Person("id-z", "user.z", site: null));

        var run = await f.SyncAsync();

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal(6, run.Mapped);
        var x = await PersonAsync(f, "id-x");
        Assert.True(x.IsActive);
        Assert.Null(x.SiteId);
        var issues = DirectorySyncService.ParseIssues(run.IssuesJson);
        Assert.Contains(issues, i => i.Code == "unmapped-site" && i.Count == 2 && i.Message.Contains("Branch Nowhere"));
        Assert.Contains(issues, i => i.Code == "missing-site" && i.Count == 1);
    }

    [Fact]
    public async Task Site_mapping_is_case_insensitive_and_follows_admin_changes()
    {
        using var f = await NewFactoryAsync();
        f.Directory.Entries = [Entries.Person("id-a", "user.a", site: "HEAD OFFICE"), Entries.Person("id-b", "user.b", site: "Annex")];
        await f.SyncAsync();
        Assert.NotNull((await PersonAsync(f, "id-a")).SiteId);
        Assert.Null((await PersonAsync(f, "id-b")).SiteId);

        var annex = await f.WithDbAsync(db => Seed.SiteAsync(db, "ANX", "Annex"));
        var run = await f.SyncAsync();
        Assert.Equal(1, run.Updated);
        Assert.Equal(annex.Id, (await PersonAsync(f, "id-b")).SiteId);
    }

    [Fact]
    public async Task Sid_moving_to_another_directory_id_is_released_from_the_previous_person()
    {
        using var f = await NewFactoryAsync();
        f.Directory.Entries = Entries.Many(10);
        f.Directory.Entries[0] = Entries.Person("id-0", "user0", sid: "S-1-5-21-9-9-9-2000");
        await f.SyncAsync();

        f.Directory.Entries[0] = Entries.Person("id-0", "user0", sid: null);
        f.Directory.Entries.Add(Entries.Person("id-new", "user.new", sid: "S-1-5-21-9-9-9-2000"));
        var run = await f.SyncAsync();

        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Equal("S-1-5-21-9-9-9-2000", (await PersonAsync(f, "id-new")).Sid);
        Assert.Null((await PersonAsync(f, "id-0")).Sid);
    }

    [Fact]
    public async Task Status_reports_staleness_from_the_last_successful_run()
    {
        using var f = await NewFactoryAsync(new() { ["DirectorySync:StaleAfter"] = "06:00:00" });

        var none = await f.StatusAsync();
        Assert.True(none.IsStale);
        Assert.Null(none.LastSuccessAt);

        f.Directory.Entries = Entries.Many(3);
        var run = await f.SyncAsync();
        var fresh = await f.StatusAsync();
        Assert.False(fresh.IsStale);
        Assert.False(fresh.LastRunUnsuccessful);
        Assert.Equal(run.FinishedAt, fresh.LastSuccessAt);

        f.Clock.Advance(TimeSpan.FromHours(7));
        Assert.True((await f.StatusAsync()).IsStale);
    }

    [Fact]
    public async Task It_users_see_a_stale_or_failed_sync_warning_others_do_not()
    {
        using var f = await NewFactoryAsync();
        async Task<bool> WarningShown(TestUser u) =>
            (await (await f.CreateClientAs(u).GetAsync("/")).Content.ReadAsStringAsync()).Contains("sync-status-warning");

        Assert.True(await WarningShown(TestUser.It));
        Assert.False(await WarningShown(TestUser.Admin));
        Assert.False(await WarningShown(TestUser.Manager));

        f.Directory.Entries = Entries.Many(3);
        await f.SyncAsync();
        Assert.False(await WarningShown(TestUser.It));

        f.Directory.FailWith = new InvalidOperationException("LDAP down");
        await f.SyncAsync();
        Assert.True(await WarningShown(TestUser.It));
    }

    [Fact]
    public async Task A_run_already_in_progress_blocks_a_second_run()
    {
        using var f = await NewFactoryAsync();
        await f.WithDbAsync(async db =>
        {
            db.SyncRuns.Add(new SyncRun { StartedAt = f.Clock.UtcNow, Status = SyncRunStatus.Running, TriggeredBy = "other" });
            await db.SaveChangesAsync();
        });

        await Assert.ThrowsAsync<ConflictException>(() => f.SyncAsync());

        // An abandoned run (older than the running timeout) no longer blocks.
        f.Clock.Advance(TimeSpan.FromHours(2));
        f.Directory.Entries = Entries.Many(2);
        Assert.Equal(SyncRunStatus.Succeeded, (await f.SyncAsync()).Status);
    }
}
