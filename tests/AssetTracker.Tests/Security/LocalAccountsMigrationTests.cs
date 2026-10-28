using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Core.Security;
using AssetTracker.Infrastructure;
using AssetTracker.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AssetTracker.Tests.Security;

/// <summary>
/// Upgrading an existing database with the <c>LocalAccounts</c> migration: existing data stays, the table and its unique
/// username index appear, the model can use it, and Down removes it again. SQL Server only (the migrations are SQL Server
/// specific), on a database of its own; it runs in the CI SQL Server job.
/// </summary>
public class LocalAccountsMigrationTests
{
    private const string Before = "20261027231820_ZoneRename";

    [SqlServerFact]
    public async Task Upgrading_adds_the_local_accounts_table_and_keeps_existing_data()
    {
        var cs = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(AppFactory.SqlServerEnvVar))
        {
            InitialCatalog = "ats_local_accounts_" + Guid.NewGuid().ToString("N"),
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(cs, o => o.MigrationsAssembly(typeof(InfrastructureServiceCollectionExtensions).Assembly.FullName))
            .Options;

        await using var db = new AppDbContext(options);
        try
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(Before);
            await db.Database.ExecuteSqlRawAsync("INSERT INTO [Sites] ([Key], [Name], [IsActive]) VALUES (N'MIGLA', N'Before local accounts', 1);");

            await migrator.MigrateAsync();

            Assert.Equal("Before local accounts", (await db.Sites.AsNoTracking().SingleAsync(s => s.Key == "MIGLA")).Name);
            db.LocalAccounts.Add(new LocalAccount
            {
                UserName = SuperAdmin.UserName, PasswordHash = SuperAdmin.HashPassword("a long enough test password"),
                IsEnabled = true, SecurityStamp = SuperAdmin.NewSecurityStamp(), PasswordChangedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            Assert.True((await db.LocalAccounts.SingleAsync()).IsEnabled);

            // The username is unique.
            db.LocalAccounts.Add(new LocalAccount { UserName = SuperAdmin.UserName, SecurityStamp = "x" });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.ChangeTracker.Clear();

            var indexes = await db.Database.SqlQueryRaw<string>(
                "SELECT i.[name] AS [Value] FROM sys.indexes i WHERE OBJECT_NAME(i.[object_id]) = 'LocalAccounts' AND i.[name] IS NOT NULL").ToListAsync();
            Assert.Superset(new HashSet<string> { "PK_LocalAccounts", "IX_LocalAccounts_UserName" }, indexes.ToHashSet());

            // The atomic counter update the sign-in uses translates on SQL Server.
            var now = DateTimeOffset.UtcNow;
            Assert.Equal(1, await db.LocalAccounts
                .Where(a => (a.LockedUntil == null && a.FailedCount < SuperAdmin.MaxFailedAttempts) || a.LockedUntil <= now)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.FailedCount, a => a.LockedUntil != null ? 1 : a.FailedCount + 1)
                    .SetProperty(a => a.LockedUntil, (DateTimeOffset?)null)));
            Assert.Equal(1, await db.LocalAccounts.AsNoTracking().Select(a => a.FailedCount).SingleAsync());

            await migrator.MigrateAsync(Before);
            Assert.Equal(0, await db.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS [Value] FROM sys.tables WHERE [name] = 'LocalAccounts'").SingleAsync());
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }
}
