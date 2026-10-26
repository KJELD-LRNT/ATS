using System.Net;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Inventory;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Lending;

public class LoanPagesTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public LoanPagesTests(AppFactory f) => _f = f;

    [Fact]
    public async Task Manager_cannot_post_checkout_or_open_mutation_pages()
    {
        var site = await _f.SiteAsync("LP01");
        var holder = await _f.PersonAsync("lp01.holder", site);
        var asset = await _f.AssetAsync("LP-0001", site);
        var client = _f.CreateClientAs(TestUser.Manager);

        var res = await client.PostAsync("/Loans/Checkout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.AssetId"] = asset.Id.ToString(), ["Input.HolderId"] = holder.Id.ToString(),
        }));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.False(await _f.WithDbAsync(db => db.Loans.AnyAsync(l => l.AssetId == asset.Id)));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/Loans/Checkout?who=lp01")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/Loans/Return/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/Loans/Correct/1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _f.CreateClientAs(TestUser.Admin).GetAsync("/Loans")).StatusCode);
    }

    [Fact]
    public async Task It_checks_out_returns_and_corrects_through_the_pages()
    {
        var site = await _f.SiteAsync("LP02");
        var holder = await _f.PersonAsync("lp02.holder", site);
        var other = await _f.PersonAsync("lp02.other", site);
        var pool = await _f.PoolAsync("HS-LP02", site, 5);
        var client = _f.CreateClientAs(TestUser.It);

        var form = $"/Loans/Checkout?poolId={pool.Id}&quantity=3&who=lp02.holder";
        var page = await (await client.GetAsync(form)).Content.ReadAsStringAsync();
        Assert.Contains("lp02.holder@example.test", page);
        Assert.Contains("stale", page); // no successful sync in this database

        var res = await FormHelper.PostFormAsync(client, form, "/Loans/Checkout",
            ("Input.PoolId", pool.Id.ToString()), ("Input.Quantity", "3"), ("Input.HolderId", holder.Id.ToString()),
            ("Input.DueDate", _f.Clock.LocalToday.AddDays(5).ToString("yyyy-MM-dd")));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var loan = await _f.WithDbAsync(db => db.Loans.AsNoTracking().SingleAsync(l => l.PoolId == pool.Id));
        Assert.Equal(3, loan.Quantity);

        var index = await (await client.GetAsync("/Loans?q=HS-LP02")).Content.ReadAsStringAsync();
        Assert.Contains($"#{loan.Id}", index);

        res = await FormHelper.PostFormAsync(client, $"/Loans/Return/{loan.Id}", $"/Loans/Return/{loan.Id}",
            ("Input.Quantity", "1"), ("Input.Condition", "Fine"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal(1, (await _f.ReloadLoanAsync(loan.Id)).ReturnedQuantity);

        // Correction: preview, then commit with a reason.
        var correctUrl = $"/Loans/Correct/{loan.Id}?who=lp02.other";
        var fields = new[]
        {
            ("Input.HolderId", other.Id.ToString()), ("Input.Quantity", "3"), ("Input.ReturnedQuantity", "1"),
            ("Input.DueDate", _f.Clock.LocalToday.AddDays(5).ToString("yyyy-MM-dd")),
        };
        res = await FormHelper.PostFormAsync(client, correctUrl, $"/Loans/Correct/{loan.Id}?handler=Preview", fields);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var preview = await res.Content.ReadAsStringAsync();
        Assert.Contains("Commit correction", preview);
        Assert.Contains("lp02.other", preview);

        res = await FormHelper.PostFormAsync(client, correctUrl, $"/Loans/Correct/{loan.Id}?handler=Commit",
            fields.Append(("Input.Reason", "Collected by a colleague")).ToArray());
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal(other.Id, (await _f.ReloadLoanAsync(loan.Id)).HolderId);

        var details = await (await client.GetAsync($"/Loans/Details/{loan.Id}")).Content.ReadAsStringAsync();
        Assert.Contains("superseded by", details);
        Assert.Contains("Collected by a colleague", details);
    }

    [Fact]
    public async Task Manager_sees_full_history_of_an_asset_at_a_granted_site_even_from_before_a_move()
    {
        var managerA = new TestUser(@"CORP\mgr.lp03a", "S-1-5-21-1000-1000-1000-3301", TestGroups.Manager);
        var managerB = new TestUser(@"CORP\mgr.lp03b", "S-1-5-21-1000-1000-1000-3302", TestGroups.Manager);
        var siteA = await _f.SiteAsync("LP03A");
        var siteB = await _f.SiteAsync("LP03B");
        await _f.WithDbAsync(async db =>
        {
            await Seed.ManagerWithGrantsAsync(db, managerA, siteA);
            await Seed.ManagerWithGrantsAsync(db, managerB, siteB);
        });
        var holder = await _f.PersonAsync("lp03.holder", siteA);
        var asset = await _f.AssetAsync("LP-0003", siteA);

        // Loan created while the asset was at site A, returned, then the asset moved to B.
        var loan = (await _f.CheckoutTaggedAsync(asset, holder)).Loan;
        await _f.AsItLending(s => s.ReturnAsync(loan.Id));
        await _f.RunAsAsync(TestUser.It, sp => sp.GetRequiredService<AssetService>().TransferSiteAsync(asset.Id, siteB.Id, "Moved"));

        var b = _f.CreateClientAs(managerB);
        var res = await b.GetAsync($"/Loans/Details/{loan.Id}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("lp03.holder@example.test", html);
        Assert.DoesNotContain("/Loans/Correct/", html);
        Assert.Contains($"/Loans/Details/{loan.Id}", await (await b.GetAsync($"/Assets/Details/{asset.Id}")).Content.ReadAsStringAsync());

        var a = _f.CreateClientAs(managerA);
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/Loans/Details/{loan.Id}")).StatusCode);
    }

    [Fact]
    public async Task Manager_loan_list_is_scoped_to_granted_sites()
    {
        var manager = new TestUser(@"CORP\mgr.lp04", "S-1-5-21-1000-1000-1000-3401", TestGroups.Manager);
        var granted = await _f.SiteAsync("LP04G");
        var other = await _f.SiteAsync("LP04X");
        await _f.WithDbAsync(db => Seed.ManagerWithGrantsAsync(db, manager, granted));
        var holder = await _f.PersonAsync("lp04.holder", granted);
        await _f.CheckoutPoolAsync(await _f.PoolAsync("HS-LP04G", granted, 2), holder, 1);
        await _f.CheckoutPoolAsync(await _f.PoolAsync("HS-LP04X", other, 2), holder, 1);

        var html = await (await _f.CreateClientAs(manager).GetAsync("/Loans?q=HS-LP04")).Content.ReadAsStringAsync();
        Assert.Contains("HS-LP04G", html);
        Assert.DoesNotContain("HS-LP04X", html);
    }
}
