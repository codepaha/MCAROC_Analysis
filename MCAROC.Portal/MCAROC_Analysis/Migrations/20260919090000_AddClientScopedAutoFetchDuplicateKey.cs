using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations;

public partial class AddClientScopedAutoFetchDuplicateKey : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AutoFetchCompanyIdentifier",
            table: "Requests",
            type: "nvarchar(30)",
            maxLength: 30,
            nullable: true);

        // Preserve deduplication for requests created before the identifier column existed. Only
        // AutoFetch jobs qualify: manual requests may use the same CIN but are intentionally outside
        // this entry point's idempotency contract. Retain the most recent job if old duplicate rows
        // already exist, rather than making the migration fail on historic data.
        migrationBuilder.Sql("""
            WITH Candidates AS (
                SELECT r.RequestId,
                       j.Cin,
                       ROW_NUMBER() OVER (
                           PARTITION BY r.ClientId, j.Cin
                           ORDER BY r.RequestId DESC) AS RowNumber
                FROM Requests AS r
                INNER JOIN AutoFetchJobs AS j ON j.RequestId = r.RequestId
                WHERE r.AutoFetchCompanyIdentifier IS NULL
                  AND j.Cin IS NOT NULL
            )
            UPDATE r
            SET AutoFetchCompanyIdentifier = c.Cin
            FROM Requests AS r
            INNER JOIN Candidates AS c ON c.RequestId = r.RequestId
            WHERE c.RowNumber = 1;
            """);

        migrationBuilder.CreateIndex(
            name: "UX_McaRequests_Client_AutoFetchIdentifier",
            table: "Requests",
            columns: new[] { "ClientId", "AutoFetchCompanyIdentifier" },
            unique: true,
            filter: "[AutoFetchCompanyIdentifier] IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "UX_McaRequests_Client_AutoFetchIdentifier",
            table: "Requests");

        migrationBuilder.DropColumn(
            name: "AutoFetchCompanyIdentifier",
            table: "Requests");
    }
}
