using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetTracker.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class FloorPlanDesks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Floor plans place only people, desktops and screens: pool placements go, and each row records what it placed.
            migrationBuilder.DropForeignKey(
                name: "FK_WorkspotAssignments_Pools_PoolId",
                table: "WorkspotAssignments");

            migrationBuilder.DropIndex(
                name: "IX_WorkspotAssignments_PoolId",
                table: "WorkspotAssignments");

            migrationBuilder.DropIndex(
                name: "UX_WorkspotAssignments_OpenPool",
                table: "WorkspotAssignments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkspotAssignment_OneKind",
                table: "WorkspotAssignments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkspotAssignment_Quantity",
                table: "WorkspotAssignments");

            migrationBuilder.Sql("DELETE FROM [WorkspotAssignments] WHERE [PoolId] IS NOT NULL;");

            migrationBuilder.DropColumn(
                name: "PoolId",
                table: "WorkspotAssignments");

            migrationBuilder.DropColumn(
                name: "Quantity",
                table: "WorkspotAssignments");

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "WorkspotAssignments",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsDesk",
                table: "Workspots",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "WorkspotRole",
                table: "AssetTypes",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // The built-in Desktop type (id 1) is a desktop; a Screen type is added unless one exists by that name.
            migrationBuilder.Sql("UPDATE [AssetTypes] SET [WorkspotRole] = 1 WHERE [Id] = 1 AND [Mode] = 1;");
            migrationBuilder.Sql(
                "IF NOT EXISTS (SELECT 1 FROM [AssetTypes] WHERE [Name] = N'Screen') " +
                "INSERT INTO [AssetTypes] ([Name], [Mode], [IsActive], [UsesModel], [UsesDescription], [UsesSerialNumber], [UsesCondition], [TagPattern], [WorkspotRole]) " +
                "VALUES (N'Screen', 1, 1, 1, 1, 1, 1, NULL, 2) " +
                "ELSE UPDATE [AssetTypes] SET [WorkspotRole] = 2 WHERE [Name] = N'Screen' AND [Mode] = 1;");

            // Seats in canteens, meeting rooms and the like are not desks.
            migrationBuilder.Sql(
                "UPDATE [Workspots] SET [IsDesk] = 0 WHERE [RoomType] IN (N'canteen', N'meeting', N'support', N'core', N'workshop', N'kitchen', N'lounge');");

            // What each existing row placed: a person, or an asset as desktop or screen. Assets of other types (laptops,
            // phones) are no longer placed at desks: their open rows are closed and kept as history.
            migrationBuilder.Sql("UPDATE [WorkspotAssignments] SET [Kind] = 1 WHERE [PersonId] IS NOT NULL;");
            migrationBuilder.Sql(
                "UPDATE w SET [Kind] = CASE WHEN t.[WorkspotRole] = 1 THEN 2 ELSE 3 END, " +
                "[ReleasedAt] = CASE WHEN t.[WorkspotRole] = 0 AND w.[ReleasedAt] IS NULL THEN SYSDATETIMEOFFSET() ELSE w.[ReleasedAt] END, " +
                "[ReleasedBy] = CASE WHEN t.[WorkspotRole] = 0 AND w.[ReleasedAt] IS NULL THEN N'system:migration' ELSE w.[ReleasedBy] END " +
                "FROM [WorkspotAssignments] w JOIN [IndividualAssets] a ON a.[Id] = w.[AssetId] JOIN [AssetTypes] t ON t.[Id] = a.[TypeId];");

            migrationBuilder.CreateIndex(
                name: "UX_WorkspotAssignments_OpenSeatDesktop",
                table: "WorkspotAssignments",
                columns: new[] { "WorkspotId", "Kind" },
                unique: true,
                filter: "[Kind] = 2 AND [ReleasedAt] IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkspotAssignment_Kind",
                table: "WorkspotAssignments",
                sql: "([Kind] = 1 AND [PersonId] IS NOT NULL AND [AssetId] IS NULL) OR ([Kind] IN (2, 3) AND [PersonId] IS NULL AND [AssetId] IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_WorkspotAssignments_OpenSeatDesktop",
                table: "WorkspotAssignments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkspotAssignment_Kind",
                table: "WorkspotAssignments");

            migrationBuilder.DropColumn(
                name: "IsDesk",
                table: "Workspots");

            migrationBuilder.DropColumn(
                name: "WorkspotRole",
                table: "AssetTypes");

            migrationBuilder.RenameColumn(
                name: "Kind",
                table: "WorkspotAssignments",
                newName: "Quantity");

            migrationBuilder.Sql("UPDATE [WorkspotAssignments] SET [Quantity] = 1;");

            migrationBuilder.AddColumn<int>(
                name: "PoolId",
                table: "WorkspotAssignments",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkspotAssignments_PoolId",
                table: "WorkspotAssignments",
                column: "PoolId");

            migrationBuilder.CreateIndex(
                name: "UX_WorkspotAssignments_OpenPool",
                table: "WorkspotAssignments",
                columns: new[] { "WorkspotId", "PoolId" },
                unique: true,
                filter: "[PoolId] IS NOT NULL AND [ReleasedAt] IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkspotAssignment_OneKind",
                table: "WorkspotAssignments",
                sql: "([PersonId] IS NOT NULL AND [AssetId] IS NULL AND [PoolId] IS NULL) OR ([PersonId] IS NULL AND [AssetId] IS NOT NULL AND [PoolId] IS NULL) OR ([PersonId] IS NULL AND [AssetId] IS NULL AND [PoolId] IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkspotAssignment_Quantity",
                table: "WorkspotAssignments",
                sql: "[Quantity] > 0");

            migrationBuilder.AddForeignKey(
                name: "FK_WorkspotAssignments_Pools_PoolId",
                table: "WorkspotAssignments",
                column: "PoolId",
                principalTable: "Pools",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
