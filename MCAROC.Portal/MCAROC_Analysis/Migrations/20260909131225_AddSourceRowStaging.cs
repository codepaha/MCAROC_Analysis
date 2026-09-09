using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceRowStaging : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SourceRows",
                columns: table => new
                {
                    SourceRowId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: false),
                    WorkbookRole = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    SheetName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    SheetIndex = table.Column<int>(type: "int", nullable: false),
                    RowNumber = table.Column<int>(type: "int", nullable: false),
                    CellsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RowHash = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ExtractedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceRows", x => x.SourceRowId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SourceRows_RequestId_IngestionRunId_WorkbookRole_SheetName",
                table: "SourceRows",
                columns: new[] { "RequestId", "IngestionRunId", "WorkbookRole", "SheetName" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SourceRows");
        }
    }
}
