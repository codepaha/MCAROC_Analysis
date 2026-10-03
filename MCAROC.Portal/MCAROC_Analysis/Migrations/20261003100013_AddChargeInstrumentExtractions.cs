using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddChargeInstrumentExtractions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChargeInstrumentExtractions",
                columns: table => new
                {
                    ChargeInstrumentExtractionId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    FilingDocumentId = table.Column<long>(type: "bigint", nullable: false),
                    PromptVersion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ModelId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NextAttemptUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RawResponseJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RejectedFieldsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ReusedFromExtractionId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChargeInstrumentExtractions", x => x.ChargeInstrumentExtractionId);
                    table.ForeignKey(
                        name: "FK_ChargeInstrumentExtractions_McaFilingDocuments_FilingDocumentId",
                        column: x => x.FilingDocumentId,
                        principalTable: "McaFilingDocuments",
                        principalColumn: "FilingDocumentId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChargeInstrumentExtractions_FilingDocumentId_PromptVersion",
                table: "ChargeInstrumentExtractions",
                columns: new[] { "FilingDocumentId", "PromptVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChargeInstrumentExtractions_RequestId",
                table: "ChargeInstrumentExtractions",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_ChargeInstrumentExtractions_Status_NextAttemptUtc",
                table: "ChargeInstrumentExtractions",
                columns: new[] { "Status", "NextAttemptUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChargeInstrumentExtractions");
        }
    }
}
