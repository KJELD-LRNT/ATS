using System.Net;
using System.Text.Json;
using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Inventory;
using AssetTracker.Core.Overview;
using AssetTracker.Core.Reporting;
using AssetTracker.Core.Security;
using AssetTracker.Tests.Infrastructure;
using AssetTracker.Tests.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Overview;

/// <summary>
/// Overview tiles per role, the "needs attention" counts, due-soon, site-filtered figures and pool minimum stock.
/// Tests that change tile settings restore the defaults first (the class shares one database and runs serially).
/// </summary>
public class OverviewTests : IClassFixture<AppFactory>
{
    // The default clock is 2026-03-02.
    private static readonly DateOnly Today = new(2026, 3, 2);

    private readonly AppFactory _f;
    public OverviewTests(AppFactory f) => _f = f;

    private Task AsAdmin(Func<OverviewTileService, Task> action) =>
        _f.RunAsAsync(TestUser.Admin, sp => action(sp.GetRequiredService<OverviewTileService>()));

    private Task<T> AsIt<T>(Func<IServiceProvider, Task<T>> action) => _f.RunAsAsync(TestUser.It, action);

    private Task ResetTilesAsync() => AsAdmin(s => s.ResetAsync());

    private async Task<string> HtmlAsync(TestUser user, string url)
    {
        var res = await _f.CreateClientAs(user).GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadAsStringAsync();
    }

    // --- tile configuration ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Defaults_show_it_everything_but_go_to_and_managers_everything_but_attention()
    {
        await ResetTilesAsync();
        var (it, manager) = await _f.RunAsAsync(TestUser.It, async sp =>
        {
            var s = sp.GetRequiredService<OverviewTileService>();
            return (await s.VisibleForAsync(AppRoles.It), await s.VisibleForAsync(AppRoles.Manager));
        });

        Assert.Equal(OverviewTiles.All.Count - 1, it.Count);
        Assert.Contains(OverviewTileKeys.Attention, it);
        Assert.DoesNotContain(OverviewTileKeys.Attention, manager);
        Assert.Contains(OverviewTileKeys.Links, manager);
        Assert.DoesNotContain(OverviewTileKeys.Links, it);
    }

    [Fact]
    public async Task Save_switches_tiles_per_role_clamps_to_allowed_roles_and_audits()
    {
        await ResetTilesAsync();
        await AsAdmin(s => s.SaveAsync(new Dictionary<string, AppRoles>
        {
            [OverviewTileKeys.ByType] = AppRoles.Manager,
            [OverviewTileKeys.DueSoon] = AppRoles.None,
            // Attention is IT only: a manager flag is dropped.
            [OverviewTileKeys.Attention] = AppRoles.It | AppRoles.Manager,
        }));

        var config = await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<OverviewTileService>().GetRolesAsync());
        Assert.Equal(AppRoles.Manager, config[OverviewTileKeys.ByType]);
        Assert.Equal(AppRoles.None, config[OverviewTileKeys.DueSoon]);
        Assert.Equal(AppRoles.It, config[OverviewTileKeys.Attention]);
        // Untouched tiles keep their defaults.
        Assert.Equal(AppRoles.It | AppRoles.Manager, config[OverviewTileKeys.Overdue]);

        var visibleIt = await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<OverviewTileService>().VisibleForAsync(AppRoles.It));
        Assert.DoesNotContain(OverviewTileKeys.ByType, visibleIt);
        Assert.DoesNotContain(OverviewTileKeys.DueSoon, visibleIt);

        Assert.True(await _f.WithDbAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "overview.tiles")));
        await ResetTilesAsync();
    }

    [Fact]
    public async Task A_role_combination_sees_a_tile_when_any_of_its_roles_has_it()
    {
        await ResetTilesAsync();
        await AsAdmin(s => s.SaveAsync(new Dictionary<string, AppRoles> { [OverviewTileKeys.ByType] = AppRoles.Manager }));
        var visible = await _f.RunAsAsync(TestUser.It, sp =>
            sp.GetRequiredService<OverviewTileService>().VisibleForAsync(AppRoles.It | AppRoles.Manager));
        Assert.Contains(OverviewTileKeys.ByType, visible);
        await ResetTilesAsync();
    }

    [Fact]
    public async Task Unknown_tile_is_rejected_and_only_admins_can_change_tiles()
    {
        await Assert.ThrowsAsync<DomainException>(() =>
            AsAdmin(s => s.SaveAsync(new Dictionary<string, AppRoles> { ["nope"] = AppRoles.It })));

        var roles = new Dictionary<string, AppRoles> { [OverviewTileKeys.ByType] = AppRoles.None };
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<OverviewTileService>().SaveAsync(roles)));
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            _f.RunAsAsync(TestUser.Manager, sp => sp.GetRequiredService<OverviewTileService>().ResetAsync()));
    }

    [Fact]
    public async Task Reset_removes_the_settings_and_audits()
    {
        await AsAdmin(s => s.SaveAsync(new Dictionary<string, AppRoles> { [OverviewTileKeys.ByType] = AppRoles.None }));
        await ResetTilesAsync();

        Assert.False(await _f.WithDbAsync(db => db.Settings.AnyAsync(s => s.Key.StartsWith(OverviewTileService.KeyPrefix))));
        Assert.True(await _f.WithDbAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "overview.tiles.reset")));
    }

    // --- admin page ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Admin_tiles_page_is_for_admins_only()
    {
        Assert.Equal(HttpStatusCode.OK, (await _f.CreateClientAs(TestUser.Admin).GetAsync("/Admin/OverviewTiles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _f.CreateClientAs(TestUser.It).GetAsync("/Admin/OverviewTiles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _f.CreateClientAs(TestUser.Manager).GetAsync("/Admin/OverviewTiles")).StatusCode);
    }

    [Fact]
    public async Task Admin_saves_and_resets_tiles_through_the_page_and_the_overview_follows()
    {
        await ResetTilesAsync();
        var admin = _f.CreateClientAs(TestUser.Admin);

        // IT keeps only "Inventory by type"; managers get nothing but the find box.
        var save = await FormHelper.PostFormAsync(admin, "/Admin/OverviewTiles", "/Admin/OverviewTiles",
            ("It", OverviewTileKeys.ByType), ("Manager", OverviewTileKeys.Find));
        Assert.Equal(HttpStatusCode.Redirect, save.StatusCode);

        var itHtml = await HtmlAsync(TestUser.It, "/");
        Assert.Contains("Inventory by type", itHtml);
        Assert.DoesNotContain("Overdue and due today", itHtml);
        Assert.DoesNotContain("Pool stock", itHtml);
        Assert.DoesNotContain("Needs attention", itHtml);
        Assert.DoesNotContain("Open loans", itHtml);

        var mgrHtml = await HtmlAsync(TestUser.Manager, "/");
        Assert.DoesNotContain("Inventory by type", mgrHtml);
        Assert.Contains("overview-q", mgrHtml);

        var reset = await FormHelper.PostFormAsync(admin, "/Admin/OverviewTiles", "/Admin/OverviewTiles?handler=Reset");
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        var after = await HtmlAsync(TestUser.It, "/");
        Assert.Contains("Overdue and due today", after);
        Assert.Contains("Pool stock", after);
    }

    // --- the overview page -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Overview_shows_default_tiles_to_it_and_hides_attention_from_managers()
    {
        await ResetTilesAsync();
        var it = await HtmlAsync(TestUser.It, "/");
        Assert.Contains("Needs attention", it);
        Assert.Contains("Inventory by type", it);
        Assert.Contains("Due in the next", it);
        Assert.Contains("data-find-lookup", it);

        var manager = await HtmlAsync(TestUser.Manager, "/");
        Assert.DoesNotContain("Needs attention", manager);
        Assert.Contains("Inventory by type", manager);
        Assert.Contains("Go to", manager);
        Assert.DoesNotContain("data-find-lookup", manager);
    }

    [Fact]
    public async Task Overview_site_filter_limits_figures_and_ignores_unreadable_sites()
    {
        await ResetTilesAsync();
        var manager = new TestUser(@"CORP\mgr.ov01", "S-1-5-21-1000-1000-1000-3201", TestGroups.Manager);
        var (mine, theirs) = await _f.WithDbAsync(async db =>
        {
            var granted = await Seed.SiteAsync(db, "OV01");
            var other = await Seed.SiteAsync(db, "OV02");
            await Seed.ManagerWithGrantsAsync(db, manager, granted);
            var a = await Seed.AssetAsync(db, "OV-MINE-1", granted, state: AssetState.CheckedOut);
            var b = await Seed.AssetAsync(db, "OV-OTHER-1", other, state: AssetState.CheckedOut);
            var h1 = await ReportSeed.HolderAsync(db, "ov.mine", "Holder OV Mine", granted);
            var h2 = await ReportSeed.HolderAsync(db, "ov.other", "Holder OV Other", other);
            await ReportSeed.LoanAsync(db, h1, asset: a, due: Today.AddDays(-3));
            await ReportSeed.LoanAsync(db, h2, asset: b, due: Today.AddDays(-3));
            return (granted, other);
        });

        var all = await HtmlAsync(manager, "/");
        Assert.Contains("OV-MINE-1", all);
        Assert.DoesNotContain("OV-OTHER-1", all); // not in the manager's scope

        // A site outside the grants is ignored, not an error.
        var ignored = await HtmlAsync(manager, $"/?site={theirs.Id}");
        Assert.Contains("OV-MINE-1", ignored);
        Assert.DoesNotContain("OV-OTHER-1", ignored);

        var it = await HtmlAsync(TestUser.It, $"/?site={mine.Id}");
        Assert.Contains("OV-MINE-1", it);
        Assert.DoesNotContain("OV-OTHER-1", it);
        var itOther = await HtmlAsync(TestUser.It, $"/?site={theirs.Id}");
        Assert.Contains("OV-OTHER-1", itOther);
        Assert.DoesNotContain("OV-MINE-1", itOther);
    }

    [Fact]
    public async Task Overview_is_a_notice_for_users_without_inventory_access()
    {
        var html = await HtmlAsync(TestUser.Admin, "/");
        Assert.Contains("Welcome", html);
        Assert.DoesNotContain("Inventory by type", html);
    }

    // --- find box endpoint -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Find_returns_assets_and_people_for_it_and_forbids_others()
    {
        var site = await _f.WithDbAsync(db => Seed.SiteAsync(db, "OV03"));
        await _f.WithDbAsync(async db =>
        {
            await Seed.AssetAsync(db, "OVFIND-001", site);
            await ReportSeed.HolderAsync(db, "ovfind.person", "Ovfind Person", site);
        });

        var res = await _f.CreateClientAs(TestUser.It).GetAsync("/?handler=Find&q=ovfind");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var rows = doc.RootElement.EnumerateArray().ToList();
        Assert.Contains(rows, r => r.GetProperty("kind").GetString() == "asset" && r.GetProperty("key").GetString() == "OVFIND-001");
        Assert.Contains(rows, r => r.GetProperty("kind").GetString() == "person" && r.GetProperty("key").GetString() == "ovfind.person");

        var short1 = await _f.CreateClientAs(TestUser.It).GetStringAsync("/?handler=Find&q=o");
        Assert.Equal("[]", short1);

        Assert.Equal(HttpStatusCode.Forbidden, (await _f.CreateClientAs(TestUser.Manager).GetAsync("/?handler=Find&q=ovfind")).StatusCode);
    }

    // --- attention -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Attention_counts_leavers_unavailable_assets_pools_below_minimum_and_failed_mails_per_site()
    {
        var (a, b) = await _f.WithDbAsync(async db =>
        {
            var one = await Seed.SiteAsync(db, "OV04");
            var two = await Seed.SiteAsync(db, "OV05");
            var leaver = await ReportSeed.HolderAsync(db, "ov.leaver", "Ov Leaver", one, active: false);
            var stayer = await ReportSeed.HolderAsync(db, "ov.stayer", "Ov Stayer", one);
            var out1 = await Seed.AssetAsync(db, "OV-LV-1", one, state: AssetState.CheckedOut);
            var out2 = await Seed.AssetAsync(db, "OV-LV-2", one, state: AssetState.CheckedOut);
            await ReportSeed.LoanAsync(db, leaver, asset: out1, due: Today.AddDays(5));
            await ReportSeed.LoanAsync(db, stayer, asset: out2, due: Today.AddDays(5));
            await Seed.AssetAsync(db, "OV-UN-1", one, state: AssetState.Unavailable);
            await Seed.AssetAsync(db, "OV-UN-2", two, state: AssetState.Unavailable);
            var low = await Seed.PoolAsync(db, "OV-LOW", one, 2);
            low.MinimumStock = 5;
            var fine = await Seed.PoolAsync(db, "OV-FINE", one, 9);
            fine.MinimumStock = 5;
            await Seed.PoolAsync(db, "OV-NOMIN", one, 0);
            db.Outbox.Add(new NotificationOutbox
            {
                Kind = NotificationKind.SystemAlert, Status = NotificationStatus.Failed, DedupeKey = "ov-failed-1",
                RecipientEmail = "x@example.test", Subject = "s", Body = "b", CreatedAt = ReportSeed.Started,
                NextAttemptAt = ReportSeed.Started,
            });
            await db.SaveChangesAsync();
            return (one, two);
        });

        var siteOne = await AsIt(sp => sp.GetRequiredService<OverviewService>().GetAttentionAsync(a.Id));
        Assert.Equal(1, siteOne.InactiveHolderLoans);
        Assert.Equal(1, siteOne.UnavailableAssets);
        Assert.Equal(1, siteOne.PoolsBelowMinimum);
        Assert.True(siteOne.FailedMails >= 1);
        Assert.False(siteOne.IsEmpty);

        var siteTwo = await AsIt(sp => sp.GetRequiredService<OverviewService>().GetAttentionAsync(b.Id));
        Assert.Equal(0, siteTwo.InactiveHolderLoans);
        Assert.Equal(1, siteTwo.UnavailableAssets);
        Assert.Equal(0, siteTwo.PoolsBelowMinimum);

        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            _f.RunAsAsync(TestUser.Manager, sp => sp.GetRequiredService<OverviewService>().GetAttentionAsync()));
    }

    // --- due soon and the site-filtered summary --------------------------------------------------------------------

    [Fact]
    public async Task Due_soon_lists_loans_due_in_the_window_soonest_first_and_not_overdue_or_later_ones()
    {
        var site = await _f.WithDbAsync(async db =>
        {
            var s = await Seed.SiteAsync(db, "OV06");
            var h = await ReportSeed.HolderAsync(db, "ov.due", "Ov Due", s);
            foreach (var (tag, due) in new[]
                     {
                         ("OV-D-PAST", Today.AddDays(-1)), ("OV-D-TODAY", Today), ("OV-D-2", Today.AddDays(2)),
                         ("OV-D-7", Today.AddDays(7)), ("OV-D-8", Today.AddDays(8)),
                     })
            {
                var asset = await Seed.AssetAsync(db, tag, s, state: AssetState.CheckedOut);
                await ReportSeed.LoanAsync(db, h, asset: asset, due: due);
            }
            var noDue = await Seed.AssetAsync(db, "OV-D-NONE", s, state: AssetState.CheckedOut);
            await ReportSeed.LoanAsync(db, h, asset: noDue, due: null);
            return s;
        });

        var page = await AsIt(sp => sp.GetRequiredService<ReportService>().DueSoonAsync(new ReportFilter(SiteId: site.Id), 7));
        Assert.Equal(["OV-D-2", "OV-D-7"], page.Items.Select(r => r.AssetTag!).ToArray());
        Assert.Equal([2, 7], page.Items.Select(r => r.DaysUntilDue).ToArray());
    }

    [Fact]
    public async Task Summary_can_be_limited_to_one_site()
    {
        var (a, b) = await _f.WithDbAsync(async db =>
        {
            var one = await Seed.SiteAsync(db, "OV07");
            var two = await Seed.SiteAsync(db, "OV08");
            await Seed.AssetAsync(db, "OV-S-1", one);
            await Seed.AssetAsync(db, "OV-S-2", one);
            await Seed.AssetAsync(db, "OV-S-3", two);
            await Seed.PoolAsync(db, "OV-S-POOL", two, 4);
            return (one, two);
        });

        var sumA = await AsIt(sp => sp.GetRequiredService<ReportService>().GetSummaryAsync(a.Id));
        var sumB = await AsIt(sp => sp.GetRequiredService<ReportService>().GetSummaryAsync(b.Id));
        Assert.Equal(2, sumA.Assets.Sum(x => x.Count));
        Assert.Empty(sumA.Pools);
        Assert.Equal(1, sumB.Assets.Sum(x => x.Count));
        Assert.Equal(4, sumB.Pools.Sum(x => x.Available));
    }

    // --- pool minimum stock ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Pool_minimum_can_be_set_on_create_changed_cleared_and_validated_with_audit()
    {
        var site = await _f.WithDbAsync(db => Seed.SiteAsync(db, "OV09"));
        var pool = await AsIt(sp => sp.GetRequiredService<PoolService>()
            .CreateAsync(new CreatePoolInput(BuiltIn.Headset, "OV-MIN-1", site.Id, "Headset", 3, MinimumStock: 5)));
        Assert.Equal(5, pool.MinimumStock);
        Assert.True(pool.IsBelowMinimum);

        await AsIt(sp => sp.GetRequiredService<PoolService>().SetMinimumAsync(pool.Id, 2));
        var changed = await _f.WithDbAsync(db => db.Pools.AsNoTracking().SingleAsync(p => p.Id == pool.Id));
        Assert.Equal(2, changed.MinimumStock);
        Assert.False(changed.IsBelowMinimum);
        Assert.True(await _f.WithDbAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "pool.minimum" && e.EntityId == pool.Id.ToString())));

        await AsIt(sp => sp.GetRequiredService<PoolService>().SetMinimumAsync(pool.Id, null));
        Assert.Null((await _f.WithDbAsync(db => db.Pools.AsNoTracking().SingleAsync(p => p.Id == pool.Id))).MinimumStock);

        await Assert.ThrowsAsync<DomainException>(() => AsIt(sp => sp.GetRequiredService<PoolService>().SetMinimumAsync(pool.Id, -1)));
        await Assert.ThrowsAsync<DomainException>(() =>
            AsIt(sp => sp.GetRequiredService<PoolService>().SetMinimumAsync(pool.Id, PoolService.MinimumStockMax + 1)));
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            _f.RunAsAsync(TestUser.Manager, sp => sp.GetRequiredService<PoolService>().SetMinimumAsync(pool.Id, 1)));
    }

    [Fact]
    public async Task Pool_list_filters_below_minimum_and_details_page_saves_the_minimum()
    {
        var site = await _f.WithDbAsync(db => Seed.SiteAsync(db, "OV10"));
        var (low, fine) = await _f.WithDbAsync(async db =>
        {
            var l = await Seed.PoolAsync(db, "OV-LOWPOOL", site, 1);
            l.MinimumStock = 4;
            var f = await Seed.PoolAsync(db, "OV-FINEPOOL", site, 8);
            f.MinimumStock = 4;
            await db.SaveChangesAsync();
            return (l, f);
        });
        var client = _f.CreateClientAs(TestUser.It);

        var list = await client.GetStringAsync($"/Pools?low=true&siteId={site.Id}");
        Assert.Contains("OV-LOWPOOL", list);
        Assert.DoesNotContain("OV-FINEPOOL", list);

        var res = await FormHelper.PostFormAsync(client, $"/Pools/Details/{fine.Id}", $"/Pools/Details/{fine.Id}?handler=Minimum",
            ("minimum", "20"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal(20, (await _f.WithDbAsync(db => db.Pools.AsNoTracking().SingleAsync(p => p.Id == fine.Id))).MinimumStock);

        var below = await client.GetStringAsync($"/Pools?low=true&siteId={site.Id}");
        Assert.Contains("OV-FINEPOOL", below);
        Assert.Contains("OV-LOWPOOL", below);
    }

    [Fact]
    public async Task Loan_list_can_show_open_loans_held_by_people_who_left()
    {
        var site = await _f.WithDbAsync(async db =>
        {
            var s = await Seed.SiteAsync(db, "OV11");
            var leaver = await ReportSeed.HolderAsync(db, "ov.left", "Ov Left", s, active: false);
            var stayer = await ReportSeed.HolderAsync(db, "ov.here", "Ov Here", s);
            var a = await Seed.AssetAsync(db, "OV-LEFT-1", s, state: AssetState.CheckedOut);
            var b = await Seed.AssetAsync(db, "OV-HERE-1", s, state: AssetState.CheckedOut);
            await ReportSeed.LoanAsync(db, leaver, asset: a);
            await ReportSeed.LoanAsync(db, stayer, asset: b);
            return s;
        });

        var html = await _f.CreateClientAs(TestUser.It).GetStringAsync($"/Loans?inactive=true&siteId={site.Id}");
        Assert.Contains("OV-LEFT-1", html);
        Assert.DoesNotContain("OV-HERE-1", html);
    }
}
