using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddNameHistoryAndPba : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CompanyNameHistories",
                columns: table => new
                {
                    CompanyNameHistoryId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PreviousName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    TillDate = table.Column<DateOnly>(type: "date", nullable: true),
                    TillDateRaw = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyNameHistories", x => x.CompanyNameHistoryId);
                });

            migrationBuilder.CreateTable(
                name: "PrincipalBusinessActivities",
                columns: table => new
                {
                    PrincipalBusinessActivityId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AsOnDate = table.Column<DateOnly>(type: "date", nullable: true),
                    MainActivityGroupCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    MainActivityGroupDescription = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BusinessActivityCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    BusinessActivityDescription = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TurnoverPercent = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: true),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrincipalBusinessActivities", x => x.PrincipalBusinessActivityId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyNameHistories_RequestId_IngestionRunId",
                table: "CompanyNameHistories",
                columns: new[] { "RequestId", "IngestionRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_PrincipalBusinessActivities_RequestId_IngestionRunId",
                table: "PrincipalBusinessActivities",
                columns: new[] { "RequestId", "IngestionRunId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompanyNameHistories");

            migrationBuilder.DropTable(
                name: "PrincipalBusinessActivities");
        }
    }
}
