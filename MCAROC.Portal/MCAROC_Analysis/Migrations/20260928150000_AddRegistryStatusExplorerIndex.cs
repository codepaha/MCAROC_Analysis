using MCAROC_Analysis.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260928150000_AddRegistryStatusExplorerIndex")]
public sealed class AddRegistryStatusExplorerIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_CompanyMasterRecords_RecordType_Status_Name",
            table: "CompanyMasterRecords",
            columns: new[] { "RecordType", "Status", "Name" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_CompanyMasterRecords_RecordType_Status_Name",
            table: "CompanyMasterRecords");
    }
}
