using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetTracker.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AssetCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Type",
                table: "Pools",
                newName: "TypeId");

            migrationBuilder.RenameColumn(
                name: "Type",
                table: "IndividualAssets",
                newName: "TypeId");

            migrationBuilder.RenameIndex(
                name: "IX_IndividualAssets_SiteId_Type_State",
                table: "IndividualAssets",
                newName: "IX_IndividualAssets_SiteId_TypeId_State");

            migrationBuilder.AddColumn<string>(
                name: "TagCode",
                table: "Sites",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ModelId",
                table: "IndividualAssets",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VendorId",
                table: "IndividualAssets",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssetTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Mode = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    UsesModel = table.Column<bool>(type: "bit", nullable: false),
                    UsesDescription = table.Column<bool>(type: "bit", nullable: false),
                    UsesSerialNumber = table.Column<bool>(type: "bit", nullable: false),
                    UsesCondition = table.Column<bool>(type: "bit", nullable: false),
                    TagPattern = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Vendors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vendors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AssetFields",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TypeId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    IsRequired = table.Column<bool>(type: "bit", nullable: false),
                    IsUnique = table.Column<bool>(type: "bit", nullable: false),
                    IsSensitive = table.Column<bool>(type: "bit", nullable: false),
                    MaxValues = table.Column<int>(type: "int", nullable: false),
                    Options = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetFields", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssetFields_AssetTypes_TypeId",
                        column: x => x.TypeId,
                        principalTable: "AssetTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TagCounters",
                columns: table => new
                {
                    TypeId = table.Column<int>(type: "int", nullable: false),
                    SiteId = table.Column<int>(type: "int", nullable: false),
                    LastNumber = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagCounters", x => new { x.TypeId, x.SiteId });
                    table.ForeignKey(
                        name: "FK_TagCounters_AssetTypes_TypeId",
                        column: x => x.TypeId,
                        principalTable: "AssetTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TagCounters_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssetModels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    VendorId = table.Column<int>(type: "int", nullable: false),
                    TypeId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetModels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssetModels_AssetTypes_TypeId",
                        column: x => x.TypeId,
                        principalTable: "AssetTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetModels_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AssetFieldValues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AssetId = table.Column<int>(type: "int", nullable: false),
                    FieldId = table.Column<int>(type: "int", nullable: false),
                    Position = table.Column<int>(type: "int", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    UniqueKey = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssetFieldValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssetFieldValues_AssetFields_FieldId",
                        column: x => x.FieldId,
                        principalTable: "AssetFields",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssetFieldValues_IndividualAssets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "IndividualAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // ---- Data: the types and fields every installation starts with, then the existing IMEIs and phone numbers.
            // The ids equal the old AssetType enum values (1-7), so IndividualAssets.TypeId / Pools.TypeId (renamed from
            // Type above) already point at the right type. Keep in step with CatalogDefaults and Domain/AssetType.cs (BuiltIn).
            migrationBuilder.InsertData(
                table: "AssetTypes",
                columns: new[] { "Id", "Name", "Mode", "IsActive", "UsesModel", "UsesDescription", "UsesSerialNumber", "UsesCondition", "TagPattern" },
                values: new object[,]
                {
                    { 1, "Desktop", 1, true, true, true, true, true, null },
                    { 2, "Laptop", 1, true, true, true, true, true, null },
                    { 3, "Phone", 1, true, true, true, true, true, null },
                    { 4, "Phone number", 1, true, false, false, false, false, null },
                    { 5, "Peripheral", 2, true, false, false, false, false, null },
                    { 6, "Webcam", 2, true, false, false, false, false, null },
                    { 7, "Headset", 2, true, false, false, false, false, null },
                });

            migrationBuilder.InsertData(
                table: "AssetFields",
                columns: new[] { "Id", "TypeId", "Name", "Kind", "IsRequired", "IsUnique", "IsSensitive", "MaxValues", "Options", "SortOrder", "IsActive" },
                values: new object[,]
                {
                    { 1, 3, "IMEI", 6, false, true, false, 2, null, 10, true },
                    { 2, 3, "Phone number", 5, false, true, false, 1, null, 20, true },
                    { 3, 4, "Phone number", 5, true, true, false, 1, null, 10, true },
                    { 4, 4, "Provider", 1, false, false, false, 1, null, 20, true },
                    { 5, 4, "SIM type", 4, false, false, false, 1, "Physical SIM\neSIM", 30, true },
                    { 6, 4, "SIM status", 4, false, false, false, 1, "Active\nInactive\nSuspended\nBlocked\nLost or stolen", 40, true },
                    { 7, 4, "SIM number", 1, false, true, false, 1, null, 50, true },
                    { 8, 4, "PUC", 1, false, false, true, 1, null, 60, true },
                });

            // A retired asset keeps its value but not the unique key, so the number can be reused (as the old filtered indexes did).
            migrationBuilder.Sql(@"
INSERT INTO AssetFieldValues (AssetId, FieldId, Position, [Value], UniqueKey)
SELECT Id, 1, 0, Imei, CASE WHEN RetiredAt IS NULL THEN 'Imei:' + Imei END
FROM IndividualAssets WHERE Imei IS NOT NULL;");
            migrationBuilder.Sql(@"
INSERT INTO AssetFieldValues (AssetId, FieldId, Position, [Value], UniqueKey)
SELECT Id, CASE WHEN TypeId = 4 THEN 3 ELSE 2 END, 0, PhoneNumber, CASE WHEN RetiredAt IS NULL THEN 'PhoneNumber:' + PhoneNumber END
FROM IndividualAssets WHERE PhoneNumber IS NOT NULL;");

            migrationBuilder.DropIndex(
                name: "IX_IndividualAssets_Imei",
                table: "IndividualAssets");

            migrationBuilder.DropIndex(
                name: "IX_IndividualAssets_PhoneNumber",
                table: "IndividualAssets");

            migrationBuilder.DropColumn(
                name: "Imei",
                table: "IndividualAssets");

            migrationBuilder.DropColumn(
                name: "PhoneNumber",
                table: "IndividualAssets");

            migrationBuilder.CreateIndex(
                name: "IX_Pools_TypeId",
                table: "Pools",
                column: "TypeId");

            migrationBuilder.CreateIndex(
                name: "IX_IndividualAssets_ModelId",
                table: "IndividualAssets",
                column: "ModelId");

            migrationBuilder.CreateIndex(
                name: "IX_IndividualAssets_TypeId",
                table: "IndividualAssets",
                column: "TypeId");

            migrationBuilder.CreateIndex(
                name: "IX_IndividualAssets_VendorId",
                table: "IndividualAssets",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetFields_TypeId_Name",
                table: "AssetFields",
                columns: new[] { "TypeId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetFieldValues_AssetId_FieldId_Position",
                table: "AssetFieldValues",
                columns: new[] { "AssetId", "FieldId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetFieldValues_FieldId",
                table: "AssetFieldValues",
                column: "FieldId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetFieldValues_UniqueKey",
                table: "AssetFieldValues",
                column: "UniqueKey",
                unique: true,
                filter: "[UniqueKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AssetModels_TypeId",
                table: "AssetModels",
                column: "TypeId");

            migrationBuilder.CreateIndex(
                name: "IX_AssetModels_VendorId_TypeId_Name",
                table: "AssetModels",
                columns: new[] { "VendorId", "TypeId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssetTypes_Name",
                table: "AssetTypes",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TagCounters_SiteId",
                table: "TagCounters",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_Vendors_Name",
                table: "Vendors",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_IndividualAssets_AssetModels_ModelId",
                table: "IndividualAssets",
                column: "ModelId",
                principalTable: "AssetModels",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_IndividualAssets_AssetTypes_TypeId",
                table: "IndividualAssets",
                column: "TypeId",
                principalTable: "AssetTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_IndividualAssets_Vendors_VendorId",
                table: "IndividualAssets",
                column: "VendorId",
                principalTable: "Vendors",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Pools_AssetTypes_TypeId",
                table: "Pools",
                column: "TypeId",
                principalTable: "AssetTypes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Back to one IMEI and one phone number per asset: the first IMEI and the first phone number are kept (a
            // second IMEI, vendors, models, SIM fields and the other asset types' fields are lost).
            migrationBuilder.AddColumn<string>(
                name: "Imei",
                table: "IndividualAssets",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhoneNumber",
                table: "IndividualAssets",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.Sql(@"
UPDATE a SET Imei = v.[Value]
FROM IndividualAssets a JOIN AssetFieldValues v ON v.AssetId = a.Id AND v.Position = 0
JOIN AssetFields f ON f.Id = v.FieldId AND f.Kind = 6;");
            migrationBuilder.Sql(@"
UPDATE a SET PhoneNumber = v.[Value]
FROM IndividualAssets a JOIN AssetFieldValues v ON v.AssetId = a.Id AND v.Position = 0
JOIN AssetFields f ON f.Id = v.FieldId AND f.Kind = 5 AND f.Id = (SELECT MIN(f2.Id) FROM AssetFields f2
    JOIN AssetFieldValues v2 ON v2.FieldId = f2.Id AND v2.AssetId = a.Id WHERE f2.Kind = 5);");

            migrationBuilder.DropForeignKey(
                name: "FK_IndividualAssets_AssetModels_ModelId",
                table: "IndividualAssets");

            migrationBuilder.DropForeignKey(
                name: "FK_IndividualAssets_AssetTypes_TypeId",
                table: "IndividualAssets");

            migrationBuilder.DropForeignKey(
                name: "FK_IndividualAssets_Vendors_VendorId",
                table: "IndividualAssets");

            migrationBuilder.DropForeignKey(
                name: "FK_Pools_AssetTypes_TypeId",
                table: "Pools");

            migrationBuilder.DropTable(
                name: "AssetFieldValues");

            migrationBuilder.DropTable(
                name: "AssetModels");

            migrationBuilder.DropTable(
                name: "TagCounters");

            migrationBuilder.DropTable(
                name: "AssetFields");

            migrationBuilder.DropTable(
                name: "Vendors");

            migrationBuilder.DropTable(
                name: "AssetTypes");

            migrationBuilder.DropIndex(
                name: "IX_Pools_TypeId",
                table: "Pools");

            migrationBuilder.DropIndex(
                name: "IX_IndividualAssets_ModelId",
                table: "IndividualAssets");

            migrationBuilder.DropIndex(
                name: "IX_IndividualAssets_TypeId",
                table: "IndividualAssets");

            migrationBuilder.DropIndex(
                name: "IX_IndividualAssets_VendorId",
                table: "IndividualAssets");

            migrationBuilder.DropColumn(
                name: "TagCode",
                table: "Sites");

            migrationBuilder.DropColumn(
                name: "ModelId",
                table: "IndividualAssets");

            migrationBuilder.DropColumn(
                name: "VendorId",
                table: "IndividualAssets");

            migrationBuilder.RenameColumn(
                name: "TypeId",
                table: "Pools",
                newName: "Type");

            migrationBuilder.RenameColumn(
                name: "TypeId",
                table: "IndividualAssets",
                newName: "Type");

            migrationBuilder.RenameIndex(
                name: "IX_IndividualAssets_SiteId_TypeId_State",
                table: "IndividualAssets",
                newName: "IX_IndividualAssets_SiteId_Type_State");

            migrationBuilder.CreateIndex(
                name: "IX_IndividualAssets_Imei",
                table: "IndividualAssets",
                column: "Imei",
                unique: true,
                filter: "[Imei] IS NOT NULL AND [RetiredAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_IndividualAssets_PhoneNumber",
                table: "IndividualAssets",
                column: "PhoneNumber",
                unique: true,
                filter: "[PhoneNumber] IS NOT NULL AND [RetiredAt] IS NULL");
        }
    }
}
