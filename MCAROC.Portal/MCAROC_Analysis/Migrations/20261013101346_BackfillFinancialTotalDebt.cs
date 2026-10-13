using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class BackfillFinancialTotalDebt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Total Debt was never parsed (the sheet has no total line): fill it from the borrowings for every row already ingested,
            // exactly as the parser now derives it for new ones. Rows with neither borrowing line stay null.
            migrationBuilder.Sql(@"
UPDATE FinancialYearData
SET TotalDebt = ISNULL(LongTermBorrowings, 0) + ISNULL(ShortTermBorrowings, 0)
WHERE TotalDebt IS NULL AND (LongTermBorrowings IS NOT NULL OR ShortTermBorrowings IS NOT NULL);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible without losing a value that was never stored: no source row ever carried a Total Debt line, so a derived one is left in place.
        }
    }
}
