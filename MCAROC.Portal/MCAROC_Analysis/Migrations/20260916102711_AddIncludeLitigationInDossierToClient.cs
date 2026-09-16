using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddIncludeLitigationInDossierToClient : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // EF's scaffolder defaults new bool columns to CLR default(bool) = false, ignoring the entity's
            // own `= true` property initializer (that initializer only affects newly `new Client()`-ed C#
            // objects, never the SQL column default) — explicitly overridden to true here so every existing
            // client row (not just the 4 HasData-seeded ones patched below) keeps today's behavior.
            migrationBuilder.AddColumn<bool>(
                name: "IncludeLitigationInDossier",
                table: "Clients",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.UpdateData(
                table: "Clients",
                keyColumn: "ClientId",
                keyValue: 1L,
                column: "IncludeLitigationInDossier",
                value: true);

            migrationBuilder.UpdateData(
                table: "Clients",
                keyColumn: "ClientId",
                keyValue: 2L,
                column: "IncludeLitigationInDossier",
                value: true);

            migrationBuilder.UpdateData(
                table: "Clients",
                keyColumn: "ClientId",
                keyValue: 3L,
                column: "IncludeLitigationInDossier",
                value: true);

            migrationBuilder.UpdateData(
                table: "Clients",
                keyColumn: "ClientId",
                keyValue: 4L,
                column: "IncludeLitigationInDossier",
                value: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IncludeLitigationInDossier",
                table: "Clients");
        }
    }
}
