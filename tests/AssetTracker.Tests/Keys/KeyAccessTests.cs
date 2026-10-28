using System.Net;
using AssetTracker.Core.Common;
using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Keys;
using AssetTracker.Core.Privacy;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Keys;

public class KeyAccessTests(AppFactory f) : IClassFixture<AppFactory>
{
    private Task<T> Run<T>(Func<KeyAccessService, Task<T>> action, TestUser? user = null) =>
        f.RunAsAsync(user ?? TestUser.Facility, sp => action(sp.GetRequiredService<KeyAccessService>()));

    private Task<long> Revision() => f.WithDbAsync(db => db.KeyAccessRevisions.Select(x => x.Version).SingleAsync());

    private async Task<(Guid Building, Guid Room, Guid Definition, Guid Copy, int Person)> Setup()
    {
        var token = Guid.NewGuid().ToString("N")[..8];
        var site = await f.WithDbAsync(db => Seed.SiteAsync(db, token));
        var person = await f.WithDbAsync(db => Seed.PersonAsync(db, "key-" + token, site));
        var building = await Run(s => s.SaveBuildingAsync(null, site.Id, "Building"));
        var room = await Run(s => s.SaveRoomAsync(null, building, "101", "Finance"));
        var definition = await Run(s => s.SaveDefinitionAsync(null, building, "FIN", "Finance key"));
        var revision = await Revision();
        await Run(s => s.SetRoomAsync(definition, room, true, revision));
        var copy = await Run(s => s.SaveCopyAsync(null, definition, token, "Facility cabinet", null));
        return (building, room, definition, copy, person.Id);
    }

    private async Task<Guid> Issue(Guid copy, int person, DateOnly? due = null, bool reserve = false,
        DateTimeOffset? start = null, DateTimeOffset? end = null)
    {
        var rev = await Revision();
        return await Run(s => s.LendAsync(copy, person, due, "Test issue", reserve, start, end, LockOpeningMethod.Pin, rev));
    }

    [Theory]
    [InlineData("it")]
    [InlineData("manager")]
    [InlineData("admin")]
    [InlineData("nobody")]
    public async Task Only_facility_can_read_or_mutate_at_service_and_page_layers(string role)
    {
        var user = role switch { "it" => TestUser.It, "manager" => TestUser.Manager, "admin" => TestUser.Admin, _ => TestUser.Nobody };
        await Assert.ThrowsAsync<AccessDeniedException>(() => Run(s => s.BoardAsync(), user));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Run(s => s.SaveBuildingAsync(null, 1, "Forbidden"), user));
        using var client = f.CreateClientAs(user);
        foreach (var url in new[] { "/Keys", "/Keys/Register", "/Keys/Loans", "/Keys/Devices" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Master_access_is_transitive_overlapping_and_cycle_safe()
    {
        var x = await Setup();
        var wing = await Run(s => s.SaveDefinitionAsync(null, x.Building, "WING", "Wing master"));
        var grand = await Run(s => s.SaveDefinitionAsync(null, x.Building, "GRAND", "Grand master"));
        var shared = await Run(s => s.SaveDefinitionAsync(null, x.Building, "SHARED", "Overlapping master"));
        foreach (var pair in new[] { (wing, x.Definition), (grand, wing), (shared, x.Definition) })
        {
            var rev = await Revision(); await Run(s => s.SetMasterAsync(pair.Item1, pair.Item2, true, rev));
        }
        var board = await Run(s => s.BoardAsync());
        Assert.Contains(x.Room, board.RoomsFor(grand)); Assert.Contains(x.Room, board.RoomsFor(shared));
        var before = await Revision();
        await Assert.ThrowsAsync<DomainException>(() => Run(s => s.SetMasterAsync(x.Definition, grand, true, before)));
        Assert.Equal(before, await Revision()); // rejected transaction does not advance the write gate
        Assert.True(await f.WithDbAsync(db => db.AuditEvents.AnyAsync(a => a.Action == "keys.master-access" && a.DetailsJson!.Contains("before"))));
    }

    [Fact]
    public async Task Access_review_rejects_stale_checkout_and_topology_confirmation()
    {
        var x = await Setup(); var rev = await Revision();
        await Run(s => s.SaveRoomAsync(null, x.Building, "102", "Storage"));
        await Assert.ThrowsAsync<DomainException>(() => Run(s => s.LendAsync(x.Copy, x.Person, null, "Issue", false, expectedRevision: rev)));
        await Assert.ThrowsAsync<DomainException>(() => Run(s => s.SetRoomAsync(x.Definition, x.Room, false, rev)));
        Assert.False(await f.WithDbAsync(db => db.KeyLoans.AnyAsync(l => l.CopyId == x.Copy)));
    }

    [Fact]
    public async Task Reported_return_does_not_free_stock_and_verified_return_does()
    {
        var x = await Setup(); var loan = await Issue(x.Copy, x.Person);
        await Run(s => s.LoanActionAsync(loan, "report-return"));
        Assert.Equal(KeyCopyState.Issued, await f.WithDbAsync(db => db.KeyCopies.Where(c => c.Id == x.Copy).Select(c => c.State).SingleAsync()));
        await Assert.ThrowsAsync<DomainException>(() => Issue(x.Copy, x.Person));
        await Run(s => s.LoanActionAsync(loan, "verify-return", "Good — physically checked"));
        Assert.Equal(KeyCopyState.Available, await f.WithDbAsync(db => db.KeyCopies.Where(c => c.Id == x.Copy).Select(c => c.State).SingleAsync()));
        await Issue(x.Copy, x.Person);
        Assert.Equal(2, await f.WithDbAsync(db => db.KeyLoans.CountAsync(l => l.CopyId == x.Copy)));
        Assert.True(await f.WithDbAsync(db => db.Outbox.AnyAsync(o => o.DedupeKey.StartsWith("key-access:" + x.Person + ":"))));
    }

    [Fact]
    public async Task Closing_a_loss_does_not_record_a_return_or_make_the_key_available()
    {
        var x = await Setup(); var loan = await Issue(x.Copy, x.Person);
        await Run(s => s.LoanActionAsync(loan, "lost", "Missing"));
        await Run(s => s.LoanActionAsync(loan, "close-loss", "Locksmith case closed"));
        Assert.Equal(KeyCopyState.Lost, await f.WithDbAsync(db => db.KeyCopies.Where(c => c.Id == x.Copy).Select(c => c.State).SingleAsync()));
        var saved = await f.WithDbAsync(db => db.KeyLoans.AsNoTracking().SingleAsync(l => l.Id == loan));
        Assert.Equal(KeyLoanState.LostClosed, saved.State); Assert.Null(saved.ReceivedBy);
        await Assert.ThrowsAsync<DomainException>(() => Issue(x.Copy, x.Person));
    }

    [Fact]
    public async Task Lockbox_collection_needs_verified_access_and_explicit_physical_confirmation()
    {
        var x = await Setup();
        var box = await Run(s => s.SaveDeviceAsync(null, x.Building, null, "Key box", "Fictional", "Bluetooth box", "BOX-1", "Reception", DigitalLockKind.Lockbox, LockOpeningMethod.Pin | LockOpeningMethod.BluetoothApp));
        var tag = await f.WithDbAsync(db => db.KeyCopies.Where(c => c.Id == x.Copy).Select(c => c.Tag).SingleAsync());
        await Run(s => s.SaveCopyAsync(x.Copy, x.Definition, tag, "Reception", box));
        var loan = await Issue(x.Copy, x.Person, reserve: true, start: f.Clock.UtcNow.AddMinutes(-1), end: f.Clock.UtcNow.AddHours(2));
        await Assert.ThrowsAsync<DomainException>(() => Run(s => s.LoanActionAsync(loan, "collect")));
        var permission = await f.WithDbAsync(db => db.KeyLoans.Where(l => l.Id == loan).Select(l => l.PermissionId!.Value).SingleAsync());
        await Run(s => s.PermissionActionAsync(permission, "confirm", "Provider audit reference #1"));
        Assert.Equal(KeyLoanState.Reserved, await f.WithDbAsync(db => db.KeyLoans.Where(l => l.Id == loan).Select(l => l.State).SingleAsync()));
        await Run(s => s.LoanActionAsync(loan, "collect"));
        Assert.Equal(DigitalPermissionState.RevocationPending, await f.WithDbAsync(db => db.DigitalPermissions.Where(p => p.Id == permission).Select(p => p.State).SingleAsync()));
        await Run(s => s.LoanActionAsync(loan, "report-return"));
        Assert.Equal(KeyCopyState.Issued, await f.WithDbAsync(db => db.KeyCopies.Where(c => c.Id == x.Copy).Select(c => c.State).SingleAsync()));
    }

    [Fact]
    public async Task Digital_validation_and_revocation_do_not_claim_unverified_hardware_changes()
    {
        var x = await Setup();
        var device = await Run(s => s.SaveDeviceAsync(null, x.Building, x.Room, "Door", "Fictional", "PIN lock", "D-1", "Finance", DigitalLockKind.Door, LockOpeningMethod.Pin));
        await Assert.ThrowsAsync<DomainException>(() => Run(s => s.GrantAsync(device, x.Person, f.Clock.UtcNow, f.Clock.UtcNow.AddHours(1), LockOpeningMethod.BluetoothApp, "Unsupported")));
        await Assert.ThrowsAsync<DomainException>(() => Run(s => s.GrantAsync(device, x.Person, f.Clock.UtcNow, f.Clock.UtcNow, LockOpeningMethod.Pin, "Invalid window")));
        var p = await Run(s => s.GrantAsync(device, x.Person, f.Clock.UtcNow, f.Clock.UtcNow.AddHours(1), LockOpeningMethod.Pin, "Visit"));
        await Assert.ThrowsAsync<DomainException>(() => Run(s => s.PermissionActionAsync(p, "verify-revoke", "Not requested")));
        await Run(s => s.PermissionActionAsync(p, "confirm", "Provisioning receipt"));
        await Run(s => s.PermissionActionAsync(p, "request-revoke", null));
        await Run(s => s.PermissionActionAsync(p, "fail", "Offline device"));
        Assert.Equal(DigitalPermissionState.RevocationPending, await f.WithDbAsync(db => db.DigitalPermissions.Where(a => a.Id == p).Select(a => a.State).SingleAsync()));
        await Run(s => s.PermissionActionAsync(p, "verify-revoke", "Verified on device"));
        await Assert.ThrowsAsync<DomainException>(() => Run(s => s.PermissionActionAsync(p, "confirm", "Cannot reopen")));
    }

    [Fact]
    public async Task Inactive_user_access_enters_revocation_queue_and_blocks_privacy_until_resolved()
    {
        var x = await Setup(); var loan = await Issue(x.Copy, x.Person);
        var device = await Run(s => s.SaveDeviceAsync(null, x.Building, x.Room, "Door", "Fictional", "Lock", "D-2", "Finance", DigitalLockKind.Door, LockOpeningMethod.Pin));
        var p = await Run(s => s.GrantAsync(device, x.Person, f.Clock.UtcNow, f.Clock.UtcNow.AddHours(1), LockOpeningMethod.Pin, "Visit"));
        await f.WithDbAsync(async db => { (await db.People.SingleAsync(a => a.Id == x.Person)).IsActive = false; await db.SaveChangesAsync(); return 0; });
        await Assert.ThrowsAsync<DomainException>(() => Issue(x.Copy, x.Person));
        await f.RunAsAsync(TestUser.Facility, async sp => { await sp.GetRequiredService<KeyAccessMaintenance>().RunAsync(); return 0; });
        Assert.Equal(DigitalPermissionState.RevocationPending, await f.WithDbAsync(db => db.DigitalPermissions.Where(a => a.Id == p).Select(a => a.State).SingleAsync()));
        await Assert.ThrowsAsync<ConflictException>(() => f.RunAsAsync(TestUser.Admin, sp => sp.GetRequiredService<PrivacyService>().AnonymizePersonAsync(x.Person, "Client request")));
        await Run(s => s.LoanActionAsync(loan, "verify-return", "Good"));
        await Run(s => s.PermissionActionAsync(p, "verify-revoke", "Provider verified"));
        await f.RunAsAsync(TestUser.Admin, sp => sp.GetRequiredService<PrivacyService>().AnonymizePersonAsync(x.Person, "Client request"));
        Assert.False(await f.WithDbAsync(db => db.Outbox.AnyAsync(o => o.RecipientEmail != null && o.DedupeKey.StartsWith("key-access:" + x.Person + ":"))));
    }

    [Fact]
    public async Task Database_prevents_two_open_loans_for_one_copy()
    {
        var x = await Setup(); await Issue(x.Copy, x.Person);
        await Assert.ThrowsAsync<DbUpdateException>(() => f.WithDbAsync(async db =>
        {
            db.KeyLoans.Add(new KeyLoan { CopyId = x.Copy, PersonId = x.Person, State = KeyLoanState.OnLoan, CreatedAt = f.Clock.UtcNow });
            return await db.SaveChangesAsync();
        }));
    }

    [Fact]
    public async Task Facility_can_use_pages_and_forms_with_antiforgery()
    {
        var x = await Setup();
        using var client = f.CreateClientAs(TestUser.Facility);
        foreach (var url in new[] { "/Keys", "/Keys/Register", "/Keys/Loans", "/Keys/Devices", $"/Keys/Definition/{x.Definition}" })
        {
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain("style=", await response.Content.ReadAsStringAsync());
        }
        var board = await Run(s => s.BoardAsync());
        var post = await AntiforgeryHelper.PostFormAsync(client, "/Keys/Loans?copyId=" + x.Copy, new Dictionary<string, string>
        {
            ["copyId"] = x.Copy.ToString(), ["personId"] = x.Person.ToString(), ["reason"] = "Browser checkout", ["revision"] = board.Revision.ToString(),
        }, "/Keys/Loans?handler=Lend");
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.True(await f.WithDbAsync(db => db.KeyLoans.AnyAsync(l => l.CopyId == x.Copy)));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/Keys/Loans?handler=Action", new FormUrlEncodedContent(new Dictionary<string, string>()))).StatusCode);
    }

    [Fact]
    public async Task Expiry_requests_revocation_and_reminders_are_once_per_site_day()
    {
        var original = f.Clock.UtcNow;
        try
        {
            var x = await Setup();
            await Issue(x.Copy, x.Person, f.Clock.UtcToday);
            var device = await Run(s => s.SaveDeviceAsync(null, x.Building, x.Room, "Expiring door", "Fictional", "Lock", "EXP", "Finance", DigitalLockKind.Door, LockOpeningMethod.Pin));
            var permission = await Run(s => s.GrantAsync(device, x.Person, f.Clock.UtcNow, f.Clock.UtcNow.AddMinutes(1), LockOpeningMethod.Pin, "Temporary"));
            await Run(s => s.PermissionActionAsync(permission, "confirm", "Provider receipt"));
            f.Clock.Advance(TimeSpan.FromMinutes(2));
            async Task Tick() => await f.RunAsAsync(TestUser.Facility, async sp => { await sp.GetRequiredService<KeyAccessMaintenance>().RunAsync(); return 0; });
            await Tick();
            Assert.Equal(DigitalPermissionState.RevocationPending, await f.WithDbAsync(db => db.DigitalPermissions.Where(p => p.Id == permission).Select(p => p.State).SingleAsync()));
            var revision = await Revision();
            await Tick();
            Assert.Equal(revision, await Revision());
            Assert.Equal(1, await f.WithDbAsync(db => db.Outbox.CountAsync(o => o.DedupeKey.StartsWith("key-access:" + x.Person + ":reminder:"))));
        }
        finally { f.Clock.UtcNow = original; }
    }

    [Fact]
    public async Task Cross_building_links_and_unknown_records_are_rejected()
    {
        var a = await Setup(); var b = await Setup(); var revision = await Revision();
        await Assert.ThrowsAsync<DomainException>(() => Run(s => s.SetRoomAsync(a.Definition, b.Room, true, revision)));
        await Assert.ThrowsAsync<DomainException>(() => Run(s => s.SetMasterAsync(a.Definition, b.Definition, true, revision)));
        await Assert.ThrowsAsync<DomainException>(() => Run(s => s.LoanActionAsync(Guid.NewGuid(), "collect")));
    }
}
