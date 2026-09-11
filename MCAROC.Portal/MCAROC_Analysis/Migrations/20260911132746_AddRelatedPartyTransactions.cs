using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddRelatedPartyTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RelatedPartyTransactions",
                columns: table => new
                {
                    RelatedPartyTransactionId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FinancialYearEnding = table.Column<DateOnly>(type: "date", nullable: true),
                    EntityType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EntityNameRaw = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EntityNameNormalized = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RelationshipRaw = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TransactionType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AmountCrore = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RelatedPartyTransactions", x => x.RelatedPartyTransactionId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RelatedPartyTransactions");
        }
    }
}
