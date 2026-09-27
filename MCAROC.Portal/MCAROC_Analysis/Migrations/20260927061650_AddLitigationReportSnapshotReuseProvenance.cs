using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddLitigationReportSnapshotReuseProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "OriginSnapshotId",
                table: "LitigationReportSnapshots",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RequestId",
                table: "LitigationReportSnapshots",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ReusedFromRequestId",
                table: "LitigationReportSnapshots",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReusedFromSnapshotId",
                table: "LitigationReportSnapshots",
                type: "bigint",
                nullable: true);

            // Backfill RequestId before anything reads it: every pre-existing row's AddColumn default (0)
            // above is wrong (no request has id 0), so this must run before that value is ever queried.
            // One job per request, never ambiguous — see LitigationSearchJob's own remarks. OriginSnapshotId
            // needs no backfill: null already means "this row is its own lineage root" (see the entity).
            migrationBuilder.Sql(@"
UPDATE s
SET s.RequestId = j.RequestId
FROM LitigationReportSnapshots s
JOIN LitigationSearchJobs j ON j.LitigationSearchJobId = s.LitigationSearchJobId;");

            migrationBuilder.CreateIndex(
                name: "IX_LitigationReportSnapshots_OriginSnapshotId",
                table: "LitigationReportSnapshots",
                column: "OriginSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_LitigationReportSnapshots_RequestId_OriginSnapshotId",
                table: "LitigationReportSnapshots",
                columns: new[] { "RequestId", "OriginSnapshotId" },
                unique: true,
                filter: "[OriginSnapshotId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LitigationReportSnapshots_ReusedFromRequestId",
                table: "LitigationReportSnapshots",
                column: "ReusedFromRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_LitigationReportSnapshots_ReusedFromSnapshotId",
                table: "LitigationReportSnapshots",
                column: "ReusedFromSnapshotId");

            migrationBuilder.AddForeignKey(
                name: "FK_LitigationReportSnapshots_LitigationReportSnapshots_OriginSnapshotId",
                table: "LitigationReportSnapshots",
                column: "OriginSnapshotId",
                principalTable: "LitigationReportSnapshots",
                principalColumn: "LitigationReportSnapshotId");

            migrationBuilder.AddForeignKey(
                name: "FK_LitigationReportSnapshots_LitigationReportSnapshots_ReusedFromSnapshotId",
                table: "LitigationReportSnapshots",
                column: "ReusedFromSnapshotId",
                principalTable: "LitigationReportSnapshots",
                principalColumn: "LitigationReportSnapshotId");

            migrationBuilder.AddForeignKey(
                name: "FK_LitigationReportSnapshots_Requests_ReusedFromRequestId",
                table: "LitigationReportSnapshots",
                column: "ReusedFromRequestId",
                principalTable: "Requests",
                principalColumn: "RequestId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LitigationReportSnapshots_LitigationReportSnapshots_OriginSnapshotId",
                table: "LitigationReportSnapshots");

            migrationBuilder.DropForeignKey(
                name: "FK_LitigationReportSnapshots_LitigationReportSnapshots_ReusedFromSnapshotId",
                table: "LitigationReportSnapshots");

            migrationBuilder.DropForeignKey(
                name: "FK_LitigationReportSnapshots_Requests_ReusedFromRequestId",
                table: "LitigationReportSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_LitigationReportSnapshots_OriginSnapshotId",
                table: "LitigationReportSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_LitigationReportSnapshots_RequestId_OriginSnapshotId",
                table: "LitigationReportSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_LitigationReportSnapshots_ReusedFromRequestId",
                table: "LitigationReportSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_LitigationReportSnapshots_ReusedFromSnapshotId",
                table: "LitigationReportSnapshots");

            migrationBuilder.DropColumn(
                name: "OriginSnapshotId",
                table: "LitigationReportSnapshots");

            migrationBuilder.DropColumn(
                name: "RequestId",
                table: "LitigationReportSnapshots");

            migrationBuilder.DropColumn(
                name: "ReusedFromRequestId",
                table: "LitigationReportSnapshots");

            migrationBuilder.DropColumn(
                name: "ReusedFromSnapshotId",
                table: "LitigationReportSnapshots");
        }
    }
}
