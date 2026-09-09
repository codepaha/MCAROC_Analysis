using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class SourceRowUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_SourceRows_IngestionRunId_WorkbookRole_SheetIndex_RowNumber",
                table: "SourceRows",
                columns: new[] { "IngestionRunId", "WorkbookRole", "SheetIndex", "RowNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SourceRows_IngestionRunId_WorkbookRole_SheetIndex_RowNumber",
                table: "SourceRows");
        }
    }
}
