using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddCumulativeArchiveStatsToBatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CumulativePdfCount",
                table: "McaFilingBatches",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "CumulativeUncompressedBytes",
                table: "McaFilingBatches",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CumulativePdfCount",
                table: "McaFilingBatches");

            migrationBuilder.DropColumn(
                name: "CumulativeUncompressedBytes",
                table: "McaFilingBatches");
        }
    }
}
