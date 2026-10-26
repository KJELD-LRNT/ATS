namespace AssetTracker.Core.Settings;

/// <summary>
/// Reloads database-backed settings (<see cref="Domain.AppSetting"/> rows with a configuration-path key) into the
/// application configuration, so <c>IOptionsMonitor&lt;T&gt;</c> consumers see new values without a restart.
/// Implemented in the infrastructure layer; also runs periodically (<c>Settings:ReloadInterval</c>).
/// </summary>
public interface ISettingsReloader
{
    /// <summary>Reads the settings rows now and applies them. Never throws for a database error (logged instead).</summary>
    Task ReloadAsync(CancellationToken ct = default);

    /// <summary>
    /// The value of a configuration key from every source except the database (appsettings files, environment,
    /// command line), or null when none sets it. Used to show where an effective value comes from.
    /// </summary>
    string? GetBaseValue(string key);
}
