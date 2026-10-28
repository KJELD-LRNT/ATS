using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AssetTracker.Core.Data;
using AssetTracker.Core.Directory;
using AssetTracker.Core.Notifications;
using AssetTracker.Core.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AssetTracker.Infrastructure.Demo;

/// <summary>
/// Demo database lifecycle: create and seed on start-up, "reset demo data", and time travel. Schema comes from
/// <c>EnsureCreated()</c> (the EF migrations are SQL Server specific). Resets run under a lock; background jobs that
/// hit the database during the few seconds of a reset fail once and retry on their next tick.
/// </summary>
public sealed class DemoDataManager(
    IServiceScopeFactory scopes,
    DemoOptions options,
    DemoClock clock,
    DemoSeeder seeder,
    DemoMailbox mailbox,
    ISettingsReloader settings,
    ILogger<DemoDataManager> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the folders and database when missing, seeds an empty database and loads the clock offset.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            System.IO.Directory.CreateDirectory(options.DataDirectory);
            System.IO.Directory.CreateDirectory(options.MailDirectory);
            using (var scope = scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                // EnsureCreated does nothing on an existing file, so a demo.db from an older version of the app would
                // miss new tables and columns: rebuild it when its schema version differs from this app's.
                var version = SchemaVersion(db);
                if (File.Exists(options.DatabasePath) && await ReadSchemaVersionAsync(ct) != version)
                {
                    logger.LogWarning("The demo database at {Path} was made by another version of the app; rebuilding it.",
                        options.DatabasePath);
                    DeleteDatabaseFiles();
                    mailbox.Clear();
                }
                if (await db.Database.EnsureCreatedAsync(ct))
                {
                    logger.LogInformation("Demo database created at {Path}.", options.DatabasePath);
                    await WriteSchemaVersionAsync(version, ct);
                }
                // Write-ahead logging: pages and background jobs read while another connection writes.
                await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
            }
            await clock.LoadAsync(ct);
            if (await seeder.SeedIfEmptyAsync(ct))
                logger.LogInformation("Demo data seeded (fictional people, sites, assets, pools and loans).");
            await seeder.EnsureSuperAdminAsync(ct);
            await settings.ReloadAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes all demo data and mail, resets the clock to real time and seeds again.</summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using (var scope = scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await WipeAsync(db, ct);
            }
            clock.ResetInMemory();
            mailbox.Clear();
            await seeder.SeedIfEmptyAsync(ct);
            await seeder.EnsureSuperAdminAsync(ct);
            await settings.ReloadAsync(ct);
            logger.LogInformation("Demo data reset.");
        }
        finally
        {
            _gate.Release();
        }
        // Today's reminders right away instead of on the reminder job's next tick (only when the jobs run here).
        if (options.RunBackgroundJobs) await QueueRemindersAsync("reset", ct);
    }

    /// <summary>
    /// Moves the demo clock forward by <paramref name="by"/> (or back to real time when null), then runs a directory
    /// sync (so the snapshot is not reported stale at the new date) and an overdue-reminder pass right away.
    /// </summary>
    public async Task TravelAsync(TimeSpan? by, CancellationToken ct = default)
    {
        await clock.SetOffsetAsync(by is { } d ? clock.Offset + d : TimeSpan.Zero, ct);
        using var scope = scopes.CreateScope();
        try
        {
            await scope.ServiceProvider.GetRequiredService<DirectorySyncService>().RunAsync("system:demo-time-travel", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Directory sync after time travel failed; the hourly job will retry.");
        }
        await QueueRemindersAsync("time travel", ct);
    }

    private async Task QueueRemindersAsync(string after, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<OverdueReminderService>().QueueRemindersAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Reminder pass after {After} failed; the reminder job will retry.", after);
        }
    }

    /// <summary>Version of the demo seed data; part of <see cref="SchemaVersion"/>.</summary>
    public const int SeedVersion = 2;

    /// <summary>
    /// The app's demo schema version: derived from the SQLite create script of the current EF model, so any model change
    /// (a new table, column or index) gives a new version without anyone having to remember to bump a number. Stored in
    /// the database file's <c>PRAGMA user_version</c> (0 for files made before this existed).
    /// </summary>
    public static int SchemaVersion(AppDbContext db)
    {
        // SeedVersion: bump when the demo seed changes in a way an existing demo.db must pick up (for example the
        // personas' roles moving into RoleAssignments), so the demo rebuilds even though the schema stayed the same.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(db.Database.GenerateCreateScript() + "\n-- seed " + SeedVersion));
        var version = BitConverter.ToInt32(hash, 0) & int.MaxValue;
        return version == 0 ? 1 : version;
    }

    /// <summary>The schema version stored in the demo database file (0 when none was stored).</summary>
    public async Task<int> ReadSchemaVersionAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection($"Data Source={options.DatabasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private async Task WriteSchemaVersionAsync(int version, CancellationToken ct)
    {
        await using var connection = new SqliteConnection($"Data Source={options.DatabasePath};Pooling=False");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version = " + version.ToString(CultureInfo.InvariantCulture) + ";";
        await command.ExecuteNonQueryAsync(ct);
    }

    private void DeleteDatabaseFiles()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var path = options.DatabasePath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>Empties every table (keeping the file, which another connection may hold open on Windows).</summary>
    private static async Task WipeAsync(AppDbContext db, CancellationToken ct)
    {
        var tables = db.Model.GetEntityTypes().Select(t => t.GetTableName()).Where(t => t is not null).Distinct().ToList();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;", ct);
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                foreach (var table in tables)
#pragma warning disable EF1002 // Table names come from the EF model, not from input.
                    await db.Database.ExecuteSqlRawAsync($"DELETE FROM \"{table}\";", ct);
#pragma warning restore EF1002
                try
                {
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM sqlite_sequence;", ct);
                }
                catch (SqliteException)
                {
                    // No AUTOINCREMENT table yet.
                }
                await tx.CommitAsync(ct);
            }
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;", ct);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
