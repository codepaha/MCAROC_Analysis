using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddChargeDocumentLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChargeLinksStamp",
                table: "McaFilingBatches",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ChargeDocumentLinks",
                columns: table => new
                {
                    ChargeDocumentLinkId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    BatchId = table.Column<long>(type: "bigint", nullable: false),
                    RocChargeNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    FilingDocumentId = table.Column<long>(type: "bigint", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    LinkedFromDocumentId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChargeDocumentLinks", x => x.ChargeDocumentLinkId);
                    table.ForeignKey(
                        name: "FK_ChargeDocumentLinks_McaFilingDocuments_FilingDocumentId",
                        column: x => x.FilingDocumentId,
                        principalTable: "McaFilingDocuments",
                        principalColumn: "FilingDocumentId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChargeDocumentLinks_BatchId_RocChargeNumber_FilingDocumentId",
                table: "ChargeDocumentLinks",
                columns: new[] { "BatchId", "RocChargeNumber", "FilingDocumentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChargeDocumentLinks_FilingDocumentId",
                table: "ChargeDocumentLinks",
                column: "FilingDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ChargeDocumentLinks_RequestId_RocChargeNumber",
                table: "ChargeDocumentLinks",
                columns: new[] { "RequestId", "RocChargeNumber" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChargeDocumentLinks");

            migrationBuilder.DropColumn(
                name: "ChargeLinksStamp",
                table: "McaFilingBatches");
        }
    }
}
