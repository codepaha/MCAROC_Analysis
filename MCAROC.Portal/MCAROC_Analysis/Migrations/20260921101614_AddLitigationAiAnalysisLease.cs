using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddLitigationAiAnalysisLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LeaseToken",
                table: "LitigationAiAnalysisRuns",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LitigationAiAnalysisRuns_RequestId",
                table: "LitigationAiAnalysisRuns",
                column: "RequestId",
                unique: true,
                filter: "[Status] IN ('Pending', 'InProgress')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LitigationAiAnalysisRuns_RequestId",
                table: "LitigationAiAnalysisRuns");

            migrationBuilder.DropColumn(
                name: "LeaseToken",
                table: "LitigationAiAnalysisRuns");
        }
    }
}
