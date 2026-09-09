using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddUniqueChatSessionRequestIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Collapse any pre-existing duplicate sessions (the race this unique index prevents) so the
            // CreateIndex below can't fail: move their messages onto the oldest session per request, then
            // delete the extras.
            migrationBuilder.Sql(@"
                UPDATE m SET m.ChatSessionId = k.KeepId
                FROM ChatMessages m
                JOIN ChatSessions s ON s.ChatSessionId = m.ChatSessionId
                JOIN (SELECT RequestId, MIN(ChatSessionId) AS KeepId FROM ChatSessions GROUP BY RequestId HAVING COUNT(*) > 1) k
                  ON k.RequestId = s.RequestId
                WHERE m.ChatSessionId <> k.KeepId;

                DELETE s
                FROM ChatSessions s
                JOIN (SELECT RequestId, MIN(ChatSessionId) AS KeepId FROM ChatSessions GROUP BY RequestId HAVING COUNT(*) > 1) k
                  ON k.RequestId = s.RequestId
                WHERE s.ChatSessionId <> k.KeepId;");

            migrationBuilder.DropIndex(
                name: "IX_ChatSessions_RequestId",
                table: "ChatSessions");

            migrationBuilder.CreateIndex(
                name: "IX_ChatSessions_RequestId",
                table: "ChatSessions",
                column: "RequestId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChatSessions_RequestId",
                table: "ChatSessions");

            migrationBuilder.CreateIndex(
                name: "IX_ChatSessions_RequestId",
                table: "ChatSessions",
                column: "RequestId");
        }
    }
}
