using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddLitigationRequestLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RequestLinks",
                schema: "litigation",
                columns: table => new
                {
                    LinkId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    CaseId = table.Column<long>(type: "bigint", nullable: false),
                    Verdict = table.Column<int>(type: "int", nullable: false),
                    Rule = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    MatchedParty = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    MatchedSide = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    RoleConflict = table.Column<bool>(type: "bit", nullable: false),
                    RoleNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PublishedProjectionVersion = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestLinks", x => x.LinkId);
                    table.ForeignKey(
                        name: "FK_RequestLinks_Cases_CaseId",
                        column: x => x.CaseId,
                        principalSchema: "litigation",
                        principalTable: "Cases",
                        principalColumn: "CaseId");
                });

            migrationBuilder.CreateIndex(
                name: "IX_RequestLinks_CaseId",
                schema: "litigation",
                table: "RequestLinks",
                column: "CaseId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestLinks_RequestId_CaseId",
                schema: "litigation",
                table: "RequestLinks",
                columns: new[] { "RequestId", "CaseId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RequestLinks",
                schema: "litigation");
        }
    }
}
