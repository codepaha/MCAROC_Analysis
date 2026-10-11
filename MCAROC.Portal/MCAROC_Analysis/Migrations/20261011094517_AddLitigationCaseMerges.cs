using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddLitigationCaseMerges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "MergedIntoCaseId",
                table: "LitigationCases",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CollapsedIntoOrderId",
                table: "LitigationCaseOrders",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CaseMergeChanges",
                schema: "litigation",
                columns: table => new
                {
                    ChangeId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MergeId = table.Column<long>(type: "bigint", nullable: false),
                    Table = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RowId = table.Column<long>(type: "bigint", nullable: false),
                    Column = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BeforeValue = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    AfterValue = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseMergeChanges", x => x.ChangeId);
                });

            migrationBuilder.CreateTable(
                name: "CaseMerges",
                schema: "litigation",
                columns: table => new
                {
                    MergeId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    SurvivorCaseId = table.Column<long>(type: "bigint", nullable: false),
                    LoserCaseId = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    MergedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReversedUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseMerges", x => x.MergeId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CaseMergeChanges_MergeId",
                schema: "litigation",
                table: "CaseMergeChanges",
                column: "MergeId");

            migrationBuilder.CreateIndex(
                name: "IX_CaseMerges_RequestId_LoserCaseId",
                schema: "litigation",
                table: "CaseMerges",
                columns: new[] { "RequestId", "LoserCaseId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CaseMergeChanges",
                schema: "litigation");

            migrationBuilder.DropTable(
                name: "CaseMerges",
                schema: "litigation");

            migrationBuilder.DropColumn(
                name: "MergedIntoCaseId",
                table: "LitigationCases");

            migrationBuilder.DropColumn(
                name: "CollapsedIntoOrderId",
                table: "LitigationCaseOrders");
        }
    }
}
