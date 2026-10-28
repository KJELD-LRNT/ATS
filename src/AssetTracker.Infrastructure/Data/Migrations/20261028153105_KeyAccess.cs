using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetTracker.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class KeyAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccessBuildings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SiteId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessBuildings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessBuildings_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "KeyAccessRevisions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeyAccessRevisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccessRooms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BuildingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FloorId = table.Column<int>(type: "int", nullable: true),
                    DrawingRoomId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessRooms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessRooms_AccessBuildings_BuildingId",
                        column: x => x.BuildingId,
                        principalTable: "AccessBuildings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccessRooms_Floors_FloorId",
                        column: x => x.FloorId,
                        principalTable: "Floors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "KeyDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BuildingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeyDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KeyDefinitions_AccessBuildings_BuildingId",
                        column: x => x.BuildingId,
                        principalTable: "AccessBuildings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DigitalLocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BuildingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoomId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Manufacturer = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ExternalId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Location = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Methods = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DigitalLocks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DigitalLocks_AccessBuildings_BuildingId",
                        column: x => x.BuildingId,
                        principalTable: "AccessBuildings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DigitalLocks_AccessRooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "AccessRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "KeyLinks",
                columns: table => new
                {
                    MasterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubordinateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeyLinks", x => new { x.MasterId, x.SubordinateId });
                    table.ForeignKey(
                        name: "FK_KeyLinks_KeyDefinitions_MasterId",
                        column: x => x.MasterId,
                        principalTable: "KeyDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_KeyLinks_KeyDefinitions_SubordinateId",
                        column: x => x.SubordinateId,
                        principalTable: "KeyDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "KeyRooms",
                columns: table => new
                {
                    KeyDefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeyRooms", x => new { x.KeyDefinitionId, x.RoomId });
                    table.ForeignKey(
                        name: "FK_KeyRooms_AccessRooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "AccessRooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_KeyRooms_KeyDefinitions_KeyDefinitionId",
                        column: x => x.KeyDefinitionId,
                        principalTable: "KeyDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DigitalPermissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PersonId = table.Column<int>(type: "int", nullable: false),
                    Method = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IssuedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    VerificationReference = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DigitalPermissions", x => x.Id);
                    table.CheckConstraint("CK_DigitalPermission_Window", "[EndsAt] > [StartsAt]");
                    table.ForeignKey(
                        name: "FK_DigitalPermissions_DigitalLocks_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "DigitalLocks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DigitalPermissions_People_PersonId",
                        column: x => x.PersonId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "KeyCopies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    KeyDefinitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Tag = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StorageLocation = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LockboxId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeyCopies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KeyCopies_DigitalLocks_LockboxId",
                        column: x => x.LockboxId,
                        principalTable: "DigitalLocks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_KeyCopies_KeyDefinitions_KeyDefinitionId",
                        column: x => x.KeyDefinitionId,
                        principalTable: "KeyDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "KeyLoans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CopyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PersonId = table.Column<int>(type: "int", nullable: false),
                    PermissionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CollectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReturnReportedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    IssuedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ReceivedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ReturnCondition = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AccessSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeyLoans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KeyLoans_DigitalPermissions_PermissionId",
                        column: x => x.PermissionId,
                        principalTable: "DigitalPermissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_KeyLoans_KeyCopies_CopyId",
                        column: x => x.CopyId,
                        principalTable: "KeyCopies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_KeyLoans_People_PersonId",
                        column: x => x.PersonId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "KeyAccessRevisions",
                columns: new[] { "Id", "Version" },
                values: new object[] { 1, 0L });

            migrationBuilder.CreateIndex(
                name: "IX_AccessBuildings_SiteId_Name",
                table: "AccessBuildings",
                columns: new[] { "SiteId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessRooms_BuildingId_Code",
                table: "AccessRooms",
                columns: new[] { "BuildingId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessRooms_FloorId",
                table: "AccessRooms",
                column: "FloorId");

            migrationBuilder.CreateIndex(
                name: "IX_DigitalLocks_BuildingId_Name",
                table: "DigitalLocks",
                columns: new[] { "BuildingId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DigitalLocks_RoomId",
                table: "DigitalLocks",
                column: "RoomId");

            migrationBuilder.CreateIndex(
                name: "IX_DigitalPermissions_DeviceId",
                table: "DigitalPermissions",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DigitalPermissions_PersonId_State",
                table: "DigitalPermissions",
                columns: new[] { "PersonId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_KeyCopies_KeyDefinitionId",
                table: "KeyCopies",
                column: "KeyDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_KeyCopies_LockboxId",
                table: "KeyCopies",
                column: "LockboxId");

            migrationBuilder.CreateIndex(
                name: "IX_KeyCopies_Tag",
                table: "KeyCopies",
                column: "Tag",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KeyDefinitions_BuildingId_Code",
                table: "KeyDefinitions",
                columns: new[] { "BuildingId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KeyLinks_SubordinateId",
                table: "KeyLinks",
                column: "SubordinateId");

            migrationBuilder.CreateIndex(
                name: "IX_KeyLoans_PermissionId",
                table: "KeyLoans",
                column: "PermissionId");

            migrationBuilder.CreateIndex(
                name: "IX_KeyLoans_PersonId_ClosedAt",
                table: "KeyLoans",
                columns: new[] { "PersonId", "ClosedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_KeyLoan_OpenCopy",
                table: "KeyLoans",
                column: "CopyId",
                unique: true,
                filter: "[ClosedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_KeyRooms_RoomId",
                table: "KeyRooms",
                column: "RoomId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KeyAccessRevisions");

            migrationBuilder.DropTable(
                name: "KeyLinks");

            migrationBuilder.DropTable(
                name: "KeyLoans");

            migrationBuilder.DropTable(
                name: "KeyRooms");

            migrationBuilder.DropTable(
                name: "DigitalPermissions");

            migrationBuilder.DropTable(
                name: "KeyCopies");

            migrationBuilder.DropTable(
                name: "DigitalLocks");

            migrationBuilder.DropTable(
                name: "KeyDefinitions");

            migrationBuilder.DropTable(
                name: "AccessRooms");

            migrationBuilder.DropTable(
                name: "AccessBuildings");
        }
    }
}
