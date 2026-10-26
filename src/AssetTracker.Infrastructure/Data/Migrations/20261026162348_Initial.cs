using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetTracker.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SiteId = table.Column<int>(type: "int", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DetailsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Sites",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Key = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DirectoryValue = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    TimeZoneId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sites", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    TriggeredBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Fetched = table.Column<int>(type: "int", nullable: false),
                    Mapped = table.Column<int>(type: "int", nullable: false),
                    Quarantined = table.Column<int>(type: "int", nullable: false),
                    Created = table.Column<int>(type: "int", nullable: false),
                    Updated = table.Column<int>(type: "int", nullable: false),
                    Deactivated = table.Column<int>(type: "int", nullable: false),
                    IssuesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IndividualAssets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Tag = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    SiteId = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Condition = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SerialNumber = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Imei = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    PhoneNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    State = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RetiredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndividualAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IndividualAssets_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ManagerSiteGrants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PrincipalDirectoryId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PrincipalName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    SiteId = table.Column<int>(type: "int", nullable: false),
                    GrantedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    GrantedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManagerSiteGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManagerSiteGrants_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "People",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DirectoryId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Sid = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Username = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    JobTitle = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    SiteId = table.Column<int>(type: "int", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsAnonymized = table.Column<bool>(type: "bit", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_People", x => x.Id);
                    table.ForeignKey(
                        name: "FK_People_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Pools",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Sku = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SiteId = table.Column<int>(type: "int", nullable: false),
                    Available = table.Column<int>(type: "int", nullable: false),
                    Outstanding = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pools", x => x.Id);
                    table.CheckConstraint("CK_Pool_Available_NonNegative", "[Available] >= 0");
                    table.CheckConstraint("CK_Pool_Outstanding_NonNegative", "[Outstanding] >= 0");
                    table.ForeignKey(
                        name: "FK_Pools_Sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "Sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Loans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AssetId = table.Column<int>(type: "int", nullable: true),
                    PoolId = table.Column<int>(type: "int", nullable: true),
                    HolderId = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    ReturnedQuantity = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CheckoutCondition = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CheckoutNote = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ReturnCondition = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReturnNote = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    HolderNameSnapshot = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    HolderEmailSnapshot = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    EffectiveVersion = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Loans", x => x.Id);
                    table.CheckConstraint("CK_Loan_ExactlyOneTarget", "([AssetId] IS NOT NULL AND [PoolId] IS NULL) OR ([AssetId] IS NULL AND [PoolId] IS NOT NULL)");
                    table.CheckConstraint("CK_Loan_Quantity_Positive", "[Quantity] > 0");
                    table.CheckConstraint("CK_Loan_Returned_Range", "[ReturnedQuantity] >= 0 AND [ReturnedQuantity] <= [Quantity]");
                    table.ForeignKey(
                        name: "FK_Loans_IndividualAssets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "IndividualAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Loans_People_HolderId",
                        column: x => x.HolderId,
                        principalTable: "People",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Loans_Pools_PoolId",
                        column: x => x.PoolId,
                        principalTable: "Pools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PoolId = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    QuantityDelta = table.Column<int>(type: "int", nullable: false),
                    AvailableAfter = table.Column<int>(type: "int", nullable: false),
                    OutstandingAfter = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CounterpartEventId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StockEvents_Pools_PoolId",
                        column: x => x.PoolId,
                        principalTable: "Pools",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LoanEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LoanId = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    HolderId = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Condition = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SupersedesEventId = table.Column<long>(type: "bigint", nullable: true),
                    ChangeJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DirectoryStale = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoanEvents_LoanEvents_SupersedesEventId",
                        column: x => x.SupersedesEventId,
                        principalTable: "LoanEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LoanEvents_Loans_LoanId",
                        column: x => x.LoanId,
                        principalTable: "Loans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "NotificationOutbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    DedupeKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    RecipientEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    SkipReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LoanId = table.Column<int>(type: "int", nullable: true),
                    LoanEventId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationOutbox", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationOutbox_LoanEvents_LoanEventId",
                        column: x => x.LoanEventId,
                        principalTable: "LoanEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_NotificationOutbox_Loans_LoanId",
                        column: x => x.LoanId,
                        principalTable: "Loans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_EntityType_EntityId",
                table: "AuditEvents",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_OccurredAt",
                table: "AuditEvents",
                column: "OccurredAt");

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

            migrationBuilder.CreateIndex(
                name: "IX_IndividualAssets_SiteId_Type_State",
                table: "IndividualAssets",
                columns: new[] { "SiteId", "Type", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_IndividualAssets_Tag",
                table: "IndividualAssets",
                column: "Tag",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoanEvents_LoanId",
                table: "LoanEvents",
                column: "LoanId");

            migrationBuilder.CreateIndex(
                name: "IX_LoanEvents_SupersedesEventId",
                table: "LoanEvents",
                column: "SupersedesEventId");

            migrationBuilder.CreateIndex(
                name: "IX_Loans_ClosedAt_DueDate",
                table: "Loans",
                columns: new[] { "ClosedAt", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Loans_HolderId_ClosedAt",
                table: "Loans",
                columns: new[] { "HolderId", "ClosedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Loans_PoolId_ClosedAt",
                table: "Loans",
                columns: new[] { "PoolId", "ClosedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_Loans_OpenAsset",
                table: "Loans",
                column: "AssetId",
                unique: true,
                filter: "[AssetId] IS NOT NULL AND [ClosedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ManagerSiteGrants_PrincipalDirectoryId_SiteId",
                table: "ManagerSiteGrants",
                columns: new[] { "PrincipalDirectoryId", "SiteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManagerSiteGrants_SiteId",
                table: "ManagerSiteGrants",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_DedupeKey",
                table: "NotificationOutbox",
                column: "DedupeKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_LoanEventId",
                table: "NotificationOutbox",
                column: "LoanEventId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_LoanId",
                table: "NotificationOutbox",
                column: "LoanId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationOutbox_Status_NextAttemptAt",
                table: "NotificationOutbox",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_People_DirectoryId",
                table: "People",
                column: "DirectoryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_People_Sid",
                table: "People",
                column: "Sid",
                unique: true,
                filter: "[Sid] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_People_SiteId",
                table: "People",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_People_Username",
                table: "People",
                column: "Username");

            migrationBuilder.CreateIndex(
                name: "IX_Pools_SiteId",
                table: "Pools",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_Pools_Sku_SiteId",
                table: "Pools",
                columns: new[] { "Sku", "SiteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sites_DirectoryValue",
                table: "Sites",
                column: "DirectoryValue",
                unique: true,
                filter: "[DirectoryValue] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Sites_Key",
                table: "Sites",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockEvents_PoolId",
                table: "StockEvents",
                column: "PoolId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRuns_Status_FinishedAt",
                table: "SyncRuns",
                columns: new[] { "Status", "FinishedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "ManagerSiteGrants");

            migrationBuilder.DropTable(
                name: "NotificationOutbox");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "StockEvents");

            migrationBuilder.DropTable(
                name: "SyncRuns");

            migrationBuilder.DropTable(
                name: "LoanEvents");

            migrationBuilder.DropTable(
                name: "Loans");

            migrationBuilder.DropTable(
                name: "IndividualAssets");

            migrationBuilder.DropTable(
                name: "People");

            migrationBuilder.DropTable(
                name: "Pools");

            migrationBuilder.DropTable(
                name: "Sites");
        }
    }
}
