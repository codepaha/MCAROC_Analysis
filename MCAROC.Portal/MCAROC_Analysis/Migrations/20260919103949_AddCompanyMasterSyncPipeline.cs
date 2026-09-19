using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyMasterSyncPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CompanyMasterSyncJobs",
                columns: table => new
                {
                    JobId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FencingToken = table.Column<long>(type: "bigint", nullable: false),
                    TriggerType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    PublishedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PublishedDateRaw = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PublishedDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AggregateChecksum = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    LeaseOwnerInstanceId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastHeartbeatUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LeaseExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Duration = table.Column<TimeSpan>(type: "time", nullable: true),
                    SanitizedProxyAlias = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CaptchaAttempts = table.Column<int>(type: "int", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyMasterSyncJobs", x => x.JobId);
                });

            migrationBuilder.CreateTable(
                name: "Staging_CompanyMasterRecords",
                columns: table => new
                {
                    StagingId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SyncRunId = table.Column<long>(type: "bigint", nullable: false),
                    FencingToken = table.Column<long>(type: "bigint", nullable: false),
                    BatchKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SourceChecksum = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ValidationState = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    IsPromoted = table.Column<bool>(type: "bit", nullable: false),
                    RowIndex = table.Column<long>(type: "bigint", nullable: false),
                    Identifier = table.Column<string>(type: "nvarchar(25)", maxLength: 25, nullable: false),
                    RecordType = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    RegistrationDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Category = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Class = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ListingStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    AuthorizedCapital = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    PaidupCapital = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Roc = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Address = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PinCode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    State = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    District = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Country = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: true),
                    SubCategory = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    IndustrialClassification = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Staging_CompanyMasterRecords", x => x.StagingId);
                });

            migrationBuilder.CreateTable(
                name: "CompanyMasterSyncMetrics",
                columns: table => new
                {
                    MetricId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    JobId = table.Column<long>(type: "bigint", nullable: false),
                    RecordType = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    TotalSourceRows = table.Column<int>(type: "int", nullable: false),
                    NewRowsAdded = table.Column<int>(type: "int", nullable: false),
                    ExistingRowsUpdated = table.Column<int>(type: "int", nullable: false),
                    UnchangedRowsSkipped = table.Column<int>(type: "int", nullable: false),
                    CorruptedRowsSkipped = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyMasterSyncMetrics", x => x.MetricId);
                    table.ForeignKey(
                        name: "FK_CompanyMasterSyncMetrics_CompanyMasterSyncJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "CompanyMasterSyncJobs",
                        principalColumn: "JobId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyMasterSyncJobs_FencingToken",
                table: "CompanyMasterSyncJobs",
                column: "FencingToken");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyMasterSyncJobs_Status_CreatedUtc",
                table: "CompanyMasterSyncJobs",
                columns: new[] { "Status", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CompanyMasterSyncMetrics_JobId_RecordType",
                table: "CompanyMasterSyncMetrics",
                columns: new[] { "JobId", "RecordType" });

            migrationBuilder.CreateIndex(
                name: "IX_Staging_CompanyMasterRecords_SyncRunId_FencingToken_ValidationState_IsPromoted",
                table: "Staging_CompanyMasterRecords",
                columns: new[] { "SyncRunId", "FencingToken", "ValidationState", "IsPromoted" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CompanyMasterSyncMetrics");

            migrationBuilder.DropTable(
                name: "Staging_CompanyMasterRecords");

            migrationBuilder.DropTable(
                name: "CompanyMasterSyncJobs");
        }
    }
}
