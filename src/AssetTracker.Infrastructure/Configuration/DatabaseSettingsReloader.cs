using AssetTracker.Core.Data;
using AssetTracker.Core.Settings;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AssetTracker.Infrastructure.Configuration;

/// <summary>
/// Loads database-backed settings into <see cref="DatabaseSettingsConfigurationProvider"/>. Reads through a scoped
/// <see cref="AppDbContext"/>, i.e. whatever database the host registered (SQL Server in production, the test
/// database in tests). A database error (unreachable, or the table does not exist before the first migration) is
/// logged and the previous values stay in place; it never throws.
/// </summary>
public sealed class DatabaseSettingsReloader(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<DatabaseSettingsReloader> logger) : ISettingsReloader
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _failing;
    private bool _warnedMissingProvider;

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var provider = FindProvider();
            if (provider is null)
            {
                if (!_warnedMissingProvider)
                    logger.LogWarning("The database settings configuration source is not registered; settings saved in the app are ignored.");
                _warnedMissingProvider = true;
                return;
            }

            List<KeyValuePair<string, string>> rows;
            using (var scope = scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                try
                {
                    rows = (await db.Settings.AsNoTracking().Where(s => s.Key.Contains(":"))
                            .Select(s => new { s.Key, s.Value }).ToListAsync(ct))
                        .Select(s => new KeyValuePair<string, string>(s.Key, s.Value)).ToList();
                }
                catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
                {
                    if (!_failing)
                        logger.LogWarning(ex, "Database settings could not be loaded (database unreachable or not migrated yet); " +
                                              "keeping the current values and retrying later.");
                    else
                        logger.LogDebug(ex, "Database settings still cannot be loaded.");
                    _failing = true;
                    // A failed login (e.g. the database does not exist yet) can put the SqlClient pool into its
                    // blocking period; clear it so the next attempt (or the migration) is not refused from the cache.
                    if (db.Database.GetDbConnection() is SqlConnection sql) SqlConnection.ClearPool(sql);
                    return;
                }
            }

            if (_failing) logger.LogInformation("Database settings can be loaded again.");
            _failing = false;
            if (provider.Apply(rows))
                logger.LogInformation("Database settings applied ({Count} keys).", provider.Keys.Count);
        }
        finally
        {
            _gate.Release();
        }
    }

    public string? GetBaseValue(string key)
    {
        if (configuration is not IConfigurationRoot root) return configuration[key];
        foreach (var p in root.Providers.Reverse())
        {
            if (p is DatabaseSettingsConfigurationProvider) continue;
            if (p.TryGet(key, out var value)) return value;
        }
        return null;
    }

    private DatabaseSettingsConfigurationProvider? FindProvider() =>
        (configuration as IConfigurationRoot)?.Providers.OfType<DatabaseSettingsConfigurationProvider>().LastOrDefault();
}

/// <summary>Bootstrap settings for the overlay (config section <c>Settings</c>; file only).</summary>
public class DatabaseSettingsOptions
{
    public const string SectionName = "Settings";

    /// <summary>How often each process re-reads database settings. Zero or negative disables periodic reloads.</summary>
    public TimeSpan ReloadInterval { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Re-reads database settings every <see cref="DatabaseSettingsOptions.ReloadInterval"/>, so a change saved in
/// the web app reaches the worker (and other web processes) without a restart.</summary>
public sealed class DatabaseSettingsRefreshService(ISettingsReloader reloader, IConfiguration configuration)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = configuration.GetSection(DatabaseSettingsOptions.SectionName).Get<DatabaseSettingsOptions>()
                      ?? new DatabaseSettingsOptions();
        if (options.ReloadInterval <= TimeSpan.Zero) return;
        using var timer = new PeriodicTimer(options.ReloadInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await reloader.ReloadAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}

public static class DatabaseSettingsStartup
{
    /// <summary>
    /// Call once after the host is built and before it runs: makes sure the database overlay is the last
    /// configuration source (so it overrides every file/environment value, including sources added after
    /// <see cref="DatabaseSettingsConfigurationExtensions.AddDatabaseSettings"/>) and performs the first load,
    /// so singletons created at start-up already see database values.
    /// </summary>
    public static async Task LoadDatabaseSettingsAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        if (services.GetService<IConfiguration>() is IConfigurationBuilder builder)
        {
            var source = builder.Sources.OfType<DatabaseSettingsConfigurationSource>().LastOrDefault();
            if (source is not null && !ReferenceEquals(builder.Sources[^1], source))
            {
                builder.Sources.Remove(source);
                builder.Sources.Add(source);
            }
        }
        await services.GetRequiredService<ISettingsReloader>().ReloadAsync(ct);
    }
}
