using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddPreLoginModuleScopeAndAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AttributionOutcome",
                schema: "litigation",
                table: "ScopeCases",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttributionParty",
                schema: "litigation",
                table: "ScopeCases",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttributionReason",
                schema: "litigation",
                table: "ScopeCases",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttributionRule",
                schema: "litigation",
                table: "ScopeCases",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TargetId",
                schema: "litigation",
                table: "ScopeCases",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PreLoginAttributionDecisions",
                columns: table => new
                {
                    PreLoginAttributionDecisionId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ScopeCaseId = table.Column<long>(type: "bigint", nullable: false),
                    PreLoginReportJobId = table.Column<long>(type: "bigint", nullable: false),
                    CaseRowId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TargetId = table.Column<long>(type: "bigint", nullable: false),
                    TargetKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    PartyName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ReasonKind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DecidedBy = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    DecidedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreLoginAttributionDecisions", x => x.PreLoginAttributionDecisionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScopeCases_TargetId",
                schema: "litigation",
                table: "ScopeCases",
                column: "TargetId");

            migrationBuilder.CreateIndex(
                name: "IX_PreLoginAttributionDecisions_PreLoginReportJobId_CaseRowId",
                table: "PreLoginAttributionDecisions",
                columns: new[] { "PreLoginReportJobId", "CaseRowId" });

            migrationBuilder.CreateIndex(
                name: "IX_PreLoginAttributionDecisions_ScopeCaseId_DecidedUtc",
                table: "PreLoginAttributionDecisions",
                columns: new[] { "ScopeCaseId", "DecidedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PreLoginAttributionDecisions_TargetId_Action",
                table: "PreLoginAttributionDecisions",
                columns: new[] { "TargetId", "Action" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PreLoginAttributionDecisions");

            migrationBuilder.DropIndex(
                name: "IX_ScopeCases_TargetId",
                schema: "litigation",
                table: "ScopeCases");

            migrationBuilder.DropColumn(
                name: "AttributionOutcome",
                schema: "litigation",
                table: "ScopeCases");

            migrationBuilder.DropColumn(
                name: "AttributionParty",
                schema: "litigation",
                table: "ScopeCases");

            migrationBuilder.DropColumn(
                name: "AttributionReason",
                schema: "litigation",
                table: "ScopeCases");

            migrationBuilder.DropColumn(
                name: "AttributionRule",
                schema: "litigation",
                table: "ScopeCases");

            migrationBuilder.DropColumn(
                name: "TargetId",
                schema: "litigation",
                table: "ScopeCases");
        }
    }
}
