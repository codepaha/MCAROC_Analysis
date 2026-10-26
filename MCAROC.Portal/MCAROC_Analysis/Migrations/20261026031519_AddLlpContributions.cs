using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddLlpContributions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LlpDesignatedPartners",
                table: "CompanyStructures",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LlpPartners",
                table: "CompanyStructures",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LlpContributionReceivedCrore",
                table: "CompanyProfiles",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LlpObligationOfContributionCrore",
                table: "CompanyProfiles",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LlpContributions",
                columns: table => new
                {
                    LlpContributionId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AsOnDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Designation = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    NomineeName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ObligationOfContributionCrore = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    ContributionReceivedCrore = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    DisplayOrder = table.Column<int>(type: "int", nullable: false),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LlpContributions", x => x.LlpContributionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LlpContributions_RequestId_IngestionRunId",
                table: "LlpContributions",
                columns: new[] { "RequestId", "IngestionRunId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LlpContributions");

            migrationBuilder.DropColumn(
                name: "LlpDesignatedPartners",
                table: "CompanyStructures");

            migrationBuilder.DropColumn(
                name: "LlpPartners",
                table: "CompanyStructures");

            migrationBuilder.DropColumn(
                name: "LlpContributionReceivedCrore",
                table: "CompanyProfiles");

            migrationBuilder.DropColumn(
                name: "LlpObligationOfContributionCrore",
                table: "CompanyProfiles");
        }
    }
}
