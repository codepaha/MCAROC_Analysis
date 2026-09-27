using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <summary>Issue #293: derived name columns for identity resolution. Nullable on purpose — existing rows are
    /// filled afterwards by the backfill (ImportCompanyMasterData --backfill-names), since the normalization
    /// rules live in C# (CompanyNameNormalizer) and are not duplicated in T-SQL here.</summary>
    /// <inheritdoc />
    public partial class AddCompanyMasterNameNormalization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EntityForm",
                table: "CompanyMasterRecords",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NameCore",
                table: "CompanyMasterRecords",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NameNormalized",
                table: "CompanyMasterRecords",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EntityForm",
                table: "Staging_CompanyMasterRecords",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NameCore",
                table: "Staging_CompanyMasterRecords",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NameNormalized",
                table: "Staging_CompanyMasterRecords",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompanyMasterRecords_RecordType_NameCore",
                table: "CompanyMasterRecords",
                columns: new[] { "RecordType", "NameCore" });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyMasterRecords_RecordType_NameNormalized",
                table: "CompanyMasterRecords",
                columns: new[] { "RecordType", "NameNormalized" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CompanyMasterRecords_RecordType_NameCore",
                table: "CompanyMasterRecords");

            migrationBuilder.DropIndex(
                name: "IX_CompanyMasterRecords_RecordType_NameNormalized",
                table: "CompanyMasterRecords");

            migrationBuilder.DropColumn(
                name: "EntityForm",
                table: "CompanyMasterRecords");

            migrationBuilder.DropColumn(
                name: "NameCore",
                table: "CompanyMasterRecords");

            migrationBuilder.DropColumn(
                name: "NameNormalized",
                table: "CompanyMasterRecords");

            migrationBuilder.DropColumn(
                name: "EntityForm",
                table: "Staging_CompanyMasterRecords");

            migrationBuilder.DropColumn(
                name: "NameCore",
                table: "Staging_CompanyMasterRecords");

            migrationBuilder.DropColumn(
                name: "NameNormalized",
                table: "Staging_CompanyMasterRecords");
        }
    }
}
