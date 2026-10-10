using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddPreLoginLitigationTargetsAndReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DataVersion",
                table: "PreLoginReportJobs",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "McaDataFetchedUtc",
                table: "PreLoginReportJobs",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PreLoginLitigationTargets",
                columns: table => new
                {
                    PreLoginLitigationTargetId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PreLoginReportJobId = table.Column<long>(type: "bigint", nullable: false),
                    TargetKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    TargetIndex = table.Column<int>(type: "int", nullable: false),
                    TargetName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ExactMatch = table.Column<bool>(type: "bit", nullable: false),
                    KeywordsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    VendorJobId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    IsUserSuppliedVendorJobId = table.Column<bool>(type: "bit", nullable: false),
                    RegistrationAttemptedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RegisteredUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LeaseOwner = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    AttemptVersion = table.Column<int>(type: "int", nullable: false),
                    NextAttemptUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PollingDeadlineUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ProgressPercent = table.Column<int>(type: "int", nullable: false),
                    StatusMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReportFormat = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RawReportBytes = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    RawResponseHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RawReportByteLength = table.Column<long>(type: "bigint", nullable: true),
                    NoLitigationFound = table.Column<bool>(type: "bit", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreLoginLitigationTargets", x => x.PreLoginLitigationTargetId);
                    table.ForeignKey(
                        name: "FK_PreLoginLitigationTargets_PreLoginReportJobs_PreLoginReportJobId",
                        column: x => x.PreLoginReportJobId,
                        principalTable: "PreLoginReportJobs",
                        principalColumn: "PreLoginReportJobId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PreLoginLitigationReconciliationEvents",
                columns: table => new
                {
                    PreLoginLitigationReconciliationEventId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PreLoginLitigationTargetId = table.Column<long>(type: "bigint", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PreviousStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    PreviousRegistrationAttemptedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PreviousVendorJobId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PreviousPollingDeadlineUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SuppliedVendorJobId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    EvidenceNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AttemptVersion = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreLoginLitigationReconciliationEvents", x => x.PreLoginLitigationReconciliationEventId);
                    table.ForeignKey(
                        name: "FK_PreLoginLitigationReconciliationEvents_PreLoginLitigationTargets_PreLoginLitigationTargetId",
                        column: x => x.PreLoginLitigationTargetId,
                        principalTable: "PreLoginLitigationTargets",
                        principalColumn: "PreLoginLitigationTargetId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PreLoginLitigationReconciliationEvents_PreLoginLitigationTargetId_TimestampUtc",
                table: "PreLoginLitigationReconciliationEvents",
                columns: new[] { "PreLoginLitigationTargetId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PreLoginLitigationTargets_PreLoginReportJobId_TargetKind_TargetIndex",
                table: "PreLoginLitigationTargets",
                columns: new[] { "PreLoginReportJobId", "TargetKind", "TargetIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PreLoginLitigationTargets_Status_NextAttemptUtc",
                table: "PreLoginLitigationTargets",
                columns: new[] { "Status", "NextAttemptUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PreLoginLitigationTargets_VendorJobId",
                table: "PreLoginLitigationTargets",
                column: "VendorJobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PreLoginLitigationReconciliationEvents");

            migrationBuilder.DropTable(
                name: "PreLoginLitigationTargets");

            migrationBuilder.DropColumn(
                name: "DataVersion",
                table: "PreLoginReportJobs");

            migrationBuilder.DropColumn(
                name: "McaDataFetchedUtc",
                table: "PreLoginReportJobs");
        }
    }
}
