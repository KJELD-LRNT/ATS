using AssetTracker.Core.Data;
using AssetTracker.Infrastructure;
using AssetTracker.Tests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AssetTracker.Tests.Workplace;

/// <summary>
/// The <c>ZoneRename</c> migration is hand-written (renames instead of drop and create). The CI model check only proves
/// that the model and the snapshot match; this proves that an existing database keeps its teams as zones, the desk
/// links, and that the keys and indexes get the names EF expects. SQL Server only, on a database of its own.
/// </summary>
public class ZoneRenameMigrationTests
{
    private const string BeforeRename = "20261027191428_FloorPlanDesks";

    public sealed class ZoneRow { public int Id { get; set; } public string Name { get; set; } = ""; public int ColorIndex { get; set; } }
    public sealed class SpotRow { public string ElementId { get; set; } = ""; public int? ZoneId { get; set; } }

    [SqlServerFact]
    public async Task Upgrading_keeps_teams_as_zones_with_their_desks_and_renames_keys_and_indexes()
    {
        var cs = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(AppFactory.SqlServerEnvVar))
        {
            InitialCatalog = "ats_zone_rename_" + Guid.NewGuid().ToString("N"),
        }.ConnectionString;
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(cs, o => o.MigrationsAssembly(typeof(InfrastructureServiceCollectionExtensions).Assembly.FullName))
            .Options;

        await using var db = new AppDbContext(options);
        try
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(BeforeRename);

            // A floor with a team, one desk in it and one desk without, as the old schema stores them.
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO [Sites] ([Key], [Name], [IsActive]) VALUES (N'MIG1', N'Migration site', 1);
                DECLARE @site int = SCOPE_IDENTITY();
                INSERT INTO [Floors] ([SiteId], [Name], [SortOrder], [Svg], [Css], [ViewBox], [Version], [ImportedAt], [ImportedBy], [IsActive])
                VALUES (@site, N'Floor 1', 0, N'<svg xmlns="http://www.w3.org/2000/svg"/>', N'', N'0 0 10 10', 1, SYSDATETIMEOFFSET(), N'test', 1);
                DECLARE @floor int = SCOPE_IDENTITY();
                INSERT INTO [FloorTeams] ([FloorId], [Name], [ColorIndex], [CreatedAt]) VALUES (@floor, N'Finance', 5, SYSDATETIMEOFFSET());
                DECLARE @team int = SCOPE_IDENTITY();
                INSERT INTO [Workspots] ([FloorId], [ElementId], [Label], [TeamId], [IsActive], [IsDesk], [CreatedAt])
                VALUES (@floor, N'w1', N'w1', @team, 1, 1, SYSDATETIMEOFFSET()),
                       (@floor, N'w2', N'w2', NULL, 1, 1, SYSDATETIMEOFFSET());
                """);
            var teamId = await db.Database.SqlQueryRaw<int>("SELECT [Id] AS [Value] FROM [FloorTeams]").SingleAsync();

            await migrator.MigrateAsync();

            var zones = await db.Database.SqlQueryRaw<ZoneRow>("SELECT [Id], [Name], [ColorIndex] FROM [FloorZones]").ToListAsync();
            var zone = Assert.Single(zones);
            Assert.Equal((teamId, "Finance", 5), (zone.Id, zone.Name, zone.ColorIndex));
            var spots = await db.Database.SqlQueryRaw<SpotRow>("SELECT [ElementId], [ZoneId] FROM [Workspots] ORDER BY [ElementId]").ToListAsync();
            Assert.Equal([("w1", (int?)teamId), ("w2", null)], spots.Select(x => (x.ElementId, x.ZoneId)));
            // and through the model
            Assert.Equal("Finance", (await db.Workspots.AsNoTracking().Include(w => w.Zone).SingleAsync(w => w.ElementId == "w1")).Zone!.Name);

            var names = await ConstraintAndIndexNamesAsync(db);
            Assert.Superset(new HashSet<string> { "PK_FloorZones", "FK_FloorZones_Floors_FloorId", "FK_Workspots_FloorZones_ZoneId",
                "IX_FloorZones_FloorId_Name", "IX_Workspots_ZoneId" }, names);
            Assert.DoesNotContain(names, n => n.Contains("Team", StringComparison.Ordinal));

            // Down puts the old names back, with the data.
            await migrator.MigrateAsync(BeforeRename);
            names = await ConstraintAndIndexNamesAsync(db);
            Assert.Superset(new HashSet<string> { "PK_FloorTeams", "FK_FloorTeams_Floors_FloorId", "FK_Workspots_FloorTeams_TeamId",
                "IX_FloorTeams_FloorId_Name", "IX_Workspots_TeamId" }, names);
            Assert.Equal(teamId, await db.Database.SqlQueryRaw<int>("SELECT [TeamId] AS [Value] FROM [Workspots] WHERE [ElementId] = N'w1'").SingleAsync());
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    private static async Task<HashSet<string>> ConstraintAndIndexNamesAsync(AppDbContext db) =>
    [
        .. await db.Database.SqlQueryRaw<string>("""
            SELECT o.[name] AS [Value] FROM sys.objects o WHERE o.[type] IN ('PK', 'F') AND OBJECT_NAME(o.parent_object_id) IN ('FloorZones', 'FloorTeams', 'Workspots')
            UNION ALL
            SELECT i.[name] FROM sys.indexes i WHERE i.[name] IS NOT NULL AND OBJECT_NAME(i.[object_id]) IN ('FloorZones', 'FloorTeams', 'Workspots')
            """).ToListAsync(),
    ];
}
