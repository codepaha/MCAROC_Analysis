using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddMcaDocumentRetentionAndSourceKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "KeepPermanently",
                table: "Requests",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "RetiredUtc",
                table: "McaFilingDocuments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceAttachmentName",
                table: "McaFilingDocuments",
                type: "nvarchar(260)",
                maxLength: 260,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceAwsPath",
                table: "McaFilingDocuments",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceDocId",
                table: "McaFilingDocuments",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_McaFilingDocuments_RetiredUtc",
                table: "McaFilingDocuments",
                column: "RetiredUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_McaFilingDocuments_RetiredUtc",
                table: "McaFilingDocuments");

            migrationBuilder.DropColumn(
                name: "KeepPermanently",
                table: "Requests");

            migrationBuilder.DropColumn(
                name: "RetiredUtc",
                table: "McaFilingDocuments");

            migrationBuilder.DropColumn(
                name: "SourceAttachmentName",
                table: "McaFilingDocuments");

            migrationBuilder.DropColumn(
                name: "SourceAwsPath",
                table: "McaFilingDocuments");

            migrationBuilder.DropColumn(
                name: "SourceDocId",
                table: "McaFilingDocuments");
        }
    }
}
