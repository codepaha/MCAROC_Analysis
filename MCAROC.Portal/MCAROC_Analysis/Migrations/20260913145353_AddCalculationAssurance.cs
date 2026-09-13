using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddCalculationAssurance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CalculationAssuranceOverrideAudits",
                columns: table => new
                {
                    CalculationAssuranceOverrideAuditId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PreviousMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    NewMode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ChangedByReviewerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ChangedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalculationAssuranceOverrideAudits", x => x.CalculationAssuranceOverrideAuditId);
                });

            migrationBuilder.CreateTable(
                name: "CalculationAuditSnapshots",
                columns: table => new
                {
                    CalculationAuditSnapshotId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    AnalysisRunId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalculationAuditSnapshots", x => x.CalculationAuditSnapshotId);
                });

            migrationBuilder.CreateTable(
                name: "CalculationAiAuditRuns",
                columns: table => new
                {
                    CalculationAiAuditRunId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CalculationAuditSnapshotId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    ModelId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PromptVersion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LedgerEntryCountSent = table.Column<int>(type: "int", nullable: false),
                    RawCandidateCountReturned = table.Column<int>(type: "int", nullable: false),
                    ValidatedCandidateCountAccepted = table.Column<int>(type: "int", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PromptTokenCount = table.Column<int>(type: "int", nullable: true),
                    ResponseTokenCount = table.Column<int>(type: "int", nullable: true),
                    RawResponseJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResponseHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: true),
                    RejectedCandidatesJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalculationAiAuditRuns", x => x.CalculationAiAuditRunId);
                    table.UniqueConstraint("AK_CalculationAiAuditRuns_CalculationAiAuditRunId_CalculationAuditSnapshotId", x => new { x.CalculationAiAuditRunId, x.CalculationAuditSnapshotId });
                    table.ForeignKey(
                        name: "FK_CalculationAiAuditRuns_CalculationAuditSnapshots_CalculationAuditSnapshotId",
                        column: x => x.CalculationAuditSnapshotId,
                        principalTable: "CalculationAuditSnapshots",
                        principalColumn: "CalculationAuditSnapshotId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CalculationCheckResults",
                columns: table => new
                {
                    CalculationCheckResultId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CalculationAuditSnapshotId = table.Column<long>(type: "bigint", nullable: false),
                    CheckKey = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    CheckVersion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    DetailJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NotEvaluatedReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RanUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalculationCheckResults", x => x.CalculationCheckResultId);
                    table.UniqueConstraint("AK_CalculationCheckResults_CalculationCheckResultId_CalculationAuditSnapshotId", x => new { x.CalculationCheckResultId, x.CalculationAuditSnapshotId });
                    table.ForeignKey(
                        name: "FK_CalculationCheckResults_CalculationAuditSnapshots_CalculationAuditSnapshotId",
                        column: x => x.CalculationAuditSnapshotId,
                        principalTable: "CalculationAuditSnapshots",
                        principalColumn: "CalculationAuditSnapshotId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CalculationLedgerEntries",
                columns: table => new
                {
                    CalculationLedgerEntryId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CalculationAuditSnapshotId = table.Column<long>(type: "bigint", nullable: false),
                    CalculationKey = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    CalcVersion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    MetricLabel = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Period = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Unit = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ValueNumeric = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    ValueText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    InsufficiencyReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    InputsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    InputHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    OutputHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ToleranceAbsolute = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    TolerancePercent = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: true),
                    SourceRowRefsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    HasUnresolvedProvenance = table.Column<bool>(type: "bit", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalculationLedgerEntries", x => x.CalculationLedgerEntryId);
                    table.UniqueConstraint("AK_CalculationLedgerEntries_CalculationLedgerEntryId_CalculationAuditSnapshotId", x => new { x.CalculationLedgerEntryId, x.CalculationAuditSnapshotId });
                    table.ForeignKey(
                        name: "FK_CalculationLedgerEntries_CalculationAuditSnapshots_CalculationAuditSnapshotId",
                        column: x => x.CalculationAuditSnapshotId,
                        principalTable: "CalculationAuditSnapshots",
                        principalColumn: "CalculationAuditSnapshotId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CalculationCheckResultLedgerLinks",
                columns: table => new
                {
                    CalculationCheckResultLedgerLinkId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CalculationAuditSnapshotId = table.Column<long>(type: "bigint", nullable: false),
                    CalculationCheckResultId = table.Column<long>(type: "bigint", nullable: false),
                    CalculationLedgerEntryId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalculationCheckResultLedgerLinks", x => x.CalculationCheckResultLedgerLinkId);
                    table.ForeignKey(
                        name: "FK_CalculationCheckResultLedgerLinks_CalculationCheckResults_CalculationCheckResultId_CalculationAuditSnapshotId",
                        columns: x => new { x.CalculationCheckResultId, x.CalculationAuditSnapshotId },
                        principalTable: "CalculationCheckResults",
                        principalColumns: new[] { "CalculationCheckResultId", "CalculationAuditSnapshotId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CalculationCheckResultLedgerLinks_CalculationLedgerEntries_CalculationLedgerEntryId_CalculationAuditSnapshotId",
                        columns: x => new { x.CalculationLedgerEntryId, x.CalculationAuditSnapshotId },
                        principalTable: "CalculationLedgerEntries",
                        principalColumns: new[] { "CalculationLedgerEntryId", "CalculationAuditSnapshotId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CalculationDiscrepancies",
                columns: table => new
                {
                    CalculationDiscrepancyId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CalculationAuditSnapshotId = table.Column<long>(type: "bigint", nullable: false),
                    Variant = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    SourceType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    OriginCheckKey = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    AiAuditRunId = table.Column<long>(type: "bigint", nullable: true),
                    PrimaryLedgerEntryId = table.Column<long>(type: "bigint", nullable: false),
                    ClaimSummary = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ClaimedExpectedValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    ClaimedActualValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    RequiredApprovals = table.Column<int>(type: "int", nullable: false),
                    ExceptionReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastUpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalculationDiscrepancies", x => x.CalculationDiscrepancyId);
                    table.UniqueConstraint("AK_CalculationDiscrepancies_CalculationDiscrepancyId_CalculationAuditSnapshotId", x => new { x.CalculationDiscrepancyId, x.CalculationAuditSnapshotId });
                    table.ForeignKey(
                        name: "FK_CalculationDiscrepancies_CalculationAiAuditRuns_AiAuditRunId_CalculationAuditSnapshotId",
                        columns: x => new { x.AiAuditRunId, x.CalculationAuditSnapshotId },
                        principalTable: "CalculationAiAuditRuns",
                        principalColumns: new[] { "CalculationAiAuditRunId", "CalculationAuditSnapshotId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CalculationDiscrepancies_CalculationAuditSnapshots_CalculationAuditSnapshotId",
                        column: x => x.CalculationAuditSnapshotId,
                        principalTable: "CalculationAuditSnapshots",
                        principalColumn: "CalculationAuditSnapshotId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CalculationDiscrepancies_CalculationLedgerEntries_PrimaryLedgerEntryId_CalculationAuditSnapshotId",
                        columns: x => new { x.PrimaryLedgerEntryId, x.CalculationAuditSnapshotId },
                        principalTable: "CalculationLedgerEntries",
                        principalColumns: new[] { "CalculationLedgerEntryId", "CalculationAuditSnapshotId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CalculationArtifactHolds",
                columns: table => new
                {
                    CalculationArtifactHoldId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CalculationAuditSnapshotId = table.Column<long>(type: "bigint", nullable: false),
                    Variant = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    HoldReason = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SourceDiscrepancyId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReleasedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReleasedByReviewerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReleaseNote = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalculationArtifactHolds", x => x.CalculationArtifactHoldId);
                    table.ForeignKey(
                        name: "FK_CalculationArtifactHolds_CalculationAuditSnapshots_CalculationAuditSnapshotId",
                        column: x => x.CalculationAuditSnapshotId,
                        principalTable: "CalculationAuditSnapshots",
                        principalColumn: "CalculationAuditSnapshotId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CalculationArtifactHolds_CalculationDiscrepancies_SourceDiscrepancyId_CalculationAuditSnapshotId",
                        columns: x => new { x.SourceDiscrepancyId, x.CalculationAuditSnapshotId },
                        principalTable: "CalculationDiscrepancies",
                        principalColumns: new[] { "CalculationDiscrepancyId", "CalculationAuditSnapshotId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CalculationDiscrepancyApprovals",
                columns: table => new
                {
                    CalculationDiscrepancyApprovalId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CalculationDiscrepancyId = table.Column<long>(type: "bigint", nullable: false),
                    ApprovalSequence = table.Column<int>(type: "int", nullable: false),
                    DecisionAction = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ReviewerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ReviewerNotes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DecidedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DeterministicReproductionResultJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModelIdUsed = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PromptVersionUsed = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalculationDiscrepancyApprovals", x => x.CalculationDiscrepancyApprovalId);
                    table.ForeignKey(
                        name: "FK_CalculationDiscrepancyApprovals_CalculationDiscrepancies_CalculationDiscrepancyId",
                        column: x => x.CalculationDiscrepancyId,
                        principalTable: "CalculationDiscrepancies",
                        principalColumn: "CalculationDiscrepancyId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CalculationDiscrepancyLedgerLinks",
                columns: table => new
                {
                    CalculationDiscrepancyLedgerLinkId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CalculationAuditSnapshotId = table.Column<long>(type: "bigint", nullable: false),
                    CalculationDiscrepancyId = table.Column<long>(type: "bigint", nullable: false),
                    CalculationLedgerEntryId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalculationDiscrepancyLedgerLinks", x => x.CalculationDiscrepancyLedgerLinkId);
                    table.ForeignKey(
                        name: "FK_CalculationDiscrepancyLedgerLinks_CalculationDiscrepancies_CalculationDiscrepancyId_CalculationAuditSnapshotId",
                        columns: x => new { x.CalculationDiscrepancyId, x.CalculationAuditSnapshotId },
                        principalTable: "CalculationDiscrepancies",
                        principalColumns: new[] { "CalculationDiscrepancyId", "CalculationAuditSnapshotId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CalculationDiscrepancyLedgerLinks_CalculationLedgerEntries_CalculationLedgerEntryId_CalculationAuditSnapshotId",
                        columns: x => new { x.CalculationLedgerEntryId, x.CalculationAuditSnapshotId },
                        principalTable: "CalculationLedgerEntries",
                        principalColumns: new[] { "CalculationLedgerEntryId", "CalculationAuditSnapshotId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalculationAiAuditRuns_CalculationAuditSnapshotId",
                table: "CalculationAiAuditRuns",
                column: "CalculationAuditSnapshotId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalculationArtifactHolds_CalculationAuditSnapshotId_Variant",
                table: "CalculationArtifactHolds",
                columns: new[] { "CalculationAuditSnapshotId", "Variant" });

            migrationBuilder.CreateIndex(
                name: "IX_CalculationArtifactHolds_IsActive",
                table: "CalculationArtifactHolds",
                column: "IsActive",
                filter: "[IsActive] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_CalculationArtifactHolds_SourceDiscrepancyId_CalculationAuditSnapshotId",
                table: "CalculationArtifactHolds",
                columns: new[] { "SourceDiscrepancyId", "CalculationAuditSnapshotId" });

            migrationBuilder.CreateIndex(
                name: "IX_CalculationAuditSnapshots_RequestId_IngestionRunId_AnalysisRunId",
                table: "CalculationAuditSnapshots",
                columns: new[] { "RequestId", "IngestionRunId", "AnalysisRunId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalculationCheckResultLedgerLinks_CalculationCheckResultId",
                table: "CalculationCheckResultLedgerLinks",
                column: "CalculationCheckResultId");

            migrationBuilder.CreateIndex(
                name: "IX_CalculationCheckResultLedgerLinks_CalculationCheckResultId_CalculationAuditSnapshotId",
                table: "CalculationCheckResultLedgerLinks",
                columns: new[] { "CalculationCheckResultId", "CalculationAuditSnapshotId" });

            migrationBuilder.CreateIndex(
                name: "IX_CalculationCheckResultLedgerLinks_CalculationLedgerEntryId",
                table: "CalculationCheckResultLedgerLinks",
                column: "CalculationLedgerEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_CalculationCheckResultLedgerLinks_CalculationLedgerEntryId_CalculationAuditSnapshotId",
                table: "CalculationCheckResultLedgerLinks",
                columns: new[] { "CalculationLedgerEntryId", "CalculationAuditSnapshotId" });

            migrationBuilder.CreateIndex(
                name: "IX_CalculationCheckResults_CalculationAuditSnapshotId_CheckKey",
                table: "CalculationCheckResults",
                columns: new[] { "CalculationAuditSnapshotId", "CheckKey" });

            migrationBuilder.CreateIndex(
                name: "IX_CalculationDiscrepancies_AiAuditRunId_CalculationAuditSnapshotId",
                table: "CalculationDiscrepancies",
                columns: new[] { "AiAuditRunId", "CalculationAuditSnapshotId" });

            migrationBuilder.CreateIndex(
                name: "IX_CalculationDiscrepancies_CalculationAuditSnapshotId",
                table: "CalculationDiscrepancies",
                column: "CalculationAuditSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_CalculationDiscrepancies_PrimaryLedgerEntryId",
                table: "CalculationDiscrepancies",
                column: "PrimaryLedgerEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_CalculationDiscrepancies_PrimaryLedgerEntryId_CalculationAuditSnapshotId",
                table: "CalculationDiscrepancies",
                columns: new[] { "PrimaryLedgerEntryId", "CalculationAuditSnapshotId" });

            migrationBuilder.CreateIndex(
                name: "IX_CalculationDiscrepancyApprovals_CalculationDiscrepancyId",
                table: "CalculationDiscrepancyApprovals",
                column: "CalculationDiscrepancyId");

            migrationBuilder.CreateIndex(
                name: "IX_CalculationDiscrepancyLedgerLinks_CalculationDiscrepancyId",
                table: "CalculationDiscrepancyLedgerLinks",
                column: "CalculationDiscrepancyId");

            migrationBuilder.CreateIndex(
                name: "IX_CalculationDiscrepancyLedgerLinks_CalculationDiscrepancyId_CalculationAuditSnapshotId",
                table: "CalculationDiscrepancyLedgerLinks",
                columns: new[] { "CalculationDiscrepancyId", "CalculationAuditSnapshotId" });

            migrationBuilder.CreateIndex(
                name: "IX_CalculationDiscrepancyLedgerLinks_CalculationLedgerEntryId",
                table: "CalculationDiscrepancyLedgerLinks",
                column: "CalculationLedgerEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_CalculationDiscrepancyLedgerLinks_CalculationLedgerEntryId_CalculationAuditSnapshotId",
                table: "CalculationDiscrepancyLedgerLinks",
                columns: new[] { "CalculationLedgerEntryId", "CalculationAuditSnapshotId" });

            migrationBuilder.CreateIndex(
                name: "IX_CalculationLedgerEntries_CalculationAuditSnapshotId_CalculationKey_Period",
                table: "CalculationLedgerEntries",
                columns: new[] { "CalculationAuditSnapshotId", "CalculationKey", "Period" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalculationArtifactHolds");

            migrationBuilder.DropTable(
                name: "CalculationAssuranceOverrideAudits");

            migrationBuilder.DropTable(
                name: "CalculationCheckResultLedgerLinks");

            migrationBuilder.DropTable(
                name: "CalculationDiscrepancyApprovals");

            migrationBuilder.DropTable(
                name: "CalculationDiscrepancyLedgerLinks");

            migrationBuilder.DropTable(
                name: "CalculationCheckResults");

            migrationBuilder.DropTable(
                name: "CalculationDiscrepancies");

            migrationBuilder.DropTable(
                name: "CalculationAiAuditRuns");

            migrationBuilder.DropTable(
                name: "CalculationLedgerEntries");

            migrationBuilder.DropTable(
                name: "CalculationAuditSnapshots");
        }
    }
}
