using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddAiAuditLeaseAndDedup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseExpiresUtc",
                table: "CalculationAiAuditRuns",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseOwner",
                table: "CalculationAiAuditRuns",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextAttemptUtc",
                table: "CalculationAiAuditRuns",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalculationDiscrepancies_AiAuditRunId_PrimaryLedgerEntryId",
                table: "CalculationDiscrepancies",
                columns: new[] { "AiAuditRunId", "PrimaryLedgerEntryId" },
                unique: true,
                filter: "[AiAuditRunId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CalculationDiscrepancies_AiAuditRunId_PrimaryLedgerEntryId",
                table: "CalculationDiscrepancies");

            migrationBuilder.DropColumn(
                name: "LeaseExpiresUtc",
                table: "CalculationAiAuditRuns");

            migrationBuilder.DropColumn(
                name: "LeaseOwner",
                table: "CalculationAiAuditRuns");

            migrationBuilder.DropColumn(
                name: "NextAttemptUtc",
                table: "CalculationAiAuditRuns");
        }
    }
}
