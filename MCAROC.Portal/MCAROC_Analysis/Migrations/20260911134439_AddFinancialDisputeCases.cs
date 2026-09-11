using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancialDisputeCases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FinancialDisputeCases",
                columns: table => new
                {
                    FinancialDisputeCaseId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Direction = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DisputeType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Currency = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AmountUnderDefault = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Verdict = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Court = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Litigants = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CaseNumber = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DateOfDefault = table.Column<DateOnly>(type: "date", nullable: true),
                    DateOfJudgement = table.Column<DateOnly>(type: "date", nullable: true),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialDisputeCases", x => x.FinancialDisputeCaseId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FinancialDisputeCases");
        }
    }
}
