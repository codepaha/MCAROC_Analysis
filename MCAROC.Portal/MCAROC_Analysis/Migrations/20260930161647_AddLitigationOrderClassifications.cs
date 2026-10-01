using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddLitigationOrderClassifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LitigationOrderClassifications",
                columns: table => new
                {
                    LitigationOrderClassificationId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LitigationAiAnalysisRunId = table.Column<long>(type: "bigint", nullable: false),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    LitigationCaseId = table.Column<long>(type: "bigint", nullable: false),
                    LitigationCaseOrderId = table.Column<long>(type: "bigint", nullable: false),
                    LitigationOrderDocumentId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    OutcomeTypesJson = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    FineAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: true),
                    Confidence = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    EvidenceTruncated = table.Column<bool>(type: "bit", nullable: false),
                    EvidenceJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EvidenceHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PromptHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RawResponseJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResponseHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ClassificationJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LitigationOrderClassifications", x => x.LitigationOrderClassificationId);
                    table.ForeignKey(
                        name: "FK_LitigationOrderClassifications_LitigationAiAnalysisRuns_LitigationAiAnalysisRunId",
                        column: x => x.LitigationAiAnalysisRunId,
                        principalTable: "LitigationAiAnalysisRuns",
                        principalColumn: "LitigationAiAnalysisRunId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LitigationOrderClassifications_LitigationOrderDocuments_LitigationOrderDocumentId",
                        column: x => x.LitigationOrderDocumentId,
                        principalTable: "LitigationOrderDocuments",
                        principalColumn: "LitigationOrderDocumentId");
                });

            migrationBuilder.CreateIndex(
                name: "IX_LitigationOrderClassifications_LitigationAiAnalysisRunId_LitigationOrderDocumentId",
                table: "LitigationOrderClassifications",
                columns: new[] { "LitigationAiAnalysisRunId", "LitigationOrderDocumentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LitigationOrderClassifications_LitigationOrderDocumentId",
                table: "LitigationOrderClassifications",
                column: "LitigationOrderDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_LitigationOrderClassifications_RequestId_LitigationOrderDocumentId_Status",
                table: "LitigationOrderClassifications",
                columns: new[] { "RequestId", "LitigationOrderDocumentId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LitigationOrderClassifications");
        }
    }
}
