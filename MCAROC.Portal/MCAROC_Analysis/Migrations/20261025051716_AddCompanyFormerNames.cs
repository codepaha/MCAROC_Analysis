using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyFormerNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CompanyFormerNames",
                columns: table => new
                {
                    CompanyFormerNameId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Identifier = table.Column<string>(type: "nvarchar(25)", maxLength: 25, nullable: false),
                    FormerName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    FormerNameNormalized = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    CurrentNameWhenSeen = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyFormerNames", x => x.CompanyFormerNameId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyFormerNames_FormerNameNormalized_Identifier",
                table: "CompanyFormerNames",
                columns: new[] { "FormerNameNormalized", "Identifier" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompanyFormerNames");
        }
    }
}
