using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddLitigationAiAnalysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LitigationAiAnalysisRuns",
                columns: table => new
                {
                    LitigationAiAnalysisRunId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    RunNumber = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    ModelId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PromptVersion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    LeaseOwner = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LeaseExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NextAttemptUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LitigationAiAnalysisRuns", x => x.LitigationAiAnalysisRunId);
                    table.ForeignKey(
                        name: "FK_LitigationAiAnalysisRuns_Requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "Requests",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LitigationCaseAiAnalyses",
                columns: table => new
                {
                    LitigationCaseAiAnalysisId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LitigationAiAnalysisRunId = table.Column<long>(type: "bigint", nullable: false),
                    LitigationCaseId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    EvidenceJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EvidenceHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PromptHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RawResponseJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResponseHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AnalysisJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PromptTokenCount = table.Column<int>(type: "int", nullable: true),
                    ResponseTokenCount = table.Column<int>(type: "int", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LitigationCaseAiAnalyses", x => x.LitigationCaseAiAnalysisId);
                    table.ForeignKey(
                        name: "FK_LitigationCaseAiAnalyses_LitigationAiAnalysisRuns_LitigationAiAnalysisRunId",
                        column: x => x.LitigationAiAnalysisRunId,
                        principalTable: "LitigationAiAnalysisRuns",
                        principalColumn: "LitigationAiAnalysisRunId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LitigationCaseAiAnalyses_LitigationCases_LitigationCaseId",
                        column: x => x.LitigationCaseId,
                        principalTable: "LitigationCases",
                        principalColumn: "LitigationCaseId");
                });

            migrationBuilder.CreateTable(
                name: "LitigationPortfolioAiAnalyses",
                columns: table => new
                {
                    LitigationPortfolioAiAnalysisId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LitigationAiAnalysisRunId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    EvidenceJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EvidenceHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PromptHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RawResponseJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResponseHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AnalysisJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PromptTokenCount = table.Column<int>(type: "int", nullable: true),
                    ResponseTokenCount = table.Column<int>(type: "int", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LitigationPortfolioAiAnalyses", x => x.LitigationPortfolioAiAnalysisId);
                    table.ForeignKey(
                        name: "FK_LitigationPortfolioAiAnalyses_LitigationAiAnalysisRuns_LitigationAiAnalysisRunId",
                        column: x => x.LitigationAiAnalysisRunId,
                        principalTable: "LitigationAiAnalysisRuns",
                        principalColumn: "LitigationAiAnalysisRunId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LitigationAiAnalysisRuns_RequestId_RunNumber",
                table: "LitigationAiAnalysisRuns",
                columns: new[] { "RequestId", "RunNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LitigationAiAnalysisRuns_Status_NextAttemptUtc",
                table: "LitigationAiAnalysisRuns",
                columns: new[] { "Status", "NextAttemptUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LitigationCaseAiAnalyses_LitigationAiAnalysisRunId_LitigationCaseId",
                table: "LitigationCaseAiAnalyses",
                columns: new[] { "LitigationAiAnalysisRunId", "LitigationCaseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LitigationCaseAiAnalyses_LitigationCaseId",
                table: "LitigationCaseAiAnalyses",
                column: "LitigationCaseId");

            migrationBuilder.CreateIndex(
                name: "IX_LitigationPortfolioAiAnalyses_LitigationAiAnalysisRunId",
                table: "LitigationPortfolioAiAnalyses",
                column: "LitigationAiAnalysisRunId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LitigationCaseAiAnalyses");

            migrationBuilder.DropTable(
                name: "LitigationPortfolioAiAnalyses");

            migrationBuilder.DropTable(
                name: "LitigationAiAnalysisRuns");
        }
    }
}
