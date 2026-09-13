using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditorDetailTableColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DirectorsComments",
                table: "AuditorObservations",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Footnotes",
                table: "AuditorObservations",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SectionCode",
                table: "AuditorObservations",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SectionName",
                table: "AuditorObservations",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SerialNumber",
                table: "AuditorObservations",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DirectorsComments",
                table: "AuditorObservations");

            migrationBuilder.DropColumn(
                name: "Footnotes",
                table: "AuditorObservations");

            migrationBuilder.DropColumn(
                name: "SectionCode",
                table: "AuditorObservations");

            migrationBuilder.DropColumn(
                name: "SectionName",
                table: "AuditorObservations");

            migrationBuilder.DropColumn(
                name: "SerialNumber",
                table: "AuditorObservations");
        }
    }
}
