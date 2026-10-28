using System.Globalization;
using AssetTracker.Core.Data;
using AssetTracker.Core.Directory;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Notifications;
using AssetTracker.Core.Ops;
using AssetTracker.Core.Settings;
using AssetTracker.Infrastructure.Configuration;
using AssetTracker.Infrastructure.Directory;
using AssetTracker.Infrastructure.Mail;
using AssetTracker.Core.Time;
using AssetTracker.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AssetTracker.Tests.Settings;

/// <summary>The database configuration overlay: precedence, live reload, fallback and which keys it exposes.</summary>
public class SettingsConfigurationTests(SettingsFactory f) : IClassFixture<SettingsFactory>
{
    private T Current<T>() where T : class => f.Services.GetRequiredService<IOptionsMonitor<T>>().CurrentValue;

    [Fact]
    public async Task Database_value_overrides_the_config_file_live_and_removing_it_falls_back()
    {
        Assert.Equal(SettingsFactory.FileFromName, Current<MailOptions>().FromName);
        Assert.Equal(SettingSource.ConfigFile, (await f.ViewAsync("Mail:FromName")).Source);

        await f.SaveAsync(SettingSections.Mail, ("Mail:FromName", "Database Sender"));

        // No restart: the save reloads configuration and IOptionsMonitor rebinds.
        Assert.Equal("Database Sender", Current<MailOptions>().FromName);
        var view = await f.ViewAsync("Mail:FromName");
        Assert.Equal((SettingSource.Database, "Database Sender", SettingsFactory.FileFromName),
            (view.Source, view.EffectiveValue, view.FileValue));
        Assert.Equal(TestUser.Admin.Name, view.UpdatedBy);

        // Empty value removes the override: the config file value applies again.
        await f.SaveAsync(SettingSections.Mail, ("Mail:FromName", ""));
        Assert.Equal(SettingsFactory.FileFromName, Current<MailOptions>().FromName);
        Assert.False(await f.WithDbAsync(db => db.Settings.AnyAsync(s => s.Key == "Mail:FromName")));
        Assert.Equal(SettingSource.ConfigFile, (await f.ViewAsync("Mail:FromName")).Source);
    }

    [Fact]
    public async Task Rows_written_elsewhere_apply_on_reload_and_scoped_IOptions_see_them()
    {
        // As if saved by another process (the worker picks changes up on its periodic reload).
        await f.WithDbAsync(async db =>
        {
            db.Settings.Add(new AppSetting { Key = "DirectorySync:MaxDropFraction", Value = "0.5", UpdatedBy = "test" });
            await db.SaveChangesAsync();
        });
        Assert.Equal(0.2, Current<DirectorySyncOptions>().MaxDropFraction);

        await f.ReloadSettingsAsync();
        Assert.Equal(0.5, Current<DirectorySyncOptions>().MaxDropFraction);
        using (var scope = f.Services.CreateScope())
            Assert.Equal(0.5, scope.ServiceProvider.GetRequiredService<IOptions<DirectorySyncOptions>>().Value.MaxDropFraction);

        await f.WithDbAsync(db => db.Settings.Where(s => s.Key == "DirectorySync:MaxDropFraction").ExecuteDeleteAsync());
        await f.ReloadSettingsAsync();
        Assert.Equal(0.2, Current<DirectorySyncOptions>().MaxDropFraction);
    }

    [Fact]
    public async Task Reminder_time_and_default_time_zone_keys_flow_through_configuration()
    {
        await f.SaveAsync(SettingSections.Notifications, ("Notifications:ReminderTimeOfDay", "7:15"));
        Assert.Equal(new TimeOnly(7, 15), Current<NotificationOptions>().ReminderTimeOfDay);

        await f.SaveAsync(SettingSections.Clock, ("Clock:TimeZoneId", "Europe/Amsterdam"));
        Assert.Equal("Europe/Amsterdam", Current<ClockOptions>().TimeZoneId);
        Assert.Equal("Europe/Amsterdam", f.Services.GetRequiredService<IConfiguration>()["Clock:TimeZoneId"]);

        await f.SaveAsync(SettingSections.Clock, ("Clock:TimeZoneId", ""));
        Assert.Equal("UTC", Current<ClockOptions>().TimeZoneId);
        await f.SaveAsync(SettingSections.Notifications, ("Notifications:ReminderTimeOfDay", ""));
        Assert.Equal(new TimeOnly(8, 0), Current<NotificationOptions>().ReminderTimeOfDay);
    }

    [Fact]
    public async Task Non_configuration_and_bootstrap_keys_are_never_exposed()
    {
        await f.RunAsAsync(TestUser.Nobody, sp => sp.GetRequiredService<OpsStatusService>().RecordHeartbeatAsync("1.0"));
        await f.WithDbAsync(async db =>
        {
            db.Settings.AddRange(
                new AppSetting { Key = "Ops:WorkerHeartbeatInterval", Value = "00:00:01", UpdatedBy = "attacker" },
                new AppSetting { Key = "Roles:AdminGroupSids:0", Value = "S-1-5-21-9-9-9-666", UpdatedBy = "attacker" },
                new AppSetting { Key = "ConnectionStrings:AssetTracker", Value = "Server=evil", UpdatedBy = "attacker" },
                new AppSetting { Key = "Mail:PasswordProtected", Value = "not-from-the-settings-page", UpdatedBy = "attacker" });
            await db.SaveChangesAsync();
        });
        await f.ReloadSettingsAsync();

        var config = f.Services.GetRequiredService<IConfiguration>();
        Assert.Null(config[OpsStatusService.HeartbeatKey]);
        Assert.Null(config[AppSetting.ItMailbox]);
        // A file-only key keeps its config file value (appsettings.json); an unknown section is not exposed at all.
        Assert.Equal("00:01:00", config["Ops:WorkerHeartbeatInterval"]);
        Assert.Null(config["Roles:AdminGroupSids:0"]);
        Assert.NotEqual("Server=evil", config["ConnectionStrings:AssetTracker"]);
        Assert.Null(config["Mail:PasswordProtected"]);

        var provider = ((IConfigurationRoot)config).Providers.OfType<DatabaseSettingsConfigurationProvider>().Single();
        Assert.All(provider.Keys, k => Assert.True(SettingCatalog.IsConfigKey(k), k));
    }

    [Fact]
    public async Task Provider_survives_a_missing_table_and_loads_once_it_exists()
    {
        var path = Path.Combine(Path.GetTempPath(), "ats_settings_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Mail:Host"] = "file-host.example.test" })
                .AddDatabaseSettings()
                .Build();
            var services = new ServiceCollection()
                .AddLogging()
                .AddSingleton<IConfiguration>(config)
                .AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={path}"))
                .AddSingleton<ISettingsReloader, DatabaseSettingsReloader>()
                .BuildServiceProvider();
            var reloader = services.GetRequiredService<ISettingsReloader>();

            // No schema yet (first start before migrations): no exception, file values still served.
            await reloader.ReloadAsync();
            Assert.Equal("file-host.example.test", config["Mail:Host"]);

            using (var scope = services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.EnsureCreatedAsync();
                db.Settings.Add(new AppSetting { Key = "Mail:Host", Value = "db-host.example.test", UpdatedBy = "test" });
                await db.SaveChangesAsync();
            }
            await reloader.ReloadAsync();
            Assert.Equal("db-host.example.test", config["Mail:Host"]);
            Assert.Equal("file-host.example.test", reloader.GetBaseValue("Mail:Host"));
            await services.DisposeAsync();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Catalog_defaults_match_the_option_class_defaults()
    {
        var types = new Dictionary<string, Type>
        {
            ["ActiveDirectory"] = typeof(ActiveDirectoryOptions),
            ["DirectorySync"] = typeof(DirectorySyncOptions),
            ["Mail"] = typeof(MailOptions),
            ["Notifications"] = typeof(NotificationOptions),
            ["Clock"] = typeof(ClockOptions),
        };
        foreach (var d in SettingCatalog.All)
        {
            var section = d.Key[..d.Key.IndexOf(':')];
            var property = d.Key[(d.Key.IndexOf(':') + 1)..];
            var instance = Activator.CreateInstance(types[section])!;
            var value = types[section].GetProperty(property)?.GetValue(instance);
            Assert.True(types[section].GetProperty(property) is not null, $"{d.Key} has no option property");
            var text = value switch
            {
                null => null,
                "" => null,
                TimeSpan t => t.ToString("c", CultureInfo.InvariantCulture),
                TimeOnly t => t.ToString("HH:mm", CultureInfo.InvariantCulture),
                bool b => b ? "true" : "false",
                double x => x.ToString(CultureInfo.InvariantCulture),
                IFormattable x => x.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString(),
            };
            Assert.True(d.Default == text, $"{d.Key}: catalog default '{d.Default}' but options default '{text}'");
        }
    }

    [Fact]
    public async Task Smtp_relay_uses_the_new_host_without_a_restart()
    {
        // The test host replaces IMailRelay with a fake; build the real relay to inspect what it would connect to.
        var relay = ActivatorUtilities.CreateInstance<SmtpMailRelay>(f.Services);
        Assert.Equal(SettingsFactory.FileHost, relay.ResolveSettings().Options.Host);

        await f.SaveAsync(SettingSections.Mail, ("Mail:Host", "relay-from-db.example.test"), ("Mail:Port", "587"));

        var (o, _) = relay.ResolveSettings();
        Assert.Equal(("relay-from-db.example.test", 587), (o.Host, o.Port));
        await f.SaveAsync(SettingSections.Mail, ("Mail:Host", ""), ("Mail:Port", ""));
        Assert.Equal(SettingsFactory.FileHost, relay.ResolveSettings().Options.Host);
    }

    [Fact]
    public async Task Directory_source_options_are_live()
    {
        Assert.Equal(300, Current<ActiveDirectoryOptions>().PageSize);
        await f.SaveAsync(SettingSections.Directory,
            ("ActiveDirectory:SearchRoot", "LDAP://dc.example.test/OU=Staff,DC=example,DC=test"),
            ("ActiveDirectory:SiteAttribute", "physicalDeliveryOfficeName"),
            ("ActiveDirectory:PageSize", "250"));
        var o = Current<ActiveDirectoryOptions>();
        Assert.Equal(("LDAP://dc.example.test/OU=Staff,DC=example,DC=test", "physicalDeliveryOfficeName", 250),
            (o.SearchRoot, o.SiteAttribute, o.PageSize));
    }
}
