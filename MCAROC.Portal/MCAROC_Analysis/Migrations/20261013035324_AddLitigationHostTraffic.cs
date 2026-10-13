using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddLitigationHostTraffic : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HostTraffic",
                schema: "litigation",
                columns: table => new
                {
                    Host = table.Column<int>(type: "int", nullable: false),
                    Concurrency = table.Column<double>(type: "float", nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    OpenUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ProbeHolder = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ProbeUntilUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSuccessUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastFailureUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastFailure = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostTraffic", x => x.Host);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HostTraffic",
                schema: "litigation");
        }
    }
}
