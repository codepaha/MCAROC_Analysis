using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class ChunkingDiagnosticsAndRetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChunkingErrorCategory",
                table: "McaFilingDocuments",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ChunkingFailedUtc",
                table: "McaFilingDocuments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ChunkingLastAttemptUtc",
                table: "McaFilingDocuments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ChunkingLastError",
                table: "McaFilingDocuments",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChunkingErrorCategory",
                table: "McaFilingDocuments");

            migrationBuilder.DropColumn(
                name: "ChunkingFailedUtc",
                table: "McaFilingDocuments");

            migrationBuilder.DropColumn(
                name: "ChunkingLastAttemptUtc",
                table: "McaFilingDocuments");

            migrationBuilder.DropColumn(
                name: "ChunkingLastError",
                table: "McaFilingDocuments");
        }
    }
}
