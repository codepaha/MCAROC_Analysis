using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddLitigationFetchWorkQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FetchWorkItems",
                schema: "litigation",
                columns: table => new
                {
                    WorkItemId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Host = table.Column<int>(type: "int", nullable: false),
                    CaseId = table.Column<long>(type: "bigint", nullable: true),
                    RunId = table.Column<long>(type: "bigint", nullable: true),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    LeaseExpiryCount = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    NextAttemptUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseOwner = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    LeaseExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastOutcome = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FetchWorkItems", x => x.WorkItemId);
                });

            migrationBuilder.CreateTable(
                name: "RefreshRuns",
                schema: "litigation",
                columns: table => new
                {
                    RunId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EligibleAtStart = table.Column<int>(type: "int", nullable: false),
                    LastAgentHeartbeatUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FinishedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FailedReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Phase = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshRuns", x => x.RunId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FetchWorkItems_Claim",
                schema: "litigation",
                table: "FetchWorkItems",
                columns: new[] { "Host", "Status", "NextAttemptUtc", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_FetchWorkItems_RunId",
                schema: "litigation",
                table: "FetchWorkItems",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "UX_FetchWorkItems_Active",
                schema: "litigation",
                table: "FetchWorkItems",
                columns: new[] { "Kind", "Host", "CaseId" },
                unique: true,
                filter: "[CaseId] IS NOT NULL AND [Status] IN (1, 2)");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshRuns_RequestId_StartedUtc",
                schema: "litigation",
                table: "RefreshRuns",
                columns: new[] { "RequestId", "StartedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FetchWorkItems",
                schema: "litigation");

            migrationBuilder.DropTable(
                name: "RefreshRuns",
                schema: "litigation");
        }
    }
}
