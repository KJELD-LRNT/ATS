using System.Net;
using System.Text;
using System.Text.Json;
using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Reporting;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Reporting;

public class ReportTests : IClassFixture<AppFactory>
{
    private static readonly DateOnly LongAgo = new(2026, 2, 1); // overdue relative to the default clock (2026-03-02)

    private readonly AppFactory _f;
    public ReportTests(AppFactory f) => _f = f;

    private static readonly string[] Pages = ["/Reports", "/Reports/Inventory", "/Reports/Inventory?view=pools",
        "/Reports/Assignments", "/Reports/Overdue", "/Reports/History"];

    private static readonly string[] Exports = ["/Reports/Inventory?handler=Export", "/Reports/Assignments?handler=Export",
        "/Reports/Overdue?handler=Export", "/Reports/History?handler=Export", "/Reports/History?handler=ExportEvents"];

    private static async Task<string> GetCsvAsync(HttpClient client, string url)
    {
        var res = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/csv", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", res.Content.Headers.ContentType?.CharSet);
        Assert.EndsWith(".csv", res.Content.Headers.ContentDisposition?.FileNameStar ?? res.Content.Headers.ContentDisposition?.FileName);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        return Encoding.UTF8.GetString(bytes[3..]);
    }

    private sealed record TwoSites(Site Mine, Site Other, Pool MinePool, Pool OtherPool);

    /// <summary>Granted site with tagged + pool loans (overdue), and another site with the same.</summary>
    private async Task<TwoSites> SeedTwoSitesAsync(string prefix, TestUser manager)
    {
        return await _f.WithDbAsync(async db =>
        {
            var mine = await Seed.SiteAsync(db, prefix + "1");
            var other = await Seed.SiteAsync(db, prefix + "2");
            await Seed.ManagerWithGrantsAsync(db, manager, mine);
            var holderMine = await ReportSeed.HolderAsync(db, prefix.ToLowerInvariant() + ".mine", $"Holder {prefix} Mine", mine);
            var holderOther = await ReportSeed.HolderAsync(db, prefix.ToLowerInvariant() + ".other", $"Holder {prefix} Other", other);
            var a1 = await Seed.AssetAsync(db, $"{prefix}-MINE-1", mine, state: AssetState.CheckedOut);
            var a2 = await Seed.AssetAsync(db, $"{prefix}-OTHER-1", other, state: AssetState.CheckedOut);
            var p1 = await Seed.PoolAsync(db, $"SKU-{prefix}-MINE", mine, 5);
            var p2 = await Seed.PoolAsync(db, $"SKU-{prefix}-OTHER", other, 5);
            await ReportSeed.LoanAsync(db, holderMine, asset: a1, due: LongAgo);
            await ReportSeed.LoanAsync(db, holderOther, asset: a2, due: LongAgo);
            await ReportSeed.LoanAsync(db, holderMine, pool: p1, quantity: 2, due: LongAgo);
            await ReportSeed.LoanAsync(db, holderOther, pool: p2, quantity: 2, due: LongAgo);
            return new TwoSites(mine, other, p1, p2);
        });
    }

    [Fact]
    public async Task Manager_sees_only_granted_sites_in_every_report_and_export()
    {
        var manager = new TestUser(@"CORP\mgr.rs", "S-1-5-21-1000-1000-1000-4101", TestGroups.Manager);
        var s = await SeedTwoSitesAsync("RS", manager);
        var client = _f.CreateClientAs(manager);

        foreach (var url in Pages)
        {
            var sep = url.Contains('?') ? '&' : '?';
            var html = await (await client.GetAsync($"{url}{sep}q=RS")).Content.ReadAsStringAsync();
            Assert.DoesNotContain("RS-OTHER", html);
            Assert.DoesNotContain("Holder RS Other", html);
        }

        foreach (var url in Exports)
        {
            var csv = await GetCsvAsync(client, url);
            Assert.DoesNotContain("RS-OTHER", csv);
            Assert.DoesNotContain("Holder RS Other", csv);
            Assert.DoesNotContain("rs.other", csv);
            Assert.Contains("RS-MINE", csv);
        }

        // Service level too.
        await _f.RunAsAsync(manager, async sp =>
        {
            var r = sp.GetRequiredService<ReportService>();
            var all = new ReportFilter(Query: "RS", PageSize: 200);
            Assert.All((await r.InventoryAssetsAsync(all)).Items, x => Assert.Equal(s.Mine.Id, x.SiteId));
            Assert.All((await r.PoolBalancesAsync(all)).Items, x => Assert.Equal(s.Mine.Id, x.SiteId));
            Assert.Equal(2, (await r.AssignmentsAsync(all)).TotalCount);
            Assert.Equal(2, (await r.OverdueAsync(all)).TotalCount);
            Assert.Equal(2, (await r.HistoryAsync(all)).TotalCount);
            var summary = await r.GetSummaryAsync();
            Assert.DoesNotContain(summary.Assets, x => x.SiteKey == "RS2");
            Assert.DoesNotContain(summary.Pools, x => x.SiteKey == "RS2");
        });
    }

    [Fact]
    public async Task Manipulated_site_filter_does_not_leak_ungranted_site()
    {
        var manager = new TestUser(@"CORP\mgr.ru", "S-1-5-21-1000-1000-1000-4102", TestGroups.Manager);
        var s = await SeedTwoSitesAsync("RU", manager);
        var client = _f.CreateClientAs(manager);

        foreach (var url in Pages.Concat(Exports))
        {
            var sep = url.Contains('?') ? '&' : '?';
            var res = await client.GetAsync($"{url}{sep}siteId={s.Other.Id}");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var body = await res.Content.ReadAsStringAsync();
            Assert.DoesNotContain("RU-OTHER", body);
            Assert.DoesNotContain("Holder RU Other", body);
        }

        var csv = await GetCsvAsync(client, $"/Reports/Assignments?handler=Export&siteId={s.Other.Id}");
        Assert.Single(CsvTestParser.Parse(csv)); // header only
    }

    [Fact]
    public async Task It_sees_all_sites()
    {
        var manager = new TestUser(@"CORP\mgr.rt", "S-1-5-21-1000-1000-1000-4103", TestGroups.Manager);
        await SeedTwoSitesAsync("RT", manager);
        var client = _f.CreateClientAs(TestUser.It);

        foreach (var url in Exports)
        {
            var sep = url.Contains('?') ? '&' : '?';
            var csv = await GetCsvAsync(client, $"{url}{sep}q=RT");
            Assert.Contains("RT-MINE", csv);
            Assert.Contains("RT-OTHER", csv);
        }
        var html = await (await client.GetAsync("/Reports/Assignments?q=RT")).Content.ReadAsStringAsync();
        Assert.Contains("RT-OTHER-1", html);
        Assert.Contains("SKU-RT-MINE", html);
    }

    [Fact]
    public async Task Report_pages_render_export_links_that_preserve_filters()
    {
        var client = _f.CreateClientAs(TestUser.It);
        var html = await (await client.GetAsync("/Reports/Overdue?q=abc&from=2026-01-01")).Content.ReadAsStringAsync();
        Assert.Contains("href=\"/Reports/Overdue?q=abc&amp;from=2026-01-01&amp;handler=Export\"", html);

        var history = await (await client.GetAsync("/Reports/History?type=Laptop")).Content.ReadAsStringAsync();
        Assert.Contains("type=Laptop&amp;handler=ExportEvents", history);

        var inventory = await (await client.GetAsync("/Reports/Inventory?view=pools&state=Available")).Content.ReadAsStringAsync();
        Assert.Contains("state=Available&amp;view=pools&amp;handler=Export", inventory);
    }

    [Fact]
    public async Task Admin_only_and_plain_users_get_403_on_reports_and_exports()
    {
        foreach (var user in new[] { TestUser.Admin, TestUser.Nobody })
        {
            var client = _f.CreateClientAs(user);
            foreach (var url in Pages.Concat(Exports))
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(url)).StatusCode);
        }

        await _f.RunAsAsync(TestUser.Admin, async sp =>
        {
            var r = sp.GetRequiredService<ReportService>();
            await Assert.ThrowsAsync<AccessDeniedException>(() => r.GetSummaryAsync());
            await Assert.ThrowsAsync<AccessDeniedException>(() => r.HistoryAsync(new ReportFilter()));
            await Assert.ThrowsAsync<AccessDeniedException>(() => r.ExportInventoryAsync(new ReportFilter()));
        });
        Assert.False(await _f.WithDbAsync(db =>
            db.AuditEvents.AnyAsync(e => e.Action.StartsWith("export.") && e.Actor == TestUser.Admin.Name)));
    }

    [Fact]
    public async Task Manager_of_new_site_sees_prior_holders_after_asset_moved_in_but_old_site_manager_does_not()
    {
        var mgrA = new TestUser(@"CORP\mgr.rva", "S-1-5-21-1000-1000-1000-4104", TestGroups.Manager);
        var mgrB = new TestUser(@"CORP\mgr.rvb", "S-1-5-21-1000-1000-1000-4105", TestGroups.Manager);
        await _f.WithDbAsync(async db =>
        {
            var a = await Seed.SiteAsync(db, "RVA");
            var b = await Seed.SiteAsync(db, "RVB");
            await Seed.ManagerWithGrantsAsync(db, mgrA, a);
            await Seed.ManagerWithGrantsAsync(db, mgrB, b);
            var prior = await ReportSeed.HolderAsync(db, "prior.rv", "Prior Holder Rv", a, jobTitle: "Field Engineer", active: false);
            var asset = await Seed.AssetAsync(db, "RV-MOVED-1", a);
            // Loan taken and returned while the asset was at site A ...
            await ReportSeed.LoanAsync(db, prior, asset: asset, closedAt: ReportSeed.Started.AddDays(10));
            // ... then the asset moved to site B.
            asset.SiteId = b.Id;
            await db.SaveChangesAsync();
        });

        var csvB = await GetCsvAsync(_f.CreateClientAs(mgrB), "/Reports/History?handler=Export&q=RV-MOVED");
        var row = Assert.Single(CsvTestParser.ParseWithHeader(csvB));
        Assert.Equal("RV-MOVED-1", row["Asset tag"]);
        Assert.Equal("Prior Holder Rv", row["Holder"]);
        Assert.Equal("prior.rv", row["Holder username"]);
        Assert.Equal("prior.rv@example.test", row["Holder email"]);
        Assert.Equal("Field Engineer", row["Holder job title"]);
        Assert.Equal("RVA", row["Holder site"]);
        Assert.Equal("No", row["Holder active"]);
        Assert.Equal("RVB", row["Site"]);
        Assert.Equal("Returned", row["Status"]);

        var events = CsvTestParser.ParseWithHeader(
            await GetCsvAsync(_f.CreateClientAs(mgrB), "/Reports/History?handler=ExportEvents&q=RV-MOVED"));
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal("Prior Holder Rv", e["Holder"]));

        var clientA = _f.CreateClientAs(mgrA);
        foreach (var url in Exports.Concat(Pages))
        {
            var sep = url.Contains('?') ? '&' : '?';
            var body = await (await clientA.GetAsync($"{url}{sep}q=RV")).Content.ReadAsStringAsync();
            Assert.DoesNotContain("RV-MOVED-1", body);
            Assert.DoesNotContain("Prior Holder Rv", body);
        }
    }

    [Fact]
    public async Task Overdue_report_follows_overdue_policy_in_each_sites_timezone()
    {
        // Two sites with the same loans: one on the default timezone (UTC in tests), one at UTC+14.
        async Task<Site> SeedSiteAsync(string key, string? tz) => await _f.WithDbAsync(async db =>
        {
            var s = await Seed.SiteAsync(db, key, timeZoneId: tz);
            var h = await ReportSeed.HolderAsync(db, key.ToLowerInvariant() + ".holder", "Holder " + key, s);
            await ReportSeed.LoanAsync(db, h, asset: await Seed.AssetAsync(db, key + "-DUE-0304", s), due: new DateOnly(2026, 3, 4));
            await ReportSeed.LoanAsync(db, h, asset: await Seed.AssetAsync(db, key + "-DUE-0303", s), due: new DateOnly(2026, 3, 3));
            await ReportSeed.LoanAsync(db, h, asset: await Seed.AssetAsync(db, key + "-DUE-0302", s), due: new DateOnly(2026, 3, 2));
            await ReportSeed.LoanAsync(db, h, asset: await Seed.AssetAsync(db, key + "-DUE-0301", s), due: new DateOnly(2026, 3, 1));
            await ReportSeed.LoanAsync(db, h, asset: await Seed.AssetAsync(db, key + "-UNDATED", s));
            await ReportSeed.LoanAsync(db, h, asset: await Seed.AssetAsync(db, key + "-RETURNED", s),
                due: new DateOnly(2026, 2, 1), closedAt: ReportSeed.Started.AddDays(1));
            return s;
        });
        var utcSite = await SeedSiteAsync("ROU", null);
        var plus14Site = await SeedSiteAsync("ROK", "Pacific/Kiritimati");

        var previousNow = _f.Clock.UtcNow;
        try
        {
            // 2026-03-02 12:00 UTC. D4: overdue from the due date itself; days overdue = days past due (0 = due today).
            _f.Clock.UtcNow = new DateTimeOffset(2026, 3, 2, 12, 0, 0, TimeSpan.Zero);

            // Default (UTC) site: still the 2nd, so items due on the 1st (1 day) and the 2nd (due today) are overdue.
            Assert.Equal([("ROU-DUE-0301", 1), ("ROU-DUE-0302", 0)], await OverdueTagsAsync(utcSite.Id));

            // UTC+14 site: already 2026-03-03 locally, so the item due on the 3rd is due today there.
            Assert.Equal([("ROK-DUE-0301", 2), ("ROK-DUE-0302", 1), ("ROK-DUE-0303", 0)], await OverdueTagsAsync(plus14Site.Id));

            var csv = await GetCsvAsync(_f.CreateClientAs(TestUser.It), $"/Reports/Overdue?handler=Export&siteId={plus14Site.Id}");
            var rows = CsvTestParser.ParseWithHeader(csv);
            Assert.Equal(["ROK-DUE-0301", "ROK-DUE-0302", "ROK-DUE-0303"], rows.Select(r => r["Asset tag"]));
            Assert.Equal(["2", "1", "0"], rows.Select(r => r["Days overdue"]));

            // The landing page summary and the loans list use the same per-site rule.
            var html = await (await _f.CreateClientAs(TestUser.It).GetAsync($"/Loans?overdue=true&siteId={plus14Site.Id}"))
                .Content.ReadAsStringAsync();
            Assert.Contains("ROK-DUE-0303", html);
            Assert.Contains("(due today)", html);
            Assert.DoesNotContain("ROK-DUE-0304", html);
        }
        finally
        {
            _f.Clock.UtcNow = previousNow;
        }
    }

    private Task<List<(string, int)>> OverdueTagsAsync(int siteId) =>
        _f.RunAsAsync(TestUser.It, async sp =>
            (await sp.GetRequiredService<ReportService>().OverdueAsync(new ReportFilter(SiteId: siteId)))
            .Items.Select(r => (r.AssetTag!, r.DaysOverdue!.Value)).ToList());

    [Fact]
    public async Task Partially_returned_pool_loan_shows_outstanding_quantity_and_stays_overdue()
    {
        var site = await _f.WithDbAsync(async db =>
        {
            var s = await Seed.SiteAsync(db, "RP1");
            var h = await ReportSeed.HolderAsync(db, "rp.holder", "Holder Rp", s);
            var pool = await Seed.PoolAsync(db, "SKU-RP-HS", s, 10);
            await ReportSeed.LoanAsync(db, h, pool: pool, quantity: 5, returned: 2, due: LongAgo);
            return s;
        });
        var client = _f.CreateClientAs(TestUser.It);

        foreach (var url in new[] { "/Reports/Assignments?handler=Export", "/Reports/Overdue?handler=Export" })
        {
            var row = Assert.Single(CsvTestParser.ParseWithHeader(await GetCsvAsync(client, $"{url}&siteId={site.Id}")));
            Assert.Equal("Pool", row["Kind"]);
            Assert.Equal("SKU-RP-HS", row["Pool SKU"]);
            Assert.Equal("5", row["Quantity"]);
            Assert.Equal("2", row["Returned"]);
            Assert.Equal("3", row["Outstanding"]);
        }

        var page = await _f.RunAsAsync(TestUser.It, sp =>
            sp.GetRequiredService<ReportService>().AssignmentsAsync(new ReportFilter(SiteId: site.Id)));
        Assert.Equal(3, Assert.Single(page.Items).OutstandingQuantity);
    }

    [Fact]
    public async Task Export_writes_audit_event_with_filters_and_row_count()
    {
        var manager = new TestUser(@"CORP\mgr.ra", "S-1-5-21-1000-1000-1000-4106", TestGroups.Manager);
        var s = await SeedTwoSitesAsync("RA", manager);
        var client = _f.CreateClientAs(manager);

        await GetCsvAsync(client, $"/Reports/Assignments?handler=Export&q=RA&siteId={s.Mine.Id}");
        await GetCsvAsync(client, "/Reports/Inventory?handler=Export&q=RA");
        await GetCsvAsync(client, "/Reports/Overdue?handler=Export&q=RA");
        await GetCsvAsync(client, "/Reports/History?handler=Export&q=RA");
        await GetCsvAsync(client, "/Reports/History?handler=ExportEvents&q=RA");

        var events = await _f.WithDbAsync(db =>
            db.AuditEvents.Where(e => e.Actor == manager.Name).OrderBy(e => e.Id).ToListAsync());
        Assert.Equal(["export.assignments", "export.inventory", "export.overdue", "export.history", "export.history"],
            events.Select(e => e.Action));

        var first = events[0];
        Assert.Equal(s.Mine.Id, first.SiteId);
        using var details = JsonDocument.Parse(first.DetailsJson!);
        Assert.Equal(2, details.RootElement.GetProperty("RowCount").GetInt32());
        Assert.Equal("RA", details.RootElement.GetProperty("Filters").GetProperty("Query").GetString());

        // Inventory: 1 visible tagged asset + 1 visible pool.
        using var inv = JsonDocument.Parse(events[1].DetailsJson!);
        Assert.Equal(2, inv.RootElement.GetProperty("RowCount").GetInt32());
        // Event-level history: 2 visible loans with one CheckedOut event each.
        using var ev = JsonDocument.Parse(events[4].DetailsJson!);
        Assert.Equal(2, ev.RootElement.GetProperty("RowCount").GetInt32());
        Assert.Equal("events", ev.RootElement.GetProperty("Level").GetString());
    }

    [Fact]
    public async Task Malicious_holder_name_is_neutralized_in_csv()
    {
        var site = await _f.WithDbAsync(async db =>
        {
            var s = await Seed.SiteAsync(db, "RM1");
            var h = await ReportSeed.HolderAsync(db, "rm.holder", "=cmd|' /C calc'!A0", s, jobTitle: "@SUM(1)");
            await ReportSeed.LoanAsync(db, h, asset: await Seed.AssetAsync(db, "RM-1", s), due: LongAgo);
            return s;
        });
        var client = _f.CreateClientAs(TestUser.It);

        foreach (var url in new[] { "/Reports/Assignments?handler=Export", "/Reports/Overdue?handler=Export",
                     "/Reports/History?handler=Export", "/Reports/Inventory?handler=Export" })
        {
            var csv = await GetCsvAsync(client, $"{url}&siteId={site.Id}");
            var row = Assert.Single(CsvTestParser.ParseWithHeader(csv));
            var holder = row.TryGetValue("Holder", out var h) ? h : row["Current holder"];
            Assert.Equal("'=cmd|' /C calc'!A0", holder);
            if (row.TryGetValue("Holder job title", out var title)) Assert.Equal("'@SUM(1)", title);
            Assert.DoesNotContain(CsvTestParser.Parse(csv).SelectMany(r => r), c => c.StartsWith('='));
        }
    }

    [Fact]
    public async Task History_reports_corrections_and_event_export_links_superseded_events()
    {
        var (site, correction) = await _f.WithDbAsync(async db =>
        {
            var s = await Seed.SiteAsync(db, "RC1");
            var h = await ReportSeed.HolderAsync(db, "rc.holder", "Holder Rc", s);
            var loan = await ReportSeed.LoanAsync(db, h, asset: await Seed.AssetAsync(db, "RC-1", s),
                closedAt: ReportSeed.Started.AddDays(2));
            await ReportSeed.CorrectionAsync(db, loan, "Wrong start date");
            return (s, await ReportSeed.CorrectionAsync(db, loan, "Second fix"));
        });

        var history = await _f.RunAsAsync(TestUser.It, sp =>
            sp.GetRequiredService<ReportService>().HistoryAsync(new ReportFilter(SiteId: site.Id)));
        var loanRow = Assert.Single(history.Items);
        Assert.Equal(2, loanRow.CorrectionCount);
        Assert.Equal("Second fix", loanRow.LatestCorrectionReason);

        var client = _f.CreateClientAs(TestUser.It);
        var events = CsvTestParser.ParseWithHeader(
            await GetCsvAsync(client, $"/Reports/History?handler=ExportEvents&siteId={site.Id}"));
        Assert.Equal(["CheckedOut", "Returned", "Corrected", "Corrected"], events.Select(e => e["Event"]));
        var original = events[0];
        Assert.Equal(correction.Id.ToString(), original["Superseded by event"]);
        Assert.Equal(original["Event id"], events[3]["Supersedes event"]);
        Assert.Equal("Second fix", events[3]["Reason"]);
        Assert.Equal(@"CORP\it.user", events[3]["Actor"]);

        var html = await (await client.GetAsync($"/Reports/History?siteId={site.Id}")).Content.ReadAsStringAsync();
        Assert.Contains("Second fix", html);
    }

    [Fact]
    public async Task History_date_range_and_holder_search_filter_server_side()
    {
        var site = await _f.WithDbAsync(async db =>
        {
            var s = await Seed.SiteAsync(db, "RD1");
            var h1 = await ReportSeed.HolderAsync(db, "rd.alpha", "Alpha Rd", s);
            var h2 = await ReportSeed.HolderAsync(db, "rd.beta", "Beta Rd", s);
            await ReportSeed.LoanAsync(db, h1, asset: await Seed.AssetAsync(db, "RD-1", s),
                started: new DateTimeOffset(2026, 1, 10, 23, 30, 0, TimeSpan.Zero), closedAt: new DateTimeOffset(2026, 1, 12, 0, 0, 0, TimeSpan.Zero));
            await ReportSeed.LoanAsync(db, h2, asset: await Seed.AssetAsync(db, "RD-2", s),
                started: new DateTimeOffset(2026, 1, 11, 0, 0, 0, TimeSpan.Zero));
            return s;
        });

        await _f.RunAsAsync(TestUser.It, async sp =>
        {
            var r = sp.GetRequiredService<ReportService>();
            var jan10 = await r.HistoryAsync(new ReportFilter(SiteId: site.Id, From: new(2026, 1, 10), To: new(2026, 1, 10)));
            Assert.Equal("RD-1", Assert.Single(jan10.Items).AssetTag);
            var beta = await r.HistoryAsync(new ReportFilter(Query: "rd.beta", SiteId: site.Id));
            Assert.Equal("RD-2", Assert.Single(beta.Items).AssetTag);
            var paged = await r.HistoryAsync(new ReportFilter(SiteId: site.Id, Page: 2, PageSize: 1));
            Assert.Equal(2, paged.TotalCount);
            Assert.Equal("RD-1", Assert.Single(paged.Items).AssetTag); // newest first
        });
    }
}
