using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class FixMcaFilingsRecoveryAndConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_McaFilingExtractions_FilingId",
                table: "McaFilingExtractions");

            migrationBuilder.CreateIndex(
                name: "IX_McaFilingExtractions_FilingId",
                table: "McaFilingExtractions",
                column: "FilingId",
                unique: true,
                filter: "[FilingId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_McaFilingExtractions_FilingId",
                table: "McaFilingExtractions");

            migrationBuilder.CreateIndex(
                name: "IX_McaFilingExtractions_FilingId",
                table: "McaFilingExtractions",
                column: "FilingId");
        }
    }
}
