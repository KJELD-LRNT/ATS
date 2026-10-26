using System.Globalization;
using System.Text.RegularExpressions;
using AssetTracker.Core.Notifications;

namespace AssetTracker.Core.Settings;

/// <summary>How a setting is edited and validated.</summary>
public enum SettingKind
{
    Text,
    Host,
    LdapPath,
    LdapFilter,
    AttributeName,
    Integer,
    Fraction,
    Duration,
    Boolean,
    Choice,
    TimeOfDay,
    TimeZone,
    Email,

    /// <summary>Write-only; stored encrypted, never shown or audited by value.</summary>
    Secret,
}

/// <summary>The page sections; also the unit of one save.</summary>
public static class SettingSections
{
    public const string Directory = "directory";
    public const string Sync = "sync";
    public const string Mail = "mail";
    public const string Notifications = "notifications";
    public const string Clock = "clock";

    public static readonly IReadOnlyList<string> All = [Directory, Sync, Mail, Notifications, Clock];
}

/// <summary>One editable setting. <see cref="Key"/> is the configuration path the options classes bind.</summary>
/// <param name="Default">The value the options class uses when nothing configures it (null = no default).</param>
/// <param name="RequiredEffective">Saving the section fails when neither the database nor the config file sets it.</param>
public sealed record SettingDefinition(
    string Key,
    string Section,
    string Label,
    SettingKind Kind,
    string? Default,
    string Help,
    int Min = 0,
    int Max = int.MaxValue,
    IReadOnlyList<string>? Choices = null,
    bool RequiredEffective = false);

/// <summary>
/// The settings administrators edit on <c>/Admin/Settings</c>. Each is stored as an <see cref="Domain.AppSetting"/>
/// row whose key is the configuration path (e.g. <c>Mail:Host</c>); the infrastructure configuration provider
/// overlays those rows on the appsettings files, so the options classes bind them unchanged.
/// Defaults here must equal the option classes' defaults (pinned by a test).
/// </summary>
public static partial class SettingCatalog
{
    /// <summary>Configuration sections the database may override. Anything else (connection strings, roles) never.</summary>
    public static readonly IReadOnlyList<string> ConfigSections = ["ActiveDirectory", "DirectorySync", "Mail", "Notifications", "Clock"];

    /// <summary>The SMTP password row. Its value is ciphertext; configuration exposes it as <see cref="MailPasswordProtectedKey"/>.</summary>
    public const string MailPasswordKey = "Mail:Password";

    /// <summary>Configuration key under which the encrypted SMTP password reaches <c>MailOptions.PasswordProtected</c>.</summary>
    public const string MailPasswordProtectedKey = "Mail:PasswordProtected";

    /// <summary>Pseudo-key of the IT mailbox field (stored under the legacy, non-configuration key <see cref="Domain.AppSetting.ItMailbox"/>).</summary>
    public const string ItMailboxField = "ItMailbox";

    public const int MaxTextLength = 1000;
    public const int MaxSecretLength = 256;

    /// <summary>True when an <see cref="Domain.AppSetting"/> key is a configuration path the database may supply.</summary>
    public static bool IsConfigKey(string key) =>
        key.IndexOf(':') is > 0 and var i && i < key.Length - 1 &&
        ConfigSections.Contains(key[..i], StringComparer.OrdinalIgnoreCase);

    private const string D = SettingSections.Directory, S = SettingSections.Sync, M = SettingSections.Mail,
        N = SettingSections.Notifications, C = SettingSections.Clock;

    public static readonly IReadOnlyList<SettingDefinition> All =
    [
        new("ActiveDirectory:SearchRoot", D, "Search root (LDAP path)", SettingKind.LdapPath, null,
            "LDAP path of the in-scope OU, e.g. LDAP://dc.example.local/OU=Staff,DC=example,DC=local (or GC://...)."),
        new("ActiveDirectory:Filter", D, "LDAP filter", SettingKind.LdapFilter, "(&(objectCategory=person)(objectClass=user))",
            "Selects in-scope user objects."),
        new("ActiveDirectory:SiteAttribute", D, "Site attribute", SettingKind.AttributeName, null,
            "AD attribute holding the person's site; mapped to sites via Admin → Sites \"directory value\"."),
        new("ActiveDirectory:PageSize", D, "Page size", SettingKind.Integer, "500", "Paged search size (1–1000).", 1, 1000),
        new("ActiveDirectory:ClientTimeout", D, "Client timeout", SettingKind.Duration, null,
            "Client-side timeout for the whole search (hh:mm:ss). Empty = none."),
        new("ActiveDirectory:IdAttribute", D, "Id attribute", SettingKind.AttributeName, "objectGUID", "Stable identity attribute."),
        new("ActiveDirectory:UsernameAttribute", D, "Username attribute", SettingKind.AttributeName, "sAMAccountName", ""),
        new("ActiveDirectory:DisplayNameAttribute", D, "Display name attribute", SettingKind.AttributeName, "displayName", ""),
        new("ActiveDirectory:TitleAttribute", D, "Job title attribute", SettingKind.AttributeName, "title", ""),
        new("ActiveDirectory:MailAttribute", D, "Mail attribute", SettingKind.AttributeName, "mail", ""),
        new("ActiveDirectory:SidAttribute", D, "SID attribute", SettingKind.AttributeName, "objectSid", ""),
        new("ActiveDirectory:UserAccountControlAttribute", D, "Account control attribute", SettingKind.AttributeName,
            "userAccountControl", "Bit 0x2 = disabled; a missing value is treated as disabled."),

        new("DirectorySync:Interval", S, "Sync interval", SettingKind.Duration, null,
            "How often the worker runs a full sync (hh:mm:ss, e.g. 01:00:00). Empty = no scheduled sync."),
        new("DirectorySync:MinimumExpectedCount", S, "Minimum expected entries", SettingKind.Integer, "0",
            "Reject a run with fewer valid entries. 0 = off (a run with zero valid entries is always rejected).", 0, 10_000_000),
        new("DirectorySync:MaxDropFraction", S, "Maximum deactivation fraction", SettingKind.Fraction, "0.2",
            "Reject a run that would deactivate more than this fraction (0–1) of active people."),
        new("DirectorySync:StaleAfter", S, "Stale after", SettingKind.Duration, "1.00:00:00",
            "People data counts as stale after this (d.hh:mm:ss). Set to ~2–3× the interval."),
        new("DirectorySync:RunningTimeout", S, "Running timeout", SettingKind.Duration, "01:00:00",
            "A run stuck in Running longer than this no longer blocks a new run."),

        new("Mail:Host", M, "SMTP relay host", SettingKind.Host, null, "Approved internal relay.", RequiredEffective: true),
        new("Mail:Port", M, "Port", SettingKind.Integer, "25", "1–65535.", 1, 65535),
        new("Mail:SecureSocket", M, "TLS mode", SettingKind.Choice, "StartTls",
            "StartTls (required TLS), SslOnConnect (usually port 465) or None (only if TLS is unavailable).",
            Choices: ["StartTls", "SslOnConnect", "None"]),
        new("Mail:Username", M, "Username", SettingKind.Text, null, "Only for an authenticated relay; empty for an IP-restricted relay."),
        new(MailPasswordKey, M, "Password", SettingKind.Secret, null,
            "Write-only. Stored encrypted (Data Protection). Leave empty to keep the current password."),
        new("Mail:FromAddress", M, "From address", SettingKind.Email, null, "Approved sending identity.", RequiredEffective: true),
        new("Mail:FromName", M, "From name", SettingKind.Text, "Asset Tracker", ""),
        new("Mail:Timeout", M, "SMTP timeout", SettingKind.Duration, "00:00:30", "Must be shorter than the claim lease."),

        new("Notifications:IncludePhoneNumberInMail", N, "Include phone numbers in mail", SettingKind.Boolean, "true",
            "Whether mail about a phone / phone-number asset shows its number (sponsor decision D5: yes)."),
        new("Notifications:ReminderTimeOfDay", N, "Overdue reminder time", SettingKind.TimeOfDay, "08:00",
            "Time of day (HH:mm) at which overdue reminders are queued, in each site's local time."),
        new("Notifications:ReminderPollInterval", N, "Reminder check interval", SettingKind.Duration, "00:15:00",
            "How often the worker checks whether a site's reminder time has been reached (hh:mm:ss)."),
        new("Notifications:MaxAttempts", N, "Send attempts", SettingKind.Integer, "8",
            "Attempts before a mail becomes Failed (1–50).", 1, 50),
        new("Notifications:InitialRetryDelay", N, "First retry delay", SettingKind.Duration, "00:01:00", "Doubled per attempt."),
        new("Notifications:MaxRetryDelay", N, "Maximum retry delay", SettingKind.Duration, "01:00:00", ""),
        new("Notifications:ClaimLease", N, "Claim lease", SettingKind.Duration, "00:05:00", "Must exceed the SMTP timeout."),
        new("Notifications:PollInterval", N, "Outbox poll interval", SettingKind.Duration, "00:00:30", ""),
        new("Notifications:BatchSize", N, "Outbox batch size", SettingKind.Integer, "50", "1–1000.", 1, 1000),

        new("Clock:TimeZoneId", C, "Default time zone", SettingKind.TimeZone, null,
            "Windows or IANA id (e.g. W. Europe Standard Time or Europe/Amsterdam). Used for due dates and overdue " +
            "reminders of every site without its own time zone (Admin → Sites).",
            RequiredEffective: true),
    ];

    public static SettingDefinition? Find(string key) =>
        All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<SettingDefinition> InSection(string section) => All.Where(d => d.Section == section);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9-]{0,63}$")]
    private static partial Regex AttributeNamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9.\-:\[\]]{1,253}$")]
    private static partial Regex HostPattern();

    /// <summary>
    /// Validates a non-empty input and returns its stored (canonical) form, or an error message.
    /// Empty input is handled by the caller (it removes the database override).
    /// </summary>
    public static (string? Value, string? Error) Normalize(SettingDefinition d, string input)
    {
        var s = input.Trim();
        var inv = CultureInfo.InvariantCulture;
        switch (d.Kind)
        {
            case SettingKind.Secret:
                return input.Length > MaxSecretLength ? (null, $"{d.Label} is too long (max {MaxSecretLength}).") : (input, null);

            case SettingKind.Text:
                return s.Length > MaxTextLength ? (null, $"{d.Label} is too long (max {MaxTextLength}).") : (s, null);

            case SettingKind.Host:
                return HostPattern().IsMatch(s) ? (s, null) : (null, $"{d.Label} must be a host name or IP address.");

            case SettingKind.LdapPath:
            {
                var ok = s.Length <= MaxTextLength && !s.Any(char.IsControl) &&
                         ((s.StartsWith("LDAP://", StringComparison.OrdinalIgnoreCase) && s.Length > 7) ||
                          (s.StartsWith("GC://", StringComparison.OrdinalIgnoreCase) && s.Length > 5));
                return ok ? (s, null) : (null, $"{d.Label} must be an LDAP path starting with LDAP:// or GC://.");
            }

            case SettingKind.LdapFilter:
                return IsBalancedFilter(s) && s.Length <= MaxTextLength
                    ? (s, null)
                    : (null, $"{d.Label} must be a parenthesized LDAP filter with balanced parentheses, e.g. (&(objectClass=user)).");

            case SettingKind.AttributeName:
                return AttributeNamePattern().IsMatch(s)
                    ? (s, null)
                    : (null, $"{d.Label} must be an attribute name (letters, digits, '-'; starts with a letter).");

            case SettingKind.Integer:
                return int.TryParse(s, NumberStyles.Integer, inv, out var n) && n >= d.Min && n <= d.Max
                    ? (n.ToString(inv), null)
                    : (null, $"{d.Label} must be a whole number from {d.Min} to {d.Max}.");

            case SettingKind.Fraction:
                return double.TryParse(s, NumberStyles.Float, inv, out var f) && f is >= 0 and <= 1
                    ? (f.ToString(inv), null)
                    : (null, $"{d.Label} must be a number from 0 to 1 (e.g. 0.2).");

            case SettingKind.Duration:
                return TimeSpan.TryParse(s, inv, out var t) && t > TimeSpan.Zero
                    ? (t.ToString("c", inv), null)
                    : (null, $"{d.Label} must be a positive duration ([d.]hh:mm:ss, e.g. 01:00:00).");

            case SettingKind.Boolean:
                return bool.TryParse(s, out var b) ? (b ? "true" : "false", null) : (null, $"{d.Label} must be true or false.");

            case SettingKind.Choice:
            {
                var match = d.Choices?.FirstOrDefault(c => string.Equals(c, s, StringComparison.OrdinalIgnoreCase));
                return match is not null ? (match, null) : (null, $"{d.Label} must be one of: {string.Join(", ", d.Choices ?? [])}.");
            }

            case SettingKind.TimeOfDay:
                return TimeOnly.TryParse(s, inv, DateTimeStyles.None, out var tod)
                    ? (tod.ToString(tod.Second == 0 ? "HH:mm" : "HH:mm:ss", inv), null)
                    : (null, $"{d.Label} must be a time of day (HH:mm, e.g. 08:00).");

            case SettingKind.TimeZone:
                return TryFindTimeZone(s) ? (s, null) : (null, $"{d.Label} '{Truncate(s, 100)}' is not a known time zone id.");

            case SettingKind.Email:
                return EmailAddresses.Normalize(s) is { } address
                    ? (address, null)
                    : (null, $"{d.Label} must be a single valid email address (e.g. asset-tracker@example.com).");

            default:
                return (null, $"{d.Label}: unsupported setting kind.");
        }
    }

    /// <summary>A non-empty filter that starts with '(' and ends with ')' and whose parentheses balance.</summary>
    public static bool IsBalancedFilter(string filter)
    {
        if (filter.Length < 2 || filter[0] != '(' || filter[^1] != ')') return false;
        var depth = 0;
        for (var i = 0; i < filter.Length; i++)
        {
            if (filter[i] == '(') depth++;
            else if (filter[i] == ')' && --depth < 0) return false;
            // The filter is one expression: depth may only return to zero at the very end.
            if (depth == 0 && i < filter.Length - 1) return false;
        }
        return depth == 0 && filter != "()";
    }

    public static bool TryFindTimeZone(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return false;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
