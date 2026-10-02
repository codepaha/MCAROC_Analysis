using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddMcaRefreshReuseSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ReusedFromExtractionId",
                table: "McaFilingExtractions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReusedFromDocumentId",
                table: "McaFilingDocuments",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_McaFilingDocuments_RequestId_FileHash",
                table: "McaFilingDocuments",
                columns: new[] { "RequestId", "FileHash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_McaFilingDocuments_RequestId_FileHash",
                table: "McaFilingDocuments");

            migrationBuilder.DropColumn(
                name: "ReusedFromExtractionId",
                table: "McaFilingExtractions");

            migrationBuilder.DropColumn(
                name: "ReusedFromDocumentId",
                table: "McaFilingDocuments");
        }
    }
}
