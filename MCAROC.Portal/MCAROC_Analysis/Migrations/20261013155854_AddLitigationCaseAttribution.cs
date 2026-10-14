using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddLitigationCaseAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AttributionDecidedUtc",
                table: "LitigationCases",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttributionOutcome",
                table: "LitigationCases",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttributionParty",
                table: "LitigationCases",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttributionReason",
                table: "LitigationCases",
                type: "nvarchar(600)",
                maxLength: 600,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttributionRule",
                table: "LitigationCases",
                type: "nvarchar(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LitigationPartyVerdicts",
                columns: table => new
                {
                    LitigationPartyVerdictId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SubjectKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PartyKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    PartyName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    PromptVersion = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Verdict = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LitigationPartyVerdicts", x => x.LitigationPartyVerdictId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LitigationCases_RequestId_AttributionOutcome",
                table: "LitigationCases",
                columns: new[] { "RequestId", "AttributionOutcome" });

            migrationBuilder.CreateIndex(
                name: "IX_LitigationPartyVerdicts_SubjectKey_PartyKey_PromptVersion",
                table: "LitigationPartyVerdicts",
                columns: new[] { "SubjectKey", "PartyKey", "PromptVersion" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LitigationPartyVerdicts");

            migrationBuilder.DropIndex(
                name: "IX_LitigationCases_RequestId_AttributionOutcome",
                table: "LitigationCases");

            migrationBuilder.DropColumn(
                name: "AttributionDecidedUtc",
                table: "LitigationCases");

            migrationBuilder.DropColumn(
                name: "AttributionOutcome",
                table: "LitigationCases");

            migrationBuilder.DropColumn(
                name: "AttributionParty",
                table: "LitigationCases");

            migrationBuilder.DropColumn(
                name: "AttributionReason",
                table: "LitigationCases");

            migrationBuilder.DropColumn(
                name: "AttributionRule",
                table: "LitigationCases");
        }
    }
}
