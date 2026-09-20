using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddLitigationSearchJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LitigationSearchJobs",
                columns: table => new
                {
                    LitigationSearchJobId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ProgressPercent = table.Column<int>(type: "int", nullable: false),
                    StatusMessage = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EntityType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ApplicationCustomerId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    KeywordsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    VendorJobId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RegisteredUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RegistrationAttemptedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    LeaseOwner = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NextAttemptUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReportFormat = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RawReportBytes = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    RawResponseHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RawReportByteLength = table.Column<long>(type: "bigint", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LitigationSearchJobs", x => x.LitigationSearchJobId);
                    table.ForeignKey(
                        name: "FK_LitigationSearchJobs_Requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "Requests",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LitigationSearchJobs_RequestId",
                table: "LitigationSearchJobs",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LitigationSearchJobs_Status",
                table: "LitigationSearchJobs",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LitigationSearchJobs");
        }
    }
}
