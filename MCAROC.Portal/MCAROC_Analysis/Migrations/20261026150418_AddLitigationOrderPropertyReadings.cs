using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddLitigationOrderPropertyReadings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LitigationOrderPropertyReadings",
                columns: table => new
                {
                    LitigationOrderPropertyReadingId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LitigationOrderDocumentId = table.Column<long>(type: "bigint", nullable: false),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    InputHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PromptVersion = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ModelId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NextAttemptUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PagesSentJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ReusedFromReadingId = table.Column<long>(type: "bigint", nullable: true),
                    RawResponseJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResponseHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RejectedFieldsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LitigationOrderPropertyReadings", x => x.LitigationOrderPropertyReadingId);
                    table.ForeignKey(
                        name: "FK_LitigationOrderPropertyReadings_LitigationOrderDocuments_LitigationOrderDocumentId",
                        column: x => x.LitigationOrderDocumentId,
                        principalTable: "LitigationOrderDocuments",
                        principalColumn: "LitigationOrderDocumentId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LitigationOrderPropertyReadings_InputHash_PromptVersion_Status",
                table: "LitigationOrderPropertyReadings",
                columns: new[] { "InputHash", "PromptVersion", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_LitigationOrderPropertyReadings_LitigationOrderDocumentId",
                table: "LitigationOrderPropertyReadings",
                column: "LitigationOrderDocumentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LitigationOrderPropertyReadings_RequestId_Status",
                table: "LitigationOrderPropertyReadings",
                columns: new[] { "RequestId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_LitigationOrderPropertyReadings_Status_NextAttemptUtc",
                table: "LitigationOrderPropertyReadings",
                columns: new[] { "Status", "NextAttemptUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LitigationOrderPropertyReadings");
        }
    }
}
