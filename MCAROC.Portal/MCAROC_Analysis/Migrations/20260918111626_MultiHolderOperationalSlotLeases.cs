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
            // Enforced pre-flight, not just a documented runbook step: this migration is about to DROP
            // OperationalSlotLeases outright (see the remark on that call below), so it must never run
            // while a real upload or unpack is mid-flight and still holding a lease. See
            // OperationalSlotLeaseMigrationGuard's own remarks for why, and README "Deploying the
            // multi-holder slot-lease migration" for the full required sequence.
            migrationBuilder.Sql(OperationalSlotLeaseMigrationGuard.Sql);

            // OperationalSlotLeases holds only transient, in-flight state (which upload/unpack currently
            // holds a slot) — nothing worth migrating row-by-row across the schema change, and (once the
            // guard above confirms nothing is actually active) any lease row still present is already
            // expired and meaningless. Drop and recreate rather than ALTER COLUMN the existing columns to
            // NOT NULL in place: EF's generated ALTER COLUMN does not backfill any existing NULL
            // ActiveHolderId/AcquiredUtc/ExpiresUtc/LastHeartbeatUtc values first, so it would fail outright
            // against a database that has ever had a lease acquired and released (a released lease under
            // the old schema is exactly a row with those columns null).
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
            // Same reasoning as Up's guard, in reverse: rolling back also drops the (now new-shape) table
            // outright, so it must not run while it holds a real active lease either.
            migrationBuilder.Sql(OperationalSlotLeaseMigrationGuard.Sql);

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
