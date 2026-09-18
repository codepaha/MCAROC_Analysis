using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class MultiHolderOperationalSlotLeases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // OperationalSlotLeases holds only transient, in-flight state (which upload/unpack currently
            // holds a slot) — nothing worth migrating row-by-row across the schema change, and any lease
            // that happened to be active at deploy time is meaningless once the app restarts anyway (its
            // holder re-acquires on its own next attempt). Drop and recreate rather than ALTER COLUMN the
            // existing columns to NOT NULL in place: EF's generated ALTER COLUMN does not backfill any
            // existing NULL ActiveHolderId/AcquiredUtc/ExpiresUtc/LastHeartbeatUtc values first, so it would
            // fail outright against a database that has ever had a lease acquired and released (a released
            // lease under the old schema is exactly a row with those columns null).
            migrationBuilder.DropTable(name: "OperationalSlotLeases");

            migrationBuilder.CreateTable(
                name: "OperationalSlotLeases",
                columns: table => new
                {
                    LeaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SlotType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ActiveHolderId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    AcquiredUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastHeartbeatUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationalSlotLeases", x => x.LeaseId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OperationalSlotLeases_SlotType_ActiveHolderId",
                table: "OperationalSlotLeases",
                columns: new[] { "SlotType", "ActiveHolderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OperationalSlotLeases_SlotType_ExpiresUtc",
                table: "OperationalSlotLeases",
                columns: new[] { "SlotType", "ExpiresUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "OperationalSlotLeases");

            migrationBuilder.CreateTable(
                name: "OperationalSlotLeases",
                columns: table => new
                {
                    SlotType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ActiveHolderId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AcquiredUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastHeartbeatUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationalSlotLeases", x => x.SlotType);
                });
        }
    }
}
