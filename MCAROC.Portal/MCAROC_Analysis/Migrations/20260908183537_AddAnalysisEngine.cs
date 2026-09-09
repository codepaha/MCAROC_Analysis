using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalysisEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnalysisRuns",
                columns: table => new
                {
                    AnalysisRunId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    RunNumber = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    RuleEngineVersion = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PromptVersion = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StartedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OverallReviewPriority = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    CriticalFindingsCount = table.Column<int>(type: "int", nullable: false),
                    ReviewFindingsCount = table.Column<int>(type: "int", nullable: false),
                    WatchFindingsCount = table.Column<int>(type: "int", nullable: false),
                    PositiveFindingsCount = table.Column<int>(type: "int", nullable: false),
                    ExecutiveSummaryJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DataSufficiencyNotesJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalysisRuns", x => x.AnalysisRunId);
                    table.ForeignKey(
                        name: "FK_AnalysisRuns_Requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "Requests",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AnalysisFindings",
                columns: table => new
                {
                    FindingId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AnalysisRunId = table.Column<long>(type: "bigint", nullable: false),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    Section = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    TemporalStatus = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Code = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SummaryText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MetricsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SupportingSignalsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    WhyThisMatters = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RecommendedReview = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceReferenceJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PeriodLabel = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ObservationDate = table.Column<DateOnly>(type: "date", nullable: true),
                    DisplayPriority = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnalysisFindings", x => x.FindingId);
                    table.ForeignKey(
                        name: "FK_AnalysisFindings_AnalysisRuns_AnalysisRunId",
                        column: x => x.AnalysisRunId,
                        principalTable: "AnalysisRuns",
                        principalColumn: "AnalysisRunId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisFindings_AnalysisRunId_Section",
                table: "AnalysisFindings",
                columns: new[] { "AnalysisRunId", "Section" });

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisFindings_RequestId_Code",
                table: "AnalysisFindings",
                columns: new[] { "RequestId", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_AnalysisRuns_RequestId_RunNumber",
                table: "AnalysisRuns",
                columns: new[] { "RequestId", "RunNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnalysisFindings");

            migrationBuilder.DropTable(
                name: "AnalysisRuns");
        }
    }
}
