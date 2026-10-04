using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddLitigationCaseProviderRecordKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The record id was never read before (the reader looked for "_id", reports carry "id"), so it is null on real data. Where a value
            // exists it must fit the key and be unique per request: clear any that does not, and every repeat after the first. The id stays on
            // the LitigationCaseSourceReports row of each report that listed the case, so nothing is lost.
            migrationBuilder.Sql(@"
UPDATE LitigationCases SET ProviderCaseId = NULL WHERE LEN(ProviderCaseId) > 100;
WITH ranked AS (
    SELECT LitigationCaseId, ROW_NUMBER() OVER (PARTITION BY RequestId, ProviderCaseId ORDER BY LitigationCaseId) AS n
    FROM LitigationCases WHERE ProviderCaseId IS NOT NULL)
UPDATE c SET c.ProviderCaseId = NULL FROM LitigationCases c JOIN ranked r ON r.LitigationCaseId = c.LitigationCaseId WHERE r.n > 1;");

            migrationBuilder.AlterColumn<string>(
                name: "ProviderCaseId",
                table: "LitigationCases",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LitigationCases_RequestId_ProviderCaseId",
                table: "LitigationCases",
                columns: new[] { "RequestId", "ProviderCaseId" },
                unique: true,
                filter: "[ProviderCaseId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LitigationCases_RequestId_ProviderCaseId",
                table: "LitigationCases");

            migrationBuilder.AlterColumn<string>(
                name: "ProviderCaseId",
                table: "LitigationCases",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(100)",
                oldMaxLength: 100,
                oldNullable: true);
        }
    }
}
