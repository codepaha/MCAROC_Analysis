using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddResumableLargeArchiveUploadAndSourceManifest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RequestDocuments_RequestId",
                table: "RequestDocuments");

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "Requests",
                type: "rowversion",
                rowVersion: true,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsActiveSource",
                table: "RequestDocuments",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "SupersededByDocumentId",
                table: "RequestDocuments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UploadSessionId",
                table: "RequestDocuments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UploadSessionId",
                table: "McaFilingBatches",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LargeArchiveUploadSessions",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    HashedCapabilityToken = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OriginalFileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    TotalExpectedSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    NextExpectedOffset = table.Column<long>(type: "bigint", nullable: false),
                    ExpectedFullSha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    StagingFilePath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    DestinationStoragePath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ActiveWriteAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActiveWriteOffset = table.Column<long>(type: "bigint", nullable: true),
                    ActiveWriteExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FinalizationAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActiveFinalizationExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedBatchId = table.Column<long>(type: "bigint", nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastHeartbeatUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LargeArchiveUploadSessions", x => x.SessionId);
                    table.ForeignKey(
                        name: "FK_LargeArchiveUploadSessions_Requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "Requests",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OperationalSlotLeases",
                columns: table => new
                {
                    SlotType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ActiveHolderId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    AcquiredUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastHeartbeatUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperationalSlotLeases", x => x.SlotType);
                });

            migrationBuilder.CreateTable(
                name: "StorageCapacityReservations",
                columns: table => new
                {
                    ReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    OwnerId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    VolumeRoot = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ReservedBytes = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastHeartbeatUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageCapacityReservations", x => x.ReservationId);
                });

            migrationBuilder.CreateTable(
                name: "StorageVolumeLeases",
                columns: table => new
                {
                    VolumeRoot = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ActiveReservedBytes = table.Column<long>(type: "bigint", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageVolumeLeases", x => x.VolumeRoot);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RequestDocuments_SupersededByDocumentId",
                table: "RequestDocuments",
                column: "SupersededByDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_RequestDocuments_UploadSessionId",
                table: "RequestDocuments",
                column: "UploadSessionId",
                unique: true,
                filter: "[UploadSessionId] IS NOT NULL");

            // Authoritative backfill using exact persisted string enum values before creating UX_RequestDocuments_ActiveSource
            migrationBuilder.Sql(@"
                UPDATE rd
                SET rd.IsActiveSource = 1
                FROM RequestDocuments rd
                INNER JOIN IngestionRuns ir ON ir.SourceRocDocumentId = rd.DocumentId
                INNER JOIN Requests r ON r.RequestId = rd.RequestId AND r.LatestCompletedIngestionRunId = ir.IngestionRunId
                WHERE rd.DocumentType = 'McaRocReport';

                UPDATE rd
                SET rd.IsActiveSource = 1
                FROM RequestDocuments rd
                INNER JOIN IngestionRuns ir ON ir.SourceChargeDocumentId = rd.DocumentId
                INNER JOIN Requests r ON r.RequestId = rd.RequestId AND r.LatestCompletedIngestionRunId = ir.IngestionRunId
                WHERE rd.DocumentType = 'ChargeReport';
            ");

            migrationBuilder.CreateIndex(
                name: "UX_RequestDocuments_ActiveSource",
                table: "RequestDocuments",
                columns: new[] { "RequestId", "DocumentType" },
                unique: true,
                filter: "[IsActiveSource] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_McaFilingBatches_UploadSessionId",
                table: "McaFilingBatches",
                column: "UploadSessionId",
                unique: true,
                filter: "[UploadSessionId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LargeArchiveUploadSessions_HashedCapabilityToken",
                table: "LargeArchiveUploadSessions",
                column: "HashedCapabilityToken");

            migrationBuilder.CreateIndex(
                name: "IX_LargeArchiveUploadSessions_RequestId",
                table: "LargeArchiveUploadSessions",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_LargeArchiveUploadSessions_Status_ExpiresUtc",
                table: "LargeArchiveUploadSessions",
                columns: new[] { "Status", "ExpiresUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_StorageCapacityReservations_OwnerType_OwnerId",
                table: "StorageCapacityReservations",
                columns: new[] { "OwnerType", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_StorageCapacityReservations_VolumeRoot_State_ExpiresUtc",
                table: "StorageCapacityReservations",
                columns: new[] { "VolumeRoot", "State", "ExpiresUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_RequestDocuments_RequestDocuments_SupersededByDocumentId",
                table: "RequestDocuments",
                column: "SupersededByDocumentId",
                principalTable: "RequestDocuments",
                principalColumn: "DocumentId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RequestDocuments_RequestDocuments_SupersededByDocumentId",
                table: "RequestDocuments");

            migrationBuilder.DropTable(
                name: "LargeArchiveUploadSessions");

            migrationBuilder.DropTable(
                name: "OperationalSlotLeases");

            migrationBuilder.DropTable(
                name: "StorageCapacityReservations");

            migrationBuilder.DropTable(
                name: "StorageVolumeLeases");

            migrationBuilder.DropIndex(
                name: "IX_RequestDocuments_SupersededByDocumentId",
                table: "RequestDocuments");

            migrationBuilder.DropIndex(
                name: "IX_RequestDocuments_UploadSessionId",
                table: "RequestDocuments");

            migrationBuilder.DropIndex(
                name: "UX_RequestDocuments_ActiveSource",
                table: "RequestDocuments");

            migrationBuilder.DropIndex(
                name: "IX_McaFilingBatches_UploadSessionId",
                table: "McaFilingBatches");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "Requests");

            migrationBuilder.DropColumn(
                name: "IsActiveSource",
                table: "RequestDocuments");

            migrationBuilder.DropColumn(
                name: "SupersededByDocumentId",
                table: "RequestDocuments");

            migrationBuilder.DropColumn(
                name: "UploadSessionId",
                table: "RequestDocuments");

            migrationBuilder.DropColumn(
                name: "UploadSessionId",
                table: "McaFilingBatches");

            migrationBuilder.CreateIndex(
                name: "IX_RequestDocuments_RequestId",
                table: "RequestDocuments",
                column: "RequestId");
        }
    }
}
