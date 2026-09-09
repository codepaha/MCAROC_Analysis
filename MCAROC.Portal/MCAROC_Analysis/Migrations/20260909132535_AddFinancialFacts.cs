using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancialFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FinancialFacts",
                columns: table => new
                {
                    FinancialFactId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Basis = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    Section = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    FinancialYear = table.Column<int>(type: "int", nullable: true),
                    RawValue = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NumericValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    YearInferred = table.Column<bool>(type: "bit", nullable: false),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialFacts", x => x.FinancialFactId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialFacts_RequestId_IngestionRunId_Basis",
                table: "FinancialFacts",
                columns: new[] { "RequestId", "IngestionRunId", "Basis" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FinancialFacts");
        }
    }
}
