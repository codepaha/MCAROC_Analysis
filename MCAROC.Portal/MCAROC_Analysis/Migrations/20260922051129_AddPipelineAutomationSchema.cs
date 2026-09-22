using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddPipelineAutomationSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "OriginSnapshotId",
                table: "LitigationAiAnalysisRuns",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Trigger",
                table: "LitigationAiAnalysisRuns",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "TriggerSnapshotId",
                table: "LitigationAiAnalysisRuns",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CompanyReportLifecycles",
                columns: table => new
                {
                    CompanyReportLifecycleId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Identifier = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    State = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    UnlockedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastRefreshRequestedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastRefreshCompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ActiveRefreshId = table.Column<long>(type: "bigint", nullable: true),
                    RefreshDeadlineUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyReportLifecycles", x => x.CompanyReportLifecycleId);
                });

            migrationBuilder.CreateTable(
                name: "IntegrationHealths",
                columns: table => new
                {
                    IntegrationHealthId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    State = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    LastSuccessUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    OpenedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastTransitionUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    NextProbeUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationHealths", x => x.IntegrationHealthId);
                });

            migrationBuilder.CreateTable(
                name: "PaidCallAdmissions",
                columns: table => new
                {
                    PaidCallAdmissionId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ScopeKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    DayKey = table.Column<DateOnly>(type: "date", nullable: false),
                    Trigger = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    ClientId = table.Column<long>(type: "bigint", nullable: true),
                    State = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ReferenceId = table.Column<long>(type: "bigint", nullable: true),
                    ReservedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ResolvedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaidCallAdmissions", x => x.PaidCallAdmissionId);
                    table.ForeignKey(
                        name: "FK_PaidCallAdmissions_Requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "Requests",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PipelineRuns",
                columns: table => new
                {
                    PipelineRunId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    Trigger = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PolicyJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Outcome = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CoreReadyUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReconcileLeaseOwner = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ReconcileLeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReconcileLeaseExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineRuns", x => x.PipelineRunId);
                    table.ForeignKey(
                        name: "FK_PipelineRuns_Requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "Requests",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SpendCounters",
                columns: table => new
                {
                    SpendCounterId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    DayKey = table.Column<DateOnly>(type: "date", nullable: false),
                    Used = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpendCounters", x => x.SpendCounterId);
                });

            migrationBuilder.CreateTable(
                name: "SpendScopes",
                columns: table => new
                {
                    SpendScopeId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ScopeKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    ActiveAdmissionId = table.Column<long>(type: "bigint", nullable: true),
                    LastCommittedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpendScopes", x => x.SpendScopeId);
                });

            migrationBuilder.CreateTable(
                name: "UnlockApprovals",
                columns: table => new
                {
                    UnlockApprovalId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Identifier = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    ApprovedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ApprovedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConsumedAdmissionId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnlockApprovals", x => x.UnlockApprovalId);
                });

            migrationBuilder.CreateTable(
                name: "PipelineEvents",
                columns: table => new
                {
                    PipelineEventId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PipelineRunId = table.Column<long>(type: "bigint", nullable: false),
                    Stage = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    Actor = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ReasonCode = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineEvents", x => x.PipelineEventId);
                    table.ForeignKey(
                        name: "FK_PipelineEvents_PipelineRuns_PipelineRunId",
                        column: x => x.PipelineRunId,
                        principalTable: "PipelineRuns",
                        principalColumn: "PipelineRunId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PipelineStageStates",
                columns: table => new
                {
                    PipelineRunId = table.Column<long>(type: "bigint", nullable: false),
                    Stage = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    State = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    SkipKind = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    SourceRef = table.Column<long>(type: "bigint", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReasonCode = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    ReasonDetail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastHeartbeatUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PipelineStageStates", x => new { x.PipelineRunId, x.Stage });
                    table.ForeignKey(
                        name: "FK_PipelineStageStates_PipelineRuns_PipelineRunId",
                        column: x => x.PipelineRunId,
                        principalTable: "PipelineRuns",
                        principalColumn: "PipelineRunId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LitigationAiAnalysisRuns_OriginSnapshotId",
                table: "LitigationAiAnalysisRuns",
                column: "OriginSnapshotId",
                unique: true,
                filter: "[Trigger] = 'Auto'");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyReportLifecycles_Identifier",
                table: "CompanyReportLifecycles",
                column: "Identifier",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationHealths_Name",
                table: "IntegrationHealths",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaidCallAdmissions_Kind_ScopeKey_State",
                table: "PaidCallAdmissions",
                columns: new[] { "Kind", "ScopeKey", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_PaidCallAdmissions_RequestId",
                table: "PaidCallAdmissions",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineEvents_PipelineRunId_AtUtc",
                table: "PipelineEvents",
                columns: new[] { "PipelineRunId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRuns_ReconcileLeaseExpiresUtc_Outcome",
                table: "PipelineRuns",
                columns: new[] { "ReconcileLeaseExpiresUtc", "Outcome" });

            migrationBuilder.CreateIndex(
                name: "IX_PipelineRuns_RequestId",
                table: "PipelineRuns",
                column: "RequestId",
                unique: true,
                filter: "[Outcome] IN ('InProgress','CoreReady','NeedsAttention')");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineStageStates_State_NextAttemptUtc",
                table: "PipelineStageStates",
                columns: new[] { "State", "NextAttemptUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SpendCounters_Kind_DayKey",
                table: "SpendCounters",
                columns: new[] { "Kind", "DayKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpendScopes_Kind_ScopeKey",
                table: "SpendScopes",
                columns: new[] { "Kind", "ScopeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UnlockApprovals_Identifier_ConsumedAdmissionId_ExpiresUtc",
                table: "UnlockApprovals",
                columns: new[] { "Identifier", "ConsumedAdmissionId", "ExpiresUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompanyReportLifecycles");

            migrationBuilder.DropTable(
                name: "IntegrationHealths");

            migrationBuilder.DropTable(
                name: "PaidCallAdmissions");

            migrationBuilder.DropTable(
                name: "PipelineEvents");

            migrationBuilder.DropTable(
                name: "PipelineStageStates");

            migrationBuilder.DropTable(
                name: "SpendCounters");

            migrationBuilder.DropTable(
                name: "SpendScopes");

            migrationBuilder.DropTable(
                name: "UnlockApprovals");

            migrationBuilder.DropTable(
                name: "PipelineRuns");

            migrationBuilder.DropIndex(
                name: "IX_LitigationAiAnalysisRuns_OriginSnapshotId",
                table: "LitigationAiAnalysisRuns");

            migrationBuilder.DropColumn(
                name: "OriginSnapshotId",
                table: "LitigationAiAnalysisRuns");

            migrationBuilder.DropColumn(
                name: "Trigger",
                table: "LitigationAiAnalysisRuns");

            migrationBuilder.DropColumn(
                name: "TriggerSnapshotId",
                table: "LitigationAiAnalysisRuns");
        }
    }
}
