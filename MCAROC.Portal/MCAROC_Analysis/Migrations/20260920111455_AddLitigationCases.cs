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
                name: "LitigationCaseOrders",
                columns: table => new
                {
                    LitigationCaseOrderId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LitigationCaseId = table.Column<long>(type: "bigint", nullable: false),
                    PdfUrl = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OrderDate = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OrderType = table.Column<string>(type: "nvarchar(max)", nullable: true),
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
                    LitigationSearchJobId = table.Column<long>(type: "bigint", nullable: false),
                    ReportHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
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
                        name: "FK_LitigationCaseSourceReports_LitigationSearchJobs_LitigationSearchJobId",
                        column: x => x.LitigationSearchJobId,
                        principalTable: "LitigationSearchJobs",
                        principalColumn: "LitigationSearchJobId");
                });

            migrationBuilder.CreateIndex(
                name: "IX_LitigationCaseOrders_LitigationCaseId",
                table: "LitigationCaseOrders",
                column: "LitigationCaseId");

            migrationBuilder.CreateIndex(
                name: "IX_LitigationCases_RequestId_Cnr",
                table: "LitigationCases",
                columns: new[] { "RequestId", "Cnr" });

            migrationBuilder.CreateIndex(
                name: "IX_LitigationCaseSourceReports_LitigationCaseId_LitigationSearchJobId_ReportHash",
                table: "LitigationCaseSourceReports",
                columns: new[] { "LitigationCaseId", "LitigationSearchJobId", "ReportHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LitigationCaseSourceReports_LitigationSearchJobId",
                table: "LitigationCaseSourceReports",
                column: "LitigationSearchJobId");
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
        }
    }
}
