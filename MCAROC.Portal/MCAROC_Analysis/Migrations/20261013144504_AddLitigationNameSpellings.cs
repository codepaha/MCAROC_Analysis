using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddLitigationNameSpellings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LitigationNameSpellings",
                columns: table => new
                {
                    LitigationNameSpellingId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    NameKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Language = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    PromptVersion = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SpellingsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExpansionsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RejectedJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LitigationNameSpellings", x => x.LitigationNameSpellingId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LitigationNameSpellings_NameKey_Language_PromptVersion",
                table: "LitigationNameSpellings",
                columns: new[] { "NameKey", "Language", "PromptVersion" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LitigationNameSpellings");
        }
    }
}
