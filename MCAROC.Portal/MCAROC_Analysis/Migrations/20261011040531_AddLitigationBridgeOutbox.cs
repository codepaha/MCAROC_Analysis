using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddLitigationBridgeOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BridgeOutbox",
                schema: "litigation",
                columns: table => new
                {
                    OutboxId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    LegacyLitigationCaseId = table.Column<long>(type: "bigint", nullable: false),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DeadLetteredUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BridgeOutbox", x => x.OutboxId);
                    table.ForeignKey(
                        name: "FK_BridgeOutbox_LitigationCases_LegacyLitigationCaseId",
                        column: x => x.LegacyLitigationCaseId,
                        principalTable: "LitigationCases",
                        principalColumn: "LitigationCaseId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BridgeOutbox_LegacyLitigationCaseId",
                schema: "litigation",
                table: "BridgeOutbox",
                column: "LegacyLitigationCaseId");

            migrationBuilder.CreateIndex(
                name: "IX_BridgeOutbox_Pending",
                schema: "litigation",
                table: "BridgeOutbox",
                column: "OutboxId",
                filter: "[ProcessedUtc] IS NULL AND [DeadLetteredUtc] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BridgeOutbox",
                schema: "litigation");
        }
    }
}
