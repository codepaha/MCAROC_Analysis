using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddLitigationCases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LitigationCases",
                columns: table => new
                {
                    LitigationCaseId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    ProviderCaseId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CspId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Cnr = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ProceedingType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    CourtCategory = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Direction = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CaseClassification = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Type = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Court = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Bench = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CaseNumber = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CaseType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CaseYear = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CaseStage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CaseStatus = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Act = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FilingDate = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastHearingDate = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NextHearingDate = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DecisionDate = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    State = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    District = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PetitionersJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RespondentsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PetitionerAdvocatesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RespondentAdvocatesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LitigationCases", x => x.LitigationCaseId);
                    table.ForeignKey(
                        name: "FK_LitigationCases_Requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "Requests",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LitigationReportSnapshots",
                columns: table => new
                {
                    LitigationReportSnapshotId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LitigationSearchJobId = table.Column<long>(type: "bigint", nullable: false),
                    ReportHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ReportFormat = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RawReportBytes = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    RawReportByteLength = table.Column<long>(type: "bigint", nullable: false),
                    RetrievedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    CasesPersistedCount = table.Column<int>(type: "int", nullable: false),
                    LeaseOwner = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LeaseExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LitigationReportSnapshots", x => x.LitigationReportSnapshotId);
                    table.ForeignKey(
                        name: "FK_LitigationReportSnapshots_LitigationSearchJobs_LitigationSearchJobId",
                        column: x => x.LitigationSearchJobId,
                        principalTable: "LitigationSearchJobs",
                        principalColumn: "LitigationSearchJobId");
                });

            migrationBuilder.CreateTable(
                name: "LitigationCaseOrders",
                columns: table => new
                {
                    LitigationCaseOrderId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LitigationCaseId = table.Column<long>(type: "bigint", nullable: false),
                    PdfUrl = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    OrderDate = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    OrderType = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LitigationCaseOrders", x => x.LitigationCaseOrderId);
                    table.ForeignKey(
                        name: "FK_LitigationCaseOrders_LitigationCases_LitigationCaseId",
                        column: x => x.LitigationCaseId,
                        principalTable: "LitigationCases",
                        principalColumn: "LitigationCaseId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LitigationCaseSourceReports",
                columns: table => new
                {
                    LitigationCaseSourceReportId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LitigationCaseId = table.Column<long>(type: "bigint", nullable: false),
                    LitigationReportSnapshotId = table.Column<long>(type: "bigint", nullable: false),
                    ProviderCaseId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CspId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LitigationCaseSourceReports", x => x.LitigationCaseSourceReportId);
                    table.ForeignKey(
                        name: "FK_LitigationCaseSourceReports_LitigationCases_LitigationCaseId",
                        column: x => x.LitigationCaseId,
                        principalTable: "LitigationCases",
                        principalColumn: "LitigationCaseId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LitigationCaseSourceReports_LitigationReportSnapshots_LitigationReportSnapshotId",
                        column: x => x.LitigationReportSnapshotId,
                        principalTable: "LitigationReportSnapshots",
                        principalColumn: "LitigationReportSnapshotId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LitigationCaseOrders_LitigationCaseId_PdfUrl_OrderDate_OrderType",
                table: "LitigationCaseOrders",
                columns: new[] { "LitigationCaseId", "PdfUrl", "OrderDate", "OrderType" },
                unique: true,
                filter: "[PdfUrl] IS NOT NULL AND [OrderDate] IS NOT NULL AND [OrderType] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LitigationCases_RequestId_Cnr_ProceedingType",
                table: "LitigationCases",
                columns: new[] { "RequestId", "Cnr", "ProceedingType" },
                unique: true,
                filter: "[Cnr] IS NOT NULL AND [ProceedingType] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LitigationCaseSourceReports_LitigationCaseId_LitigationReportSnapshotId",
                table: "LitigationCaseSourceReports",
                columns: new[] { "LitigationCaseId", "LitigationReportSnapshotId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LitigationCaseSourceReports_LitigationReportSnapshotId",
                table: "LitigationCaseSourceReports",
                column: "LitigationReportSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_LitigationReportSnapshots_LitigationSearchJobId_ReportHash",
                table: "LitigationReportSnapshots",
                columns: new[] { "LitigationSearchJobId", "ReportHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LitigationCaseOrders");

            migrationBuilder.DropTable(
                name: "LitigationCaseSourceReports");

            migrationBuilder.DropTable(
                name: "LitigationCases");

            migrationBuilder.DropTable(
                name: "LitigationReportSnapshots");
        }
    }
}
