using AssetTracker.Core.Data;
using AssetTracker.Core.Domain;
using AssetTracker.Infrastructure;
using AssetTracker.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AssetTracker.Tests.Keys;

public class KeyAccessMigrationTests
{
    [SqlServerFact]
    public async Task Upgrade_preserves_existing_data_adds_key_access_and_rolls_back_cleanly()
    {
        const string before = "20261028103330_LocalAccounts";
        var cs = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(AppFactory.SqlServerEnvVar))
            { InitialCatalog = "ats_keys_" + Guid.NewGuid().ToString("N") }.ConnectionString;
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs,
            x => x.MigrationsAssembly(typeof(InfrastructureServiceCollectionExtensions).Assembly.FullName)).Options);
        try
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(before);
            db.Sites.Add(new Site { Key = "KEY-UPGRADE", Name = "Existing site", IsActive = true });
            await db.SaveChangesAsync();
            await migrator.MigrateAsync();
            Assert.Equal("Existing site", (await db.Sites.SingleAsync(x => x.Key == "KEY-UPGRADE")).Name);
            Assert.Equal(0, await db.KeyAccessRevisions.Select(x => x.Version).SingleAsync());
            var siteId = await db.Sites.Where(x => x.Key == "KEY-UPGRADE").Select(x => x.Id).SingleAsync();
            var building = new AccessBuilding { SiteId = siteId, Name = "Building A" };
            db.AccessBuildings.Add(building);
            var room = new AccessRoom { Building = building, Code = "101", Name = "Finance" };
            db.AccessRooms.Add(room);
            var definition = new KeyDefinition { Building = building, Code = "FIN", Name = "Finance key" };
            db.KeyDefinitions.Add(definition);
            db.KeyRooms.Add(new KeyRoom { KeyDefinitionId = definition.Id, RoomId = room.Id });
            db.KeyCopies.Add(new KeyCopy { Definition = definition, Tag = "FIN-001", StorageLocation = "Cabinet" });
            await db.SaveChangesAsync();
            Assert.Single(await db.KeyCopies.ToListAsync());
            var person = await Seed.PersonAsync(db, "KEY-HOLDER");
            var device = new DigitalLock { BuildingId = building.Id, RoomId = room.Id, Name = "Door", Kind = DigitalLockKind.Door, Methods = LockOpeningMethod.Pin };
            db.DigitalLocks.Add(device);
            var permission = new DigitalPermission { DeviceId = device.Id, PersonId = person.Id, StartsAt = DateTimeOffset.UtcNow,
                EndsAt = null, State = DigitalPermissionState.Confirmed, Method = LockOpeningMethod.Pin };
            db.DigitalPermissions.Add(permission);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            // A downgrade maps ongoing access to the furthest expiry, satisfying the original window constraint.
            await migrator.MigrateAsync("20261028153105_KeyAccess");
            Assert.Equal(DateTimeOffset.MaxValue, await db.DigitalPermissions.Where(p => p.Id == permission.Id).Select(p => p.EndsAt).SingleAsync());
            await migrator.MigrateAsync();
            db.ChangeTracker.Clear();
            await migrator.MigrateAsync(before);
            Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.tables WHERE [name] = 'KeyLoans'").SingleAsync());
            Assert.True(await db.Sites.AnyAsync(x => x.Key == "KEY-UPGRADE"));
        }
        finally { await db.Database.EnsureDeletedAsync(); }
    }
}
