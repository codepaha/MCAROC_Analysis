using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddScopeCaseAttributionGradeAndSignals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AttributionGrade",
                schema: "litigation",
                table: "ScopeCases",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttributionSignalsJson",
                schema: "litigation",
                table: "ScopeCases",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttributionGrade",
                table: "LitigationCases",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttributionSignalsJson",
                table: "LitigationCases",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScopeCases_AttributionGrade",
                schema: "litigation",
                table: "ScopeCases",
                column: "AttributionGrade");

            migrationBuilder.CreateIndex(
                name: "IX_LitigationCases_RequestId_AttributionGrade",
                table: "LitigationCases",
                columns: new[] { "RequestId", "AttributionGrade" });

            migrationBuilder.Sql(@"
                UPDATE [litigation].[ScopeCases]
                SET [AttributionGrade] = CASE [AttributionOutcome]
                    WHEN 'Attributed' THEN 'Confirmed'
                    WHEN 'NotTheCompany' THEN 'Excluded'
                    WHEN 'Ambiguous' THEN 'Possible'
                    ELSE [AttributionGrade]
                END
                WHERE [AttributionGrade] IS NULL AND [AttributionOutcome] IS NOT NULL;
            ");

            migrationBuilder.Sql(@"
                UPDATE [LitigationCases]
                SET [AttributionGrade] = CASE [AttributionOutcome]
                    WHEN 'Attributed' THEN 'Confirmed'
                    WHEN 'NotTheCompany' THEN 'Excluded'
                    WHEN 'Ambiguous' THEN 'Possible'
                    ELSE [AttributionGrade]
                END
                WHERE [AttributionGrade] IS NULL AND [AttributionOutcome] IS NOT NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ScopeCases_AttributionGrade",
                schema: "litigation",
                table: "ScopeCases");

            migrationBuilder.DropIndex(
                name: "IX_LitigationCases_RequestId_AttributionGrade",
                table: "LitigationCases");

            migrationBuilder.DropColumn(
                name: "AttributionGrade",
                schema: "litigation",
                table: "ScopeCases");

            migrationBuilder.DropColumn(
                name: "AttributionSignalsJson",
                schema: "litigation",
                table: "ScopeCases");

            migrationBuilder.DropColumn(
                name: "AttributionGrade",
                table: "LitigationCases");

            migrationBuilder.DropColumn(
                name: "AttributionSignalsJson",
                table: "LitigationCases");
        }
    }
}
