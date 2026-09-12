using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddChatMessageInReplyToId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "InReplyToChatMessageId",
                table: "ChatMessages",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_InReplyToChatMessageId",
                table: "ChatMessages",
                column: "InReplyToChatMessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChatMessages_InReplyToChatMessageId",
                table: "ChatMessages");

            migrationBuilder.DropColumn(
                name: "InReplyToChatMessageId",
                table: "ChatMessages");
        }
    }
}
