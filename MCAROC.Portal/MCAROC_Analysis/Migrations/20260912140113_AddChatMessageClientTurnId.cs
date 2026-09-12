using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddChatMessageClientTurnId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClientTurnId",
                table: "ChatMessages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChatMessages_ChatSessionId_ClientTurnId",
                table: "ChatMessages",
                columns: new[] { "ChatSessionId", "ClientTurnId" },
                unique: true,
                filter: "[ClientTurnId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChatMessages_ChatSessionId_ClientTurnId",
                table: "ChatMessages");

            migrationBuilder.DropColumn(
                name: "ClientTurnId",
                table: "ChatMessages");
        }
    }
}
