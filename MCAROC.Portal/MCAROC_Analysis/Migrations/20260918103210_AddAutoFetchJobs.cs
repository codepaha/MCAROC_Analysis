using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoFetchJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AutoFetchJobs",
                columns: table => new
                {
                    AutoFetchJobId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    Cin = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Bid = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ProgressPercent = table.Column<int>(type: "int", nullable: false),
                    StatusMessage = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    WarningsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IncludeFilings = table.Column<bool>(type: "bit", nullable: false),
                    MaxDocumentsPerSection = table.Column<int>(type: "int", nullable: false),
                    RegistryTotalCount = table.Column<int>(type: "int", nullable: false),
                    RegistryListedCount = table.Column<int>(type: "int", nullable: false),
                    FilesTotal = table.Column<int>(type: "int", nullable: false),
                    FilesDownloaded = table.Column<int>(type: "int", nullable: false),
                    FilesFailed = table.Column<int>(type: "int", nullable: false),
                    BytesDownloaded = table.Column<long>(type: "bigint", nullable: false),
                    RocDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    ChargeDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: true),
                    FilingsDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    FilingBatchId = table.Column<long>(type: "bigint", nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    HeartbeatUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutoFetchJobs", x => x.AutoFetchJobId);
                    table.ForeignKey(
                        name: "FK_AutoFetchJobs_Requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "Requests",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutoFetchJobs_RequestId",
                table: "AutoFetchJobs",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutoFetchJobs_Status",
                table: "AutoFetchJobs",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutoFetchJobs");
        }
    }
}
