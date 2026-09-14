using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscrepancyWorkflowConcurrencyGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PendingConfirmSeverity",
                table: "CalculationDiscrepancies",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalculationDiscrepancyApprovals_CalculationDiscrepancyId_DecisionAction_ApprovalSequence",
                table: "CalculationDiscrepancyApprovals",
                columns: new[] { "CalculationDiscrepancyId", "DecisionAction", "ApprovalSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalculationDiscrepancyApprovals_CalculationDiscrepancyId_DecisionAction_ReviewerName",
                table: "CalculationDiscrepancyApprovals",
                columns: new[] { "CalculationDiscrepancyId", "DecisionAction", "ReviewerName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CalculationArtifactHolds_SourceDiscrepancyId",
                table: "CalculationArtifactHolds",
                column: "SourceDiscrepancyId",
                unique: true,
                filter: "[IsActive] = 1 AND [SourceDiscrepancyId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CalculationDiscrepancyApprovals_CalculationDiscrepancyId_DecisionAction_ApprovalSequence",
                table: "CalculationDiscrepancyApprovals");

            migrationBuilder.DropIndex(
                name: "IX_CalculationDiscrepancyApprovals_CalculationDiscrepancyId_DecisionAction_ReviewerName",
                table: "CalculationDiscrepancyApprovals");

            migrationBuilder.DropIndex(
                name: "IX_CalculationArtifactHolds_SourceDiscrepancyId",
                table: "CalculationArtifactHolds");

            migrationBuilder.DropColumn(
                name: "PendingConfirmSeverity",
                table: "CalculationDiscrepancies");
        }
    }
}
