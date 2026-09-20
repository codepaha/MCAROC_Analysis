using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddRequestDocumentDerivatives : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RequestDocumentDerivatives",
                columns: table => new
                {
                    DerivativeId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    DerivativeType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    SanitizerVersion = table.Column<int>(type: "int", nullable: false),
                    RawFileHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StoragePath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    FileHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestDocumentDerivatives", x => x.DerivativeId);
                    table.ForeignKey(
                        name: "FK_RequestDocumentDerivatives_RequestDocuments_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "RequestDocuments",
                        principalColumn: "DocumentId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RequestDocumentDerivatives_DocumentId_DerivativeType",
                table: "RequestDocumentDerivatives",
                columns: new[] { "DocumentId", "DerivativeType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RequestDocumentDerivatives_RequestId",
                table: "RequestDocumentDerivatives",
                column: "RequestId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RequestDocumentDerivatives");
        }
    }
}
