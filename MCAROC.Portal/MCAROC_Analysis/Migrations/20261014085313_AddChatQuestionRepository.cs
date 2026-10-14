using System;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddChatQuestionRepository : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChatQuestionGroups",
                columns: table => new
                {
                    ChatQuestionGroupId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CanonicalQuestion = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Centroid = table.Column<SqlVector<float>>(type: "vector(768)", nullable: false),
                    AskedCount = table.Column<int>(type: "int", nullable: false),
                    ClientCount = table.Column<int>(type: "int", nullable: false),
                    AnsweredWithEvidenceCount = table.Column<int>(type: "int", nullable: false),
                    AnsweredFromIndexCount = table.Column<int>(type: "int", nullable: false),
                    FirstAskedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastAskedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatQuestionGroups", x => x.ChatQuestionGroupId);
                });

            migrationBuilder.CreateTable(
                name: "ChatQuestionEntries",
                columns: table => new
                {
                    ChatQuestionEntryId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ChatMessageId = table.Column<long>(type: "bigint", nullable: false),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    ClientId = table.Column<long>(type: "bigint", nullable: false),
                    MaskedText = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Embedding = table.Column<SqlVector<float>>(type: "vector(768)", nullable: false),
                    ChatQuestionGroupId = table.Column<long>(type: "bigint", nullable: false),
                    AnsweredWithEvidence = table.Column<bool>(type: "bit", nullable: false),
                    AnsweredFromIndex = table.Column<bool>(type: "bit", nullable: false),
                    AskedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatQuestionEntries", x => x.ChatQuestionEntryId);
                    table.ForeignKey(
                        name: "FK_ChatQuestionEntries_ChatQuestionGroups_ChatQuestionGroupId",
                        column: x => x.ChatQuestionGroupId,
                        principalTable: "ChatQuestionGroups",
                        principalColumn: "ChatQuestionGroupId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChatQuestionEntries_ChatMessageId",
                table: "ChatQuestionEntries",
                column: "ChatMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatQuestionEntries_ChatQuestionGroupId_ClientId",
                table: "ChatQuestionEntries",
                columns: new[] { "ChatQuestionGroupId", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatQuestionGroups_AskedCount",
                table: "ChatQuestionGroups",
                column: "AskedCount");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatQuestionEntries");

            migrationBuilder.DropTable(
                name: "ChatQuestionGroups");
        }
    }
}
