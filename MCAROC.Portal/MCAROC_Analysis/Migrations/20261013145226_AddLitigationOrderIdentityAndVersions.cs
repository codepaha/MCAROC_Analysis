using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddLitigationOrderIdentityAndVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                table: "LitigationOrderDocuments",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSuperseded",
                table: "LitigationOrderDocuments",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "LitigationOrderDocuments",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<bool>(
                name: "IsSuperseded",
                table: "LitigationOrderChunks",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                table: "LitigationCaseOrders",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSuperseded",
                table: "LitigationCaseOrders",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "OrderKey",
                table: "LitigationCaseOrders",
                type: "nvarchar(800)",
                maxLength: 800,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "LitigationCaseOrders",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceLocator",
                table: "LitigationCaseOrders",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "LitigationCaseOrders",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.DropIndex(
                name: "IX_LitigationOrderDocuments_LitigationCaseOrderId",
                table: "LitigationOrderDocuments");

            migrationBuilder.CreateIndex(
                name: "IX_LitigationOrderDocuments_LitigationCaseOrderId",
                table: "LitigationOrderDocuments",
                column: "LitigationCaseOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_LitigationOrderDocuments_LitigationCaseOrderId_Version",
                table: "LitigationOrderDocuments",
                columns: new[] { "LitigationCaseOrderId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LitigationOrderDocuments_LitigationCaseOrderId_IsSuperseded",
                table: "LitigationOrderDocuments",
                columns: new[] { "LitigationCaseOrderId", "IsSuperseded" });

            migrationBuilder.CreateIndex(
                name: "IX_LitigationOrderChunks_RequestId_IsSuperseded",
                table: "LitigationOrderChunks",
                columns: new[] { "RequestId", "IsSuperseded" });

            migrationBuilder.CreateIndex(
                name: "IX_LitigationCaseOrders_LitigationCaseId_OrderKey_Version",
                table: "LitigationCaseOrders",
                columns: new[] { "LitigationCaseId", "OrderKey", "Version" },
                filter: "[OrderKey] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LitigationOrderDocuments_LitigationCaseOrderId_Version",
                table: "LitigationOrderDocuments");

            migrationBuilder.DropIndex(
                name: "IX_LitigationOrderDocuments_LitigationCaseOrderId",
                table: "LitigationOrderDocuments");

            migrationBuilder.CreateIndex(
                name: "IX_LitigationOrderDocuments_LitigationCaseOrderId",
                table: "LitigationOrderDocuments",
                column: "LitigationCaseOrderId",
                unique: true);

            migrationBuilder.DropIndex(
                name: "IX_LitigationOrderDocuments_LitigationCaseOrderId_IsSuperseded",
                table: "LitigationOrderDocuments");

            migrationBuilder.DropIndex(
                name: "IX_LitigationOrderChunks_RequestId_IsSuperseded",
                table: "LitigationOrderChunks");

            migrationBuilder.DropIndex(
                name: "IX_LitigationCaseOrders_LitigationCaseId_OrderKey_Version",
                table: "LitigationCaseOrders");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "LitigationOrderDocuments");

            migrationBuilder.DropColumn(
                name: "IsSuperseded",
                table: "LitigationOrderDocuments");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "LitigationOrderDocuments");

            migrationBuilder.DropColumn(
                name: "IsSuperseded",
                table: "LitigationOrderChunks");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "LitigationCaseOrders");

            migrationBuilder.DropColumn(
                name: "IsSuperseded",
                table: "LitigationCaseOrders");

            migrationBuilder.DropColumn(
                name: "OrderKey",
                table: "LitigationCaseOrders");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "LitigationCaseOrders");

            migrationBuilder.DropColumn(
                name: "SourceLocator",
                table: "LitigationCaseOrders");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "LitigationCaseOrders");
        }
    }
}
