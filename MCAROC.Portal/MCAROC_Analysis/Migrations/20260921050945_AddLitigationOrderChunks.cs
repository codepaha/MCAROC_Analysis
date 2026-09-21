using System;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddLitigationOrderChunks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ChunkRetryCount",
                table: "LitigationOrderDocuments",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ChunkingErrorCategory",
                table: "LitigationOrderDocuments",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ChunkingFailedUtc",
                table: "LitigationOrderDocuments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ChunkingLastAttemptUtc",
                table: "LitigationOrderDocuments",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ChunkingLastError",
                table: "LitigationOrderDocuments",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            // EF's own generator produced defaultValue: "" here — the CLR-side property initializer
            // (ChunkingStatus.Pending) isn't reflected through the string conversion into the migration's
            // AddColumn default. Fixed by hand: any LitigationOrderDocument row that already exists when
            // this migration runs (fully plausible — #243 already shipped) must backfill to "Pending", the
            // same value new rows get from the entity's own default, not an empty string that matches none
            // of the enum's converted values and would either throw on read or silently never match the
            // orchestrator's "find Pending documents" query.
            migrationBuilder.AddColumn<string>(
                name: "ChunkingStatus",
                table: "LitigationOrderDocuments",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Pending");

            migrationBuilder.CreateTable(
                name: "LitigationOrderChunks",
                columns: table => new
                {
                    LitigationOrderChunkId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    LitigationOrderDocumentId = table.Column<long>(type: "bigint", nullable: false),
                    LitigationCaseOrderId = table.Column<long>(type: "bigint", nullable: false),
                    LitigationCaseId = table.Column<long>(type: "bigint", nullable: false),
                    CaseNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Cnr = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Court = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OrderType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    OrderDate = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ChunkIndex = table.Column<int>(type: "int", nullable: false),
                    PageNumber = table.Column<int>(type: "int", nullable: false),
                    ChunkText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Embedding = table.Column<SqlVector<float>>(type: "vector(768)", nullable: false),
                    EmbeddingModel = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EmbeddingDimensions = table.Column<int>(type: "int", nullable: false),
                    ChunkingVersion = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LitigationOrderChunks", x => x.LitigationOrderChunkId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LitigationOrderChunks_LitigationCaseOrderId",
                table: "LitigationOrderChunks",
                column: "LitigationCaseOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_LitigationOrderChunks_LitigationOrderDocumentId",
                table: "LitigationOrderChunks",
                column: "LitigationOrderDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_LitigationOrderChunks_RequestId",
                table: "LitigationOrderChunks",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_LitigationOrderChunks_RequestId_LitigationCaseId",
                table: "LitigationOrderChunks",
                columns: new[] { "RequestId", "LitigationCaseId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LitigationOrderChunks");

            migrationBuilder.DropColumn(
                name: "ChunkRetryCount",
                table: "LitigationOrderDocuments");

            migrationBuilder.DropColumn(
                name: "ChunkingErrorCategory",
                table: "LitigationOrderDocuments");

            migrationBuilder.DropColumn(
                name: "ChunkingFailedUtc",
                table: "LitigationOrderDocuments");

            migrationBuilder.DropColumn(
                name: "ChunkingLastAttemptUtc",
                table: "LitigationOrderDocuments");

            migrationBuilder.DropColumn(
                name: "ChunkingLastError",
                table: "LitigationOrderDocuments");

            migrationBuilder.DropColumn(
                name: "ChunkingStatus",
                table: "LitigationOrderDocuments");
        }
    }
}
