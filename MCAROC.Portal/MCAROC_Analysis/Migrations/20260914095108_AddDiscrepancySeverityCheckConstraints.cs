using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscrepancySeverityCheckConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_CalculationDiscrepancyApprovals_ProposedSeverity",
                table: "CalculationDiscrepancyApprovals",
                sql: "[ProposedSeverity] IS NULL OR [ProposedSeverity] IN ('Minor', 'Material', 'Critical')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CalculationDiscrepancies_PendingConfirmSeverity",
                table: "CalculationDiscrepancies",
                sql: "[PendingConfirmSeverity] IS NULL OR [PendingConfirmSeverity] IN ('Minor', 'Material', 'Critical')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CalculationDiscrepancies_Severity",
                table: "CalculationDiscrepancies",
                sql: "[Severity] IS NULL OR [Severity] IN ('Minor', 'Material', 'Critical')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_CalculationDiscrepancyApprovals_ProposedSeverity",
                table: "CalculationDiscrepancyApprovals");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CalculationDiscrepancies_PendingConfirmSeverity",
                table: "CalculationDiscrepancies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CalculationDiscrepancies_Severity",
                table: "CalculationDiscrepancies");
        }
    }
}
