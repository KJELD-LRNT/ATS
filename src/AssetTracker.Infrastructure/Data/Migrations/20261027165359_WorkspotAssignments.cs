using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetTracker.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class WorkspotAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkspotAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WorkspotId = table.Column<int>(type: "int", nullable: false),
                    PersonId = table.Column<int>(type: "int", nullable: true),
                    AssetId = table.Column<int>(type: "int", nullable: true),
                    PoolId = table.Column<int>(type: "int", nullable: true),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    AssignedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AssignedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReleasedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspotAssignments", x => x.Id);
                    table.CheckConstraint("CK_WorkspotAssignment_OneKind", "([PersonId] IS NOT NULL AND [AssetId] IS NULL AND [PoolId] IS NULL) OR ([PersonId] IS NULL AND [AssetId] IS NOT NULL AND [PoolId] IS NULL) OR ([PersonId] IS NULL AND [AssetId] IS NULL AND [PoolId] IS NOT NULL)");
                    table.CheckConstraint("CK_WorkspotAssignment_Quantity", "[Quantity] > 0");
                    table.ForeignKey(
                        name: "FK_WorkspotAssignments_IndividualAssets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "IndividualAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkspotAssignments_People_PersonId",
                        column: x => x.PersonId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkspotAssignments_Pools_PoolId",
                        column: x => x.PoolId,
                        principalTable: "Pools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkspotAssignments_Workspots_WorkspotId",
                        column: x => x.WorkspotId,
                        principalTable: "Workspots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkspotAssignments_PoolId",
                table: "WorkspotAssignments",
                column: "PoolId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspotAssignments_WorkspotId_ReleasedAt",
                table: "WorkspotAssignments",
                columns: new[] { "WorkspotId", "ReleasedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_WorkspotAssignments_OpenAsset",
                table: "WorkspotAssignments",
                column: "AssetId",
                unique: true,
                filter: "[AssetId] IS NOT NULL AND [ReleasedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_WorkspotAssignments_OpenPerson",
                table: "WorkspotAssignments",
                column: "PersonId",
                unique: true,
                filter: "[PersonId] IS NOT NULL AND [ReleasedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_WorkspotAssignments_OpenPool",
                table: "WorkspotAssignments",
                columns: new[] { "WorkspotId", "PoolId" },
                unique: true,
                filter: "[PoolId] IS NOT NULL AND [ReleasedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_WorkspotAssignments_OpenSeatPerson",
                table: "WorkspotAssignments",
                column: "WorkspotId",
                unique: true,
                filter: "[PersonId] IS NOT NULL AND [ReleasedAt] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkspotAssignments");
        }
    }
}
