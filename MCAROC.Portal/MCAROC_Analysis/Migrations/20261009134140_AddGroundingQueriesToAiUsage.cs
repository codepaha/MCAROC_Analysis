using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddGroundingQueriesToAiUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GroundingQueries",
                table: "AiUsageRecords",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_AiUsageRecords_CreatedUtc",
                table: "AiUsageRecords",
                column: "CreatedUtc",
                filter: "[GroundingQueries] > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AiUsageRecords_CreatedUtc",
                table: "AiUsageRecords");

            migrationBuilder.DropColumn(
                name: "GroundingQueries",
                table: "AiUsageRecords");
        }
    }
}
