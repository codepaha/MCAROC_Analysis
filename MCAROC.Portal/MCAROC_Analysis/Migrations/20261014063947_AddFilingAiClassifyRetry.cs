using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddFilingAiClassifyRetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AiClassifyAttempts",
                table: "McaFilingDocuments",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "AiClassifyNextAttemptUtc",
                table: "McaFilingDocuments",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiClassifyAttempts",
                table: "McaFilingDocuments");

            migrationBuilder.DropColumn(
                name: "AiClassifyNextAttemptUtc",
                table: "McaFilingDocuments");
        }
    }
}
