using System.Net;
using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Privacy;
using AssetTracker.Tests.Directory;
using AssetTracker.Tests.Infrastructure;
using AssetTracker.Tests.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Ops;

public class PrivacyServiceTests : IClassFixture<AppFactory>
{
    private readonly AppFactory _f;
    public PrivacyServiceTests(AppFactory f) => _f = f;

    private Task<AnonymizationResult> AnonymizeAs(TestUser user, int personId, string? reason = "Client instruction PRIV-001") =>
        _f.RunAsAsync(user, sp => sp.GetRequiredService<PrivacyService>().AnonymizePersonAsync(personId, reason));

    /// <summary>An inactive person with one returned tagged loan, a manager grant and related outbox rows.</summary>
    private async Task<(Person Person, Loan Loan, long HolderRow, long ItRow, long PendingRow, long OtherRow)> ArrangeAsync(string key)
    {
        return await _f.WithDbAsync(async db =>
        {
            var site = await Seed.SiteAsync(db, key);
            var person = await Seed.PersonAsync(db, $"former.{key}", site, active: true);
            person.JobTitle = "Analyst";
            person.Sid = $"S-1-5-21-9-9-9-{Random.Shared.Next(100000, 999999)}";
            var loan = await NotificationSeed.TaggedLoanAsync(db, site, person, $"PRV-{key}", null);
            loan.ClosedAt = NotificationSeed.Started.AddDays(3);
            loan.ReturnedQuantity = 1;
            person.IsActive = false;
            db.ManagerSiteGrants.Add(new ManagerSiteGrant
            {
                PrincipalDirectoryId = person.DirectoryId, PrincipalName = person.Username, SiteId = site.Id,
                GrantedAt = NotificationSeed.Started, GrantedBy = "test",
            });
            await db.SaveChangesAsync();

            var holderRow = await NotificationSeed.OutboxRowAsync(db, person.Email!, NotificationSeed.Started);
            holderRow.Status = NotificationStatus.Sent;
            holderRow.LoanId = loan.Id;
            holderRow.Body = $"Dear {person.DisplayName}";
            var itRow = await NotificationSeed.OutboxRowAsync(db, $"it.{key}@example.test", NotificationSeed.Started);
            itRow.Status = NotificationStatus.Sent;
            itRow.LoanId = loan.Id;
            itRow.Body = $"Returned by {person.DisplayName}";
            var pendingRow = await NotificationSeed.OutboxRowAsync(db, person.Email!.ToUpperInvariant(), NotificationSeed.Started);
            var otherRow = await NotificationSeed.OutboxRowAsync(db, $"someone.else.{key}@example.test", NotificationSeed.Started);
            await db.SaveChangesAsync();
            return (person, loan, holderRow.Id, itRow.Id, pendingRow.Id, otherRow.Id);
        });
    }

    [Theory]
    [InlineData("It")]
    [InlineData("Manager")]
    [InlineData("Nobody")]
    public async Task Only_admins_can_anonymize(string userKey)
    {
        var user = userKey switch { "It" => TestUser.It, "Manager" => TestUser.Manager, _ => TestUser.Nobody };
        var (person, _, _, _, _, _) = await ArrangeAsync("pa" + userKey);

        await Assert.ThrowsAsync<AccessDeniedException>(() => AnonymizeAs(user, person.Id));
        var reloaded = await _f.WithDbAsync(db => db.People.SingleAsync(p => p.Id == person.Id));
        Assert.False(reloaded.IsAnonymized);
    }

    [Fact]
    public async Task Active_person_is_rejected()
    {
        var person = await _f.WithDbAsync(async db => await Seed.PersonAsync(db, "still.active", active: true));
        var ex = await Assert.ThrowsAsync<DomainException>(() => AnonymizeAs(TestUser.Admin, person.Id));
        Assert.Equal("privacy.person_active", ex.Code);
    }

    [Fact]
    public async Task Reason_is_required()
    {
        var (person, _, _, _, _, _) = await ArrangeAsync("preason");
        var ex = await Assert.ThrowsAsync<DomainException>(() => AnonymizeAs(TestUser.Admin, person.Id, "  "));
        Assert.Equal("privacy.reason_required", ex.Code);
    }

    [Fact]
    public async Task Person_with_open_loan_is_rejected()
    {
        var person = await _f.WithDbAsync(async db =>
        {
            var site = await Seed.SiteAsync(db, "popen");
            var p = await Seed.PersonAsync(db, "open.holder", site, active: false);
            await NotificationSeed.TaggedLoanAsync(db, site, p, "PRV-OPEN", null);
            return p;
        });
        var ex = await Assert.ThrowsAsync<ConflictException>(() => AnonymizeAs(TestUser.Admin, person.Id));
        Assert.Equal("privacy.open_loans", ex.Code);
    }

    [Fact]
    public async Task Anonymization_scrubs_person_loans_outbox_and_grants_and_is_audited()
    {
        var (person, loan, holderRow, itRow, pendingRow, otherRow) = await ArrangeAsync("pscrub");
        var eventsBefore = await _f.WithDbAsync(db => db.LoanEvents.Where(e => e.LoanId == loan.Id).CountAsync());

        var result = await AnonymizeAs(TestUser.Admin, person.Id);

        Assert.Equal(1, result.LoansScrubbed);
        Assert.Equal(3, result.OutboxRowsScrubbed);
        Assert.Equal(1, result.OutboxRowsSkipped);
        Assert.Equal(1, result.GrantsRemoved);
        await _f.WithDbAsync(async db =>
        {
            var p = await db.People.SingleAsync(x => x.Id == person.Id);
            var pseudonym = PrivacyService.PseudonymFor(person.Id);
            Assert.True(p.IsAnonymized);
            Assert.False(p.IsActive);
            Assert.Equal(pseudonym, p.Username);
            Assert.Equal(pseudonym, p.DisplayName);
            Assert.Null(p.Email);
            Assert.Null(p.JobTitle);
            Assert.Null(p.Sid);
            Assert.Null(p.SiteId);
            Assert.Equal(person.DirectoryId, p.DirectoryId);

            var l = await db.Loans.SingleAsync(x => x.Id == loan.Id);
            Assert.Equal(pseudonym, l.HolderNameSnapshot);
            Assert.Null(l.HolderEmailSnapshot);
            Assert.Equal(1, l.Quantity);
            Assert.Equal(1, l.ReturnedQuantity);
            Assert.NotNull(l.ClosedAt);
            Assert.Equal(eventsBefore, await db.LoanEvents.CountAsync(e => e.LoanId == loan.Id));

            var rows = await db.Outbox.Where(x => new[] { holderRow, itRow, pendingRow, otherRow }.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
            Assert.Null(rows[holderRow].RecipientEmail);
            Assert.Equal(PrivacyService.ScrubbedText, rows[holderRow].Body);
            Assert.Equal(NotificationStatus.Sent, rows[holderRow].Status);
            Assert.Equal($"it.pscrub@example.test", rows[itRow].RecipientEmail);
            Assert.DoesNotContain(person.DisplayName, rows[itRow].Body);
            Assert.Equal(NotificationStatus.Skipped, rows[pendingRow].Status);
            Assert.Null(rows[pendingRow].RecipientEmail);
            Assert.DoesNotContain(person.Email!, rows[pendingRow].DedupeKey, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Body", rows[otherRow].Body);
            Assert.Equal(NotificationStatus.Pending, rows[otherRow].Status);

            Assert.False(await db.ManagerSiteGrants.AnyAsync(g => g.PrincipalDirectoryId == person.DirectoryId));

            var audit = await db.AuditEvents.SingleAsync(a => a.Action == "privacy.anonymize" && a.EntityId == person.Id.ToString());
            Assert.Equal(TestUser.Admin.Name, audit.Actor);
            Assert.Equal("Client instruction PRIV-001", audit.Reason);
            Assert.DoesNotContain(person.Username, audit.DetailsJson);
        });

        var again = await Assert.ThrowsAsync<DomainException>(() => AnonymizeAs(TestUser.Admin, person.Id));
        Assert.Equal("privacy.already_anonymized", again.Code);
    }

    [Fact]
    public async Task Privacy_page_is_admin_only_and_anonymizes_with_confirmation()
    {
        var (person, _, _, _, _, _) = await ArrangeAsync("ppage");
        var admin = _f.CreateClientAs(TestUser.Admin);
        var url = "/Admin/Privacy?q=former.ppage";

        Assert.Equal(HttpStatusCode.Forbidden, (await _f.CreateClientAs(TestUser.It).GetAsync(url)).StatusCode);
        var html = await admin.GetStringAsync(url);
        Assert.Contains("former.ppage", html);

        var post = url + "&handler=Anonymize";
        var unconfirmed = await FormHelper.PostFormAsync(admin, url, post,
            ("personId", person.Id.ToString()), ("reason", "PRIV-002"));
        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);

        var ok = await FormHelper.PostFormAsync(admin, url, post,
            ("personId", person.Id.ToString()), ("reason", "PRIV-002"), ("confirm", "true"));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.True(await _f.WithDbAsync(db => db.People.Where(p => p.Id == person.Id).Select(p => p.IsAnonymized).SingleAsync()));
    }
}

/// <summary>Directory sync must never refresh or reactivate an anonymized person (own database per test).</summary>
public class PrivacySyncTests
{
    [Fact]
    public async Task Sync_does_not_overwrite_or_reactivate_an_anonymized_person()
    {
        using var f = new SyncFactory();
        await f.WithDbAsync(db => Seed.SiteAsync(db, "HO", "Head Office"));
        var entries = Entries.Many(9);
        entries.Add(Entries.Person("id-gone", "leaver"));
        f.Directory.Entries = entries;
        Assert.Equal(SyncRunStatus.Succeeded, (await f.SyncAsync()).Status);

        // The leaver is disabled in AD, then anonymized under client instruction.
        entries[^1] = Entries.Person("id-gone", "leaver", enabled: false);
        Assert.Equal(SyncRunStatus.Succeeded, (await f.SyncAsync()).Status);
        var id = await f.WithDbAsync(db => db.People.Where(p => p.DirectoryId == "id-gone").Select(p => p.Id).SingleAsync());
        await f.RunAsAsync(TestUser.Admin, sp => sp.GetRequiredService<PrivacyService>().AnonymizePersonAsync(id, "PRIV-003"));

        // Still disabled in AD: nothing is refreshed.
        var run = await f.SyncAsync();
        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        // Re-enabled and renamed in AD: still left anonymized and inactive, and reported.
        entries[^1] = Entries.Person("id-gone", "leaver.returned", enabled: true);
        run = await f.SyncAsync();
        Assert.Equal(SyncRunStatus.Succeeded, run.Status);
        Assert.Contains(Core.Directory.DirectorySyncService.ParseIssues(run.IssuesJson), i => i.Code == "anonymized-present");

        var p = await f.WithDbAsync(db => db.People.SingleAsync(x => x.Id == id));
        Assert.True(p.IsAnonymized);
        Assert.False(p.IsActive);
        Assert.Equal(PrivacyService.PseudonymFor(id), p.Username);
        Assert.Null(p.Email);
        Assert.Null(p.JobTitle);
        Assert.Equal(1, await f.WithDbAsync(db => db.People.CountAsync(x => x.DirectoryId == "id-gone")));
    }
}
