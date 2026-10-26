using System.Net;
using AssetTracker.Core.Abstractions;
using AssetTracker.Core.Common;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Settings;
using AssetTracker.Infrastructure.Mail;
using AssetTracker.Tests.Directory;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AssetTracker.Tests.Settings;

/// <summary>Validation, authorization, auditing, the write-only SMTP password, and the test buttons.</summary>
public class SettingsServiceTests(SettingsFactory f) : IClassFixture<SettingsFactory>
{
    public static TheoryData<string, string, string> Invalid => new()
    {
        { SettingSections.Directory, "ActiveDirectory:SearchRoot", "OU=Staff,DC=example,DC=test" },
        { SettingSections.Directory, "ActiveDirectory:SearchRoot", "LDAP://" },
        { SettingSections.Directory, "ActiveDirectory:Filter", "(&(objectClass=user)" },
        { SettingSections.Directory, "ActiveDirectory:Filter", "objectClass=user" },
        { SettingSections.Directory, "ActiveDirectory:Filter", "(a=1))(" },
        { SettingSections.Directory, "ActiveDirectory:SiteAttribute", "office name" },
        { SettingSections.Directory, "ActiveDirectory:PageSize", "0" },
        { SettingSections.Directory, "ActiveDirectory:PageSize", "1001" },
        { SettingSections.Directory, "ActiveDirectory:ClientTimeout", "-00:01:00" },
        { SettingSections.Sync, "DirectorySync:Interval", "soon" },
        { SettingSections.Sync, "DirectorySync:Interval", "00:00:00" },
        { SettingSections.Sync, "DirectorySync:MaxDropFraction", "1.5" },
        { SettingSections.Sync, "DirectorySync:MinimumExpectedCount", "-1" },
        { SettingSections.Mail, "Mail:Port", "0" },
        { SettingSections.Mail, "Mail:Port", "65536" },
        { SettingSections.Mail, "Mail:Port", "twenty-five" },
        { SettingSections.Mail, "Mail:SecureSocket", "Tls13" },
        { SettingSections.Mail, "Mail:FromAddress", "not-an-email" },
        { SettingSections.Mail, "Mail:FromAddress", "Desk <desk@example.test>" },
        { SettingSections.Mail, "Mail:Host", "relay host" },
        { SettingSections.Notifications, "Notifications:MaxAttempts", "0" },
        { SettingSections.Notifications, "Notifications:MaxAttempts", "51" },
        { SettingSections.Notifications, "Notifications:ReminderTimeOfDay", "25:00" },
        { SettingSections.Notifications, "Notifications:IncludePhoneNumberInMail", "maybe" },
        { SettingSections.Clock, "Clock:TimeZoneId", "Mars/Olympus_Mons" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public async Task Invalid_values_are_rejected_and_nothing_is_saved(string section, string key, string value)
    {
        var ex = await Assert.ThrowsAsync<SettingsValidationException>(() => f.SaveAsync(section, (key, value)));
        Assert.True(ex.Errors.ContainsKey(key), string.Join("; ", ex.Errors));
        Assert.False(await f.WithDbAsync(db => db.Settings.AnyAsync(s => s.Key == key)));
    }

    [Fact]
    public async Task Valid_values_are_normalized()
    {
        await f.SaveAsync(SettingSections.Directory,
            ("ActiveDirectory:Filter", " (&(objectCategory=person)(objectClass=user)(!(userAccountControl:1.2.840.113556.1.4.803:=2))) "),
            ("ActiveDirectory:SearchRoot", "GC://example.test/DC=example,DC=test"));
        await f.SaveAsync(SettingSections.Sync, ("DirectorySync:Interval", "1:00:00"), ("DirectorySync:MaxDropFraction", "0.25"));
        await f.SaveAsync(SettingSections.Mail, ("Mail:SecureSocket", "sslonconnect"));
        await f.SaveAsync(SettingSections.Notifications, ("Notifications:IncludePhoneNumberInMail", "False"));
        foreach (var tz in new[] { "W. Europe Standard Time", "Europe/Amsterdam" })
            await f.SaveAsync(SettingSections.Clock, ("Clock:TimeZoneId", tz));

        var rows = await f.WithDbAsync(db => db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value));
        Assert.Equal("(&(objectCategory=person)(objectClass=user)(!(userAccountControl:1.2.840.113556.1.4.803:=2)))", rows["ActiveDirectory:Filter"]);
        Assert.Equal("01:00:00", rows["DirectorySync:Interval"]);
        Assert.Equal("SslOnConnect", rows["Mail:SecureSocket"]);
        Assert.Equal("false", rows["Notifications:IncludePhoneNumberInMail"]);
        Assert.Equal("Europe/Amsterdam", rows["Clock:TimeZoneId"]);
        Assert.Equal(MailSecureSocket.SslOnConnect,
            f.Services.GetRequiredService<IOptionsMonitor<MailOptions>>().CurrentValue.SecureSocket);
    }

    [Fact]
    public async Task Required_mail_settings_must_be_set_somewhere()
    {
        // A host whose configuration file has no mail settings (the default after WP4.1).
        using var plain = new AppFactory();
        Task Save(params (string, string?)[] values) => plain.RunAsAsync(TestUser.Admin, sp => sp.GetRequiredService<SettingsService>()
            .SaveSectionAsync(SettingSections.Mail, values.ToDictionary(v => v.Item1, v => v.Item2)));

        var ex = await Assert.ThrowsAsync<SettingsValidationException>(() => Save(("Mail:Host", "relay.example.test"), ("Mail:FromAddress", "")));
        Assert.Equal(["Mail:FromAddress"], ex.Errors.Keys);
        await Save(("Mail:Host", "relay.example.test"), ("Mail:FromAddress", "asset-tracker@example.test"));
        ex = await Assert.ThrowsAsync<SettingsValidationException>(() => Save(("Mail:Host", "")));
        Assert.Equal(["Mail:Host"], ex.Errors.Keys);
        Assert.True(await plain.WithDbAsync(db => db.Settings.AnyAsync(s => s.Key == "Mail:Host" && s.Value == "relay.example.test")));
    }

    [Fact]
    public async Task Each_changed_key_is_audited_with_before_and_after()
    {
        await f.SaveAsync(SettingSections.Sync, ("DirectorySync:MinimumExpectedCount", "100"), ("DirectorySync:StaleAfter", "2.00:00:00"));
        var start = await f.WithDbAsync(db => db.AuditEvents.MaxAsync(a => a.Id));

        var changed = await f.SaveAsync(SettingSections.Sync,
            ("DirectorySync:MinimumExpectedCount", "150"), ("DirectorySync:StaleAfter", "2.00:00:00"), ("DirectorySync:RunningTimeout", "02:00:00"));

        Assert.Equal(["DirectorySync:MinimumExpectedCount", "DirectorySync:RunningTimeout"], changed);
        var audits = await f.WithDbAsync(db => db.AuditEvents.AsNoTracking().Where(a => a.Id > start).OrderBy(a => a.Id).ToListAsync());
        Assert.Equal(["DirectorySync:MinimumExpectedCount", "DirectorySync:RunningTimeout"], audits.Select(a => a.EntityId));
        Assert.All(audits, a => Assert.Equal((SettingsService.SettingAuditAction, nameof(AppSetting), TestUser.Admin.Name), (a.Action, a.EntityType, a.Actor)));
        Assert.Contains("\"before\":\"100\"", audits[0].DetailsJson);
        Assert.Contains("\"after\":\"150\"", audits[0].DetailsJson);
        Assert.Contains("\"before\":null", audits[1].DetailsJson);

        // Nothing changed: nothing audited.
        Assert.Empty(await f.SaveAsync(SettingSections.Sync, ("DirectorySync:MinimumExpectedCount", "150")));
        Assert.Equal(audits[^1].Id, await f.WithDbAsync(db => db.AuditEvents.MaxAsync(a => a.Id)));
    }

    [Fact]
    public async Task Non_admins_are_denied_by_the_service()
    {
        foreach (var user in new[] { TestUser.It, TestUser.Manager, TestUser.Nobody })
        {
            await Assert.ThrowsAsync<AccessDeniedException>(() => f.RunAsAsync(user, sp => sp.GetRequiredService<SettingsService>().GetAsync()));
            await Assert.ThrowsAsync<AccessDeniedException>(() => f.RunAsAsync(user, sp => sp.GetRequiredService<SettingsService>()
                .SaveSectionAsync(SettingSections.Mail, new Dictionary<string, string?> { ["Mail:Host"] = "evil.example.test" })));
            await Assert.ThrowsAsync<AccessDeniedException>(() => f.RunAsAsync(user, sp => sp.GetRequiredService<SettingsService>().PreviewDirectoryAsync()));
            await Assert.ThrowsAsync<AccessDeniedException>(() => f.RunAsAsync(user, sp => sp.GetRequiredService<SettingsService>().SendTestMailAsync("x@example.test")));
        }
        Assert.False(await f.WithDbAsync(db => db.Settings.AnyAsync(s => s.Value == "evil.example.test")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("?handler=TestDirectory")]
    [InlineData("?handler=TestMail")]
    public async Task Non_admins_get_403_on_the_page_and_its_handlers(string handler)
    {
        foreach (var user in new[] { TestUser.It, TestUser.Manager })
        {
            var c = f.CreateClientAs(user);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/Admin/Settings")).StatusCode);
            var res = await c.PostAsync("/Admin/Settings" + handler, new FormUrlEncodedContent(
                [new("section", "mail"), new("Mail:Host", "evil.example.test"), new("TestMailTo", "x@example.test")]));
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }
        Assert.False(await f.WithDbAsync(db => db.Settings.AnyAsync(s => s.Value == "evil.example.test")));
    }

    [Fact]
    public async Task Smtp_password_is_encrypted_write_only_and_never_audited_or_rendered()
    {
        const string secret = "Pl41n-Text-S3cret!";
        var c = f.CreateClientAs(TestUser.Admin);
        var res = await FormHelper.PostFormAsync(c, "/Admin/Settings", "/Admin/Settings",
            ("section", "mail"), ("Mail:Username", "svc-asset-tracker"), ("Mail:Password", secret));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var row = await f.WithDbAsync(db => db.Settings.AsNoTracking().SingleAsync(s => s.Key == SettingCatalog.MailPasswordKey));
        Assert.DoesNotContain(secret, row.Value);
        Assert.True(row.Value.Length <= 2000);
        Assert.Equal(secret, f.Services.GetRequiredService<ISecretProtector>().Unprotect(row.Value));

        // Configuration carries only the ciphertext, under a different key; the relay decrypts at send time.
        var mail = f.Services.GetRequiredService<IOptionsMonitor<MailOptions>>().CurrentValue;
        Assert.Equal((row.Value, (string?)null), (mail.PasswordProtected, mail.Password));
        var relay = ActivatorUtilities.CreateInstance<SmtpMailRelay>(f.Services);
        Assert.Equal(secret, relay.ResolveSettings().Password);

        var html = await (await c.GetAsync("/Admin/Settings")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, html);
        Assert.DoesNotContain(row.Value, html);
        Assert.Contains("<strong>set</strong>", html);

        var audit = await f.WithDbAsync(db => db.AuditEvents.AsNoTracking()
            .Where(a => a.EntityId == SettingCatalog.MailPasswordKey).OrderBy(a => a.Id).ToListAsync());
        Assert.Single(audit);
        Assert.DoesNotContain(secret, audit[0].DetailsJson);
        Assert.DoesNotContain(row.Value, audit[0].DetailsJson);
        Assert.Contains("\"change\":\"set\"", audit[0].DetailsJson);

        // An empty password field keeps the stored password.
        res = await FormHelper.PostFormAsync(c, "/Admin/Settings", "/Admin/Settings",
            ("section", "mail"), ("Mail:Username", "svc-asset-tracker"), ("Mail:Password", ""));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal(row.Value, (await f.WithDbAsync(db => db.Settings.AsNoTracking().SingleAsync(s => s.Key == SettingCatalog.MailPasswordKey))).Value);

        // "Clear password" removes it.
        res = await FormHelper.PostFormAsync(c, "/Admin/Settings", "/Admin/Settings",
            ("section", "mail"), ("Mail:Password", ""), ("Mail:Password.Clear", "true"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.False(await f.WithDbAsync(db => db.Settings.AnyAsync(s => s.Key == SettingCatalog.MailPasswordKey)));
        Assert.Null(f.Services.GetRequiredService<IOptionsMonitor<MailOptions>>().CurrentValue.PasswordProtected);
        Assert.Contains("\"change\":\"cleared\"", (await f.WithDbAsync(db => db.AuditEvents.AsNoTracking()
            .Where(a => a.EntityId == SettingCatalog.MailPasswordKey).OrderBy(a => a.Id).LastAsync())).DetailsJson);
        Assert.Contains("<strong>not set</strong>", await (await c.GetAsync("/Admin/Settings")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Page_shows_sections_effective_values_and_sources_and_rejects_bad_input()
    {
        var c = f.CreateClientAs(TestUser.Admin);
        var html = await (await c.GetAsync("/Admin/Settings")).Content.ReadAsStringAsync();
        foreach (var heading in new[] { "Directory (Active Directory)", "Directory sync safety", "Mail relay", "Notifications", "Default time zone" })
            Assert.Contains(heading, html);
        Assert.Contains(SettingsFactory.FileHost, html);  // effective value from the config file
        Assert.Contains("config file", html);
        Assert.Contains("(&amp;(objectCategory=person)(objectClass=user))", html); // default filter
        Assert.Contains("<datalist id=\"tz-list\">", html);
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("style=\"", html);

        var res = await FormHelper.PostFormAsync(c, "/Admin/Settings", "/Admin/Settings",
            ("section", "directory"), ("ActiveDirectory:SearchRoot", "OU=NoScheme"), ("ActiveDirectory:PageSize", "123"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        html = await res.Content.ReadAsStringAsync();
        Assert.Contains("LDAP://", html);
        Assert.Contains("value=\"OU=NoScheme\"", html); // re-rendered for correction
        Assert.False(await f.WithDbAsync(db => db.Settings.AnyAsync(s => s.Key == "ActiveDirectory:PageSize")));
    }

    [Fact]
    public async Task Test_directory_query_previews_without_writing_people_or_sync_runs()
    {
        await f.WithDbAsync(async db => await Seed.SiteAsync(db, "PRV1", "Preview Office"));
        f.Directory.Entries =
        [
            Entries.Person("prv-1", "prv.one", site: "Preview Office"),
            Entries.Person("prv-2", "prv.two", site: "Unknown Office"),
            Entries.Person("prv-3", "prv.three", site: null),
            Entries.Person("prv-dup", "prv.dup.a"),
            Entries.Person("prv-dup", "prv.dup.b"),
            Entries.Person("", "prv.noid"),
        ];
        var (people, runs) = await CountsAsync();

        var outcome = await f.RunAsAsync(TestUser.Admin, sp => sp.GetRequiredService<SettingsService>().PreviewDirectoryAsync());

        var p = Assert.IsType<DirectoryPreviewOutcome>(outcome).Preview!;
        Assert.Null(outcome.Error);
        Assert.Equal((6, 3, 3, 1, 1, 1), (p.Fetched, p.Valid, p.Quarantined, p.MappedToSite, p.UnmappedSite, p.MissingSite));
        Assert.Equal(3, p.Sample.Count);
        Assert.Contains(p.Sample, s => s is { Username: "prv.one", SiteValue: "Preview Office", SiteMapped: true });
        Assert.Equal((people, runs), await CountsAsync());
        var audit = await f.WithDbAsync(db => db.AuditEvents.AsNoTracking().Where(a => a.Action == "directory.preview").OrderBy(a => a.Id).LastAsync());
        Assert.Equal(TestUser.Admin.Name, audit.Actor);

        // Through the page; and a failing directory is reported, not thrown.
        var c = f.CreateClientAs(TestUser.Admin);
        var res = await FormHelper.PostFormAsync(c, "/Admin/Settings", "/Admin/Settings?handler=TestDirectory");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("Fetched <strong>6</strong>", html);
        Assert.Contains("prv.one", html);

        f.Directory.FailWith = new InvalidOperationException("LDAP server unavailable\nat line 2");
        try
        {
            res = await FormHelper.PostFormAsync(c, "/Admin/Settings", "/Admin/Settings?handler=TestDirectory");
            html = await res.Content.ReadAsStringAsync();
            Assert.Contains("Directory query failed: InvalidOperationException: LDAP server unavailable at line 2", html);
        }
        finally { f.Directory.FailWith = null; }
        Assert.Equal((people, runs), await CountsAsync());

        Task<(int, int)> CountsAsync() => f.WithDbAsync(async db => (await db.People.CountAsync(), await db.SyncRuns.CountAsync()));
    }

    [Fact]
    public async Task Test_email_goes_straight_through_the_relay_and_is_audited()
    {
        var outboxBefore = await f.WithDbAsync(db => db.Outbox.CountAsync());
        var result = await f.RunAsAsync(TestUser.Admin, sp => sp.GetRequiredService<SettingsService>().SendTestMailAsync(" ops@example.test "));
        Assert.True(result.Success);
        Assert.Contains(f.Mail.Sent, m => m.To == "ops@example.test" && m.Subject.Contains("test", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(outboxBefore, await f.WithDbAsync(db => db.Outbox.CountAsync()));

        f.Mail.FailWith = new InvalidOperationException("5.7.1 relay denied");
        try
        {
            var c = f.CreateClientAs(TestUser.Admin);
            var res = await FormHelper.PostFormAsync(c, "/Admin/Settings", "/Admin/Settings?handler=TestMail", ("TestMailTo", "ops@example.test"));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Contains("failed: InvalidOperationException: 5.7.1 relay denied", await res.Content.ReadAsStringAsync());
        }
        finally { f.Mail.FailWith = null; }

        var audits = await f.WithDbAsync(db => db.AuditEvents.AsNoTracking().Where(a => a.Action == "mail.test").OrderBy(a => a.Id).ToListAsync());
        Assert.Equal(2, audits.Count);
        Assert.Contains("\"Success\":true", audits[0].DetailsJson);
        Assert.Contains("\"Success\":false", audits[1].DetailsJson);
        Assert.Contains("relay denied", audits[1].Reason);

        await Assert.ThrowsAsync<DomainException>(() =>
            f.RunAsAsync(TestUser.Admin, sp => sp.GetRequiredService<SettingsService>().SendTestMailAsync("not an address")));
    }
}
