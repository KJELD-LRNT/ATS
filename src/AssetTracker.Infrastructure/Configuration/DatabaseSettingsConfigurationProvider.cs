using AssetTracker.Core.Settings;
using Microsoft.Extensions.Configuration;

namespace AssetTracker.Infrastructure.Configuration;

/// <summary>
/// Adds the database settings overlay. Call it <b>after</b> all other configuration sources (appsettings files,
/// environment, command line) so database values win; <see cref="DatabaseSettingsStartup.LoadDatabaseSettingsAsync"/>
/// also moves it to the end if a later source was added.
/// </summary>
public sealed class DatabaseSettingsConfigurationSource : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new DatabaseSettingsConfigurationProvider();
}

/// <summary>
/// Configuration overlay of database-backed settings (<c>AppSettings</c> rows whose key is a configuration path in
/// one of <see cref="SettingCatalog.ConfigSections"/>, e.g. <c>Mail:Host</c>).
/// <para>
/// The provider does not open the database itself: it is built with the configuration, before dependency injection
/// exists, and must read through whatever <c>AppDbContext</c> the host finally registers (SQL Server in production,
/// the test database in tests). <see cref="DatabaseSettingsReloader"/> (a DI singleton) reads the rows through a
/// scoped <c>AppDbContext</c> and calls <see cref="Apply"/>, which swaps the data and fires the reload token so
/// <c>IOptionsMonitor&lt;T&gt;</c> rebinds. Until the first successful load it serves nothing (file values apply).
/// </para>
/// The encrypted SMTP password row <c>Mail:Password</c> is exposed as <c>Mail:PasswordProtected</c>, never as
/// <c>Mail:Password</c>, so ciphertext can never be mistaken for a plaintext password.
/// </summary>
public sealed class DatabaseSettingsConfigurationProvider : ConfigurationProvider
{
    /// <summary>Loading happens via <see cref="Apply"/>; the initial state is empty.</summary>
    public override void Load() { }

    /// <summary>Replaces the overlay with the given rows (key → value). Returns true and fires the reload token when anything changed.</summary>
    public bool Apply(IEnumerable<KeyValuePair<string, string>> rows)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in rows)
        {
            if (!SettingCatalog.IsConfigKey(key)) continue;
            if (string.Equals(key, SettingCatalog.MailPasswordProtectedKey, StringComparison.OrdinalIgnoreCase)) continue;
            var exposed = string.Equals(key, SettingCatalog.MailPasswordKey, StringComparison.OrdinalIgnoreCase)
                ? SettingCatalog.MailPasswordProtectedKey
                : key;
            data[exposed] = value;
        }

        var current = Data;
        if (current.Count == data.Count && data.All(kv => current.TryGetValue(kv.Key, out var v) && v == kv.Value))
            return false;
        Data = data;
        OnReload();
        return true;
    }

    /// <summary>Keys currently served (for diagnostics and tests).</summary>
    public IReadOnlyCollection<string> Keys => Data.Keys.ToList();
}

public static class DatabaseSettingsConfigurationExtensions
{
    /// <summary>Adds the database settings overlay (see <see cref="DatabaseSettingsConfigurationProvider"/>). Add it last.</summary>
    public static IConfigurationBuilder AddDatabaseSettings(this IConfigurationBuilder builder)
    {
        if (!builder.Sources.OfType<DatabaseSettingsConfigurationSource>().Any())
            builder.Add(new DatabaseSettingsConfigurationSource());
        return builder;
    }
}
