using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyNameVariations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CompanyNameVariations",
                columns: table => new
                {
                    CompanyNameVariationId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Identifier = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    NameKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    AddedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    AddedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AddedOnRequestId = table.Column<long>(type: "bigint", nullable: false),
                    RemovedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RemovedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyNameVariations", x => x.CompanyNameVariationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyNameVariations_AddedOnRequestId",
                table: "CompanyNameVariations",
                column: "AddedOnRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyNameVariations_Identifier_NameKey",
                table: "CompanyNameVariations",
                columns: new[] { "Identifier", "NameKey" },
                unique: true,
                filter: "[RemovedUtc] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompanyNameVariations");
        }
    }
}
