using System;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentChatEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ChunkRetryCount",
                table: "McaFilingDocuments",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ChunkingStatus",
                table: "McaFilingDocuments",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Pending"); // matches ChunkingStatus.Pending's HasConversion<string>() value — EF's
                                           // generator defaults a new converted-enum column to CLR default ("")
                                           // rather than the converted string; existing rows must land on a real
                                           // enum value, not an empty string that fails to round-trip.

            migrationBuilder.CreateTable(
                name: "ChatSessions",
                columns: table => new
                {
                    ChatSessionId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastActivityDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatSessions", x => x.ChatSessionId);
                    table.ForeignKey(
                        name: "FK_ChatSessions_Requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "Requests",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DocumentChunks",
                columns: table => new
                {
                    ChunkId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    FilingDocumentId = table.Column<long>(type: "bigint", nullable: false),
                    FilingId = table.Column<long>(type: "bigint", nullable: false),
                    BatchId = table.Column<long>(type: "bigint", nullable: false),
                    Srn = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FormType = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    DocumentName = table.Column<string>(type: "nvarchar(max)", nullable: false),
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
                    table.PrimaryKey("PK_DocumentChunks", x => x.ChunkId);
                });

            migrationBuilder.CreateTable(
                name: "ChatMessages",
                columns: table => new
                {
                    ChatMessageId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ChatSessionId = table.Column<long>(type: "bigint", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    MessageText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RetrievedSourcesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CitedSourcesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Model = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PromptVersion = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EmbeddingModel = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TopK = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatMessages", x => x.ChatMessageId);
                    table.ForeignKey(
                        name: "FK_ChatMessages_ChatSessions_ChatSessionId",
                        column: x => x.ChatSessionId,
                        principalTable: "ChatSessions",
                        principalColumn: "ChatSessionId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_ChatSessionId",
                table: "ChatMessages",
                column: "ChatSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatSessions_RequestId",
                table: "ChatSessions",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentChunks_FilingDocumentId",
                table: "DocumentChunks",
                column: "FilingDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentChunks_FilingId",
                table: "DocumentChunks",
                column: "FilingId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentChunks_RequestId",
                table: "DocumentChunks",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentChunks_RequestId_Category",
                table: "DocumentChunks",
                columns: new[] { "RequestId", "Category" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentChunks_RequestId_FormType",
                table: "DocumentChunks",
                columns: new[] { "RequestId", "FormType" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentChunks_RequestId_Srn",
                table: "DocumentChunks",
                columns: new[] { "RequestId", "Srn" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatMessages");

            migrationBuilder.DropTable(
                name: "DocumentChunks");

            migrationBuilder.DropTable(
                name: "ChatSessions");

            migrationBuilder.DropColumn(
                name: "ChunkRetryCount",
                table: "McaFilingDocuments");

            migrationBuilder.DropColumn(
                name: "ChunkingStatus",
                table: "McaFilingDocuments");
        }
    }
}
