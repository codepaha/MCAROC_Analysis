using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddShareholdingPattern : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ShareholdingPatternRows",
                columns: table => new
                {
                    ShareholdingPatternRowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    HolderClass = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    AsOnDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Category = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CategoryGroup = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    EquityShares = table.Column<long>(type: "bigint", nullable: true),
                    EquityPercent = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: true),
                    PreferenceShares = table.Column<long>(type: "bigint", nullable: true),
                    PreferencePercent = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: true),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShareholdingPatternRows", x => x.ShareholdingPatternRowId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ShareholdingPatternRows_RequestId_IngestionRunId",
                table: "ShareholdingPatternRows",
                columns: new[] { "RequestId", "IngestionRunId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ShareholdingPatternRows");
        }
    }
}
