using AssetTracker.Core.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AssetTracker.Infrastructure.Demo;

/// <summary>
/// Local demo mode (WP5.1, config section <c>Demo</c>): runs the whole app on one machine with a SQLite file,
/// a fictional directory, a pickup-folder mail relay, a time-travel clock and a role switcher instead of Windows
/// Authentication. Honoured only in the <c>Development</c> environment; enabling it anywhere else fails start-up.
/// </summary>
public class DemoOptions
{
    public const string SectionName = "Demo";

    public bool Enabled { get; set; }

    /// <summary>Folder for the demo database, Data Protection keys and the mail pickup folder.</summary>
    public string DataDirectory { get; set; } = DefaultDataDirectory();

    /// <summary>Windows-style account name of the default demo user.</summary>
    public string UserName { get; set; } = DemoDefaults.YouUserName;

    /// <summary>Run the background jobs (outbox sender, reminders, sync, heartbeat) inside the web process.</summary>
    public bool RunBackgroundJobs { get; set; } = true;

    public string DatabasePath => Path.Combine(DataDirectory, "demo.db");
    public string KeysDirectory => Path.Combine(DataDirectory, "keys");
    public string MailDirectory => Path.Combine(DataDirectory, "mail");

    /// <summary><c>%LOCALAPPDATA%\AssetTrackerDemo</c> on Windows, <c>~/.local/share/AssetTrackerDemo</c> on
    /// Linux/macOS; <c>~/.assettracker-demo</c> when no local application data folder exists.</summary>
    public static string DefaultDataDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local)) return Path.Combine(local, "AssetTrackerDemo");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(string.IsNullOrWhiteSpace(home) ? Path.GetTempPath() : home, ".assettracker-demo");
    }

    /// <summary>
    /// Reads the <c>Demo</c> section. Returns null when demo mode is off. Throws when it is switched on outside the
    /// Development environment, so a demo setting can never take effect on a server by accident.
    /// </summary>
    public static DemoOptions? Resolve(IConfiguration config, IHostEnvironment environment)
    {
        var options = new DemoOptions();
        config.GetSection(SectionName).Bind(options);
        if (!options.Enabled) return null;
        if (!environment.IsDevelopment())
            throw new InvalidOperationException(
                "Demo mode (Demo:Enabled=true) is only allowed in the Development environment, but the environment is " +
                $"'{environment.EnvironmentName}'. Remove Demo:Enabled (or the Demo__Enabled environment variable).");
        if (string.IsNullOrWhiteSpace(options.DataDirectory)) options.DataDirectory = DefaultDataDirectory();
        options.DataDirectory = Path.GetFullPath(options.DataDirectory);
        if (string.IsNullOrWhiteSpace(options.UserName)) options.UserName = DemoDefaults.YouUserName;
        return options;
    }
}

/// <summary>Fixed fictional identities and values used by demo mode. Nothing here is a real account or host.</summary>
public static class DemoDefaults
{
    /// <summary>Fictional domain SID prefix (not a real domain).</summary>
    public const string SidPrefix = "S-1-5-21-1000000001-1000000002-1000000003-";

    public const string ItGroupSid = SidPrefix + "9101";
    public const string ManagerGroupSid = SidPrefix + "9102";
    public const string AdminGroupSid = SidPrefix + "9103";

    public const string Domain = "DEMO";
    public const string YouUserName = Domain + @"\you";

    /// <summary>The demo "you" person (no manager grants).</summary>
    public const string YouUsername = "you";
    public const string YouSid = SidPrefix + "5001";

    /// <summary>The demo manager person, granted site AMS only.</summary>
    public const string ManagerUsername = "manager.ams";
    public const string ManagerSid = SidPrefix + "5002";

    public const string ItMailbox = "it-helpdesk@demo.example";
    public const string FromAddress = "asset-tracker@demo.example";
    public const string EmailDomain = "demo.example";

    public const string TimeZoneId = "Europe/Amsterdam";

    /// <summary>Roles of a fresh demo session (before anything is chosen on /Demo).</summary>
    public const AppRoles DefaultRoles = AppRoles.It | AppRoles.Admin;

    /// <summary>Actor recorded for demo seeding.</summary>
    public const string SeedActor = "system:demo-seed";

    /// <summary>
    /// Configuration applied in demo mode (in memory, not in the database): fictional role group SIDs, short job
    /// intervals and the default timezone. Settings saved on Admin → Settings still override these.
    /// </summary>
    public static Dictionary<string, string?> Configuration() => new()
    {
        ["Roles:ItGroupSids:0"] = ItGroupSid,
        ["Roles:ManagerGroupSids:0"] = ManagerGroupSid,
        ["Roles:AdminGroupSids:0"] = AdminGroupSid,
        ["Clock:TimeZoneId"] = TimeZoneId,
        ["Notifications:PollInterval"] = "00:00:10",
        ["Notifications:ReminderPollInterval"] = "00:01:00",
        ["Notifications:ReminderTimeOfDay"] = "08:00",
        ["DirectorySync:Interval"] = "01:00:00",
        ["Worker:HeartbeatInterval"] = "00:00:30",
        ["Ops:WorkerHeartbeatInterval"] = "00:00:30",
        ["Mail:FromAddress"] = FromAddress,
        ["Mail:FromName"] = "Asset Tracker (demo)",
        // Keep the console readable: no per-query EF logging.
        ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Warning",
        // The demo key ring is a plain folder on purpose (no DPAPI); don't warn about it on every start.
        ["Logging:LogLevel:Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager"] = "Error",
    };
}
