using System.Net;
using AssetTracker.Core.Security;
using AssetTracker.Infrastructure.Demo;
using AssetTracker.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetTracker.Tests.Demo;

/// <summary>The demo super admin (demo mode only, password shown on /Demo) and the demo database schema rebuild.</summary>
public class DemoSuperAdminTests : IClassFixture<DemoAppFactory>
{
    private readonly DemoAppFactory _f;
    public DemoSuperAdminTests(DemoAppFactory f) => _f = f;

    [Fact]
    public async Task The_demo_super_admin_signs_in_with_the_published_password_and_gets_admin_only()
    {
        var client = _f.CreateDemoClient();
        var demo = await client.GetStringAsync("/Demo");
        Assert.Contains(SuperAdmin.DemoPassword, demo);
        Assert.Contains("/superadmin", demo);
        // The default demo user is IT + admin: inventory is open before the super admin signs in.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Assets")).StatusCode);

        var res = await FormHelper.PostFormAsync(client, "/superadmin", "/superadmin",
            ("UserName", SuperAdmin.UserName), ("Password", SuperAdmin.DemoPassword));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var settings = await client.GetStringAsync("/Admin/Settings");
        Assert.Contains("Signed in as the super admin (not an AD account).", settings);
        Assert.Contains(SuperAdmin.DisplayName, settings);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/Assets")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/Audit")).StatusCode);

        // Signing out returns this browser to the demo user.
        res = await FormHelper.PostFormAsync(client, "/superadmin/account", "/superadmin/signout");
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Assets")).StatusCode);
    }

    [Fact]
    public async Task Reset_puts_the_demo_super_admin_back()
    {
        await _f.WithDbAsync(db => db.LocalAccounts.ExecuteDeleteAsync());
        await _f.Services.GetRequiredService<DemoDataManager>().ResetAsync();
        var account = await _f.WithDbAsync(db => db.LocalAccounts.AsNoTracking().SingleAsync());
        Assert.True(account.IsEnabled);
        Assert.Equal(Microsoft.AspNetCore.Identity.PasswordVerificationResult.Success,
            SuperAdmin.VerifyPassword(account.PasswordHash!, SuperAdmin.DemoPassword));
    }
}

/// <summary>EnsureCreated does nothing on an existing file: a demo.db from another app version is rebuilt.</summary>
public class DemoSchemaRebuildTests
{
    private static async Task<int> UserVersionAsync(string dbPath)
    {
        await using var c = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task A_demo_database_with_another_schema_version_is_rebuilt_and_a_current_one_is_kept()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ats_demo_schema_" + Guid.NewGuid().ToString("N"));
        var dbPath = Path.Combine(dir, "demo.db");
        try
        {
            // An old demo.db: no LocalAccounts table, no stored schema version (user_version 0).
            System.IO.Directory.CreateDirectory(dir);
            await using (var c = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                await c.OpenAsync();
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "CREATE TABLE Sites (Id INTEGER PRIMARY KEY, Key TEXT); INSERT INTO Sites (Key) VALUES ('OLD');";
                await cmd.ExecuteNonQueryAsync();
            }

            int version;
            using (var f = new DemoAppFactory { DataDirectory = dir, KeepDataDirectory = true })
            {
                f.CreateDemoClient();
                Assert.False(await f.WithDbAsync(db => db.Sites.AnyAsync(s => s.Key == "OLD")));
                Assert.True(await f.WithDbAsync(db => db.LocalAccounts.AnyAsync(a => a.UserName == SuperAdmin.UserName)));
                version = await f.WithDbAsync(db => Task.FromResult(DemoDataManager.SchemaVersion(db)));
                // A marker that must survive the next start (same version: no rebuild).
                await f.WithDbAsync(async db =>
                {
                    await db.Database.ExecuteSqlRawAsync("UPDATE \"Sites\" SET \"Name\" = 'Kept across restarts' WHERE \"Key\" = 'AMS';");
                    return 0;
                });
            }
            SqliteConnection.ClearAllPools();
            Assert.Equal(version, await UserVersionAsync(dbPath));

            using (var f = new DemoAppFactory { DataDirectory = dir, KeepDataDirectory = true })
            {
                f.CreateDemoClient();
                Assert.True(await f.WithDbAsync(db => db.Sites.AnyAsync(s => s.Name == "Kept across restarts")));
            }
            SqliteConnection.ClearAllPools();

            // Another version stored: rebuilt from scratch and seeded again.
            await using (var c = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                await c.OpenAsync();
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "PRAGMA user_version = 12345;";
                await cmd.ExecuteNonQueryAsync();
            }
            using (var f = new DemoAppFactory { DataDirectory = dir, KeepDataDirectory = true })
            {
                f.CreateDemoClient();
                Assert.False(await f.WithDbAsync(db => db.Sites.AnyAsync(s => s.Name == "Kept across restarts")));
                Assert.True(await f.WithDbAsync(db => db.Sites.AnyAsync(s => s.Key == "AMS")));
            }
            SqliteConnection.ClearAllPools();
            Assert.Equal(version, await UserVersionAsync(dbPath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
