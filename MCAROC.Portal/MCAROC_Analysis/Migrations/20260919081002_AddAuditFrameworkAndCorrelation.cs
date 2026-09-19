using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddAuditFrameworkAndCorrelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "McaFilingBatches",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "LargeArchiveUploadSessions",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "AutoFetchJobs",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(@"
                UPDATE McaFilingBatches
                SET CorrelationId = LOWER(REPLACE(CAST(NEWID() AS nvarchar(36)), '-', ''))
                WHERE CorrelationId IS NULL OR CorrelationId = '';

                UPDATE AutoFetchJobs
                SET CorrelationId = LOWER(REPLACE(CAST(NEWID() AS nvarchar(36)), '-', ''))
                WHERE CorrelationId IS NULL OR CorrelationId = '';

                UPDATE LargeArchiveUploadSessions
                SET CorrelationId = LOWER(REPLACE(CAST(NEWID() AS nvarchar(36)), '-', ''))
                WHERE CorrelationId IS NULL OR CorrelationId = '';
            ");

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    AuditLogId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TimestampUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ActorType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    EventKind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RequestId = table.Column<long>(type: "bigint", nullable: true),
                    EntityType = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    EntityId = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    EventPayloadJson = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.AuditLogId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Action_TimestampUtc",
                table: "AuditLogs",
                columns: new[] { "Action", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_RequestId_TimestampUtc",
                table: "AuditLogs",
                columns: new[] { "RequestId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Status_TimestampUtc",
                table: "AuditLogs",
                columns: new[] { "Status", "TimestampUtc" });

            migrationBuilder.Sql(@"
                IF OBJECT_ID('TR_AuditLogs_AppendOnly', 'TR') IS NULL
                BEGIN
                    EXEC(N'CREATE TRIGGER [TR_AuditLogs_AppendOnly]
                    ON [dbo].[AuditLogs]
                    INSTEAD OF UPDATE, DELETE
                    AS
                    BEGIN
                        SET NOCOUNT ON;
                        RAISERROR(''Table AuditLogs is append-only. UPDATE and DELETE operations are forbidden.'', 16, 1);
                        ROLLBACK TRANSACTION;
                    END;')
                END
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                IF OBJECT_ID('TR_AuditLogs_AppendOnly', 'TR') IS NOT NULL
                    DROP TRIGGER TR_AuditLogs_AppendOnly;
            ");

            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "McaFilingBatches");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "LargeArchiveUploadSessions");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "AutoFetchJobs");
        }
    }
}
