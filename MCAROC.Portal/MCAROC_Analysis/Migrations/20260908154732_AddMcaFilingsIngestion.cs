using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddMcaFilingsIngestion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "McaFilingBatches",
                columns: table => new
                {
                    BatchId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StartedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McaFilingBatches", x => x.BatchId);
                    table.ForeignKey(
                        name: "FK_McaFilingBatches_Requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "Requests",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "McaFilings",
                columns: table => new
                {
                    FilingId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BatchId = table.Column<long>(type: "bigint", nullable: false),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    Srn = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ParsedCompanyName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ParsedCin = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IdentityMatchesRequest = table.Column<bool>(type: "bit", nullable: false),
                    OuterCategoryFolder = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NestedZipName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ManualReviewRequired = table.Column<bool>(type: "bit", nullable: false),
                    ManualReviewReason = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McaFilings", x => x.FilingId);
                    table.ForeignKey(
                        name: "FK_McaFilings_McaFilingBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "McaFilingBatches",
                        principalColumn: "BatchId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "McaFilingDocuments",
                columns: table => new
                {
                    FilingDocumentId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FilingId = table.Column<long>(type: "bigint", nullable: false),
                    BatchId = table.Column<long>(type: "bigint", nullable: false),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    OriginalFileName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SourceFolder = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StoragePath = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FileHash = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    DuplicateOfDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    PageCount = table.Column<int>(type: "int", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FormType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClassificationConfidence = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    ClassificationMethod = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MatchedRule = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ProcessingStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    TextExtractionMethod = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    AiExtractionStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ExtractedTextPath = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExtractedCharCount = table.Column<int>(type: "int", nullable: false),
                    NativePageCount = table.Column<int>(type: "int", nullable: false),
                    OcrPageCount = table.Column<int>(type: "int", nullable: false),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ProcessingStartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ManualReviewRequired = table.Column<bool>(type: "bit", nullable: false),
                    ManualReviewReason = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McaFilingDocuments", x => x.FilingDocumentId);
                    table.ForeignKey(
                        name: "FK_McaFilingDocuments_McaFilingDocuments_DuplicateOfDocumentId",
                        column: x => x.DuplicateOfDocumentId,
                        principalTable: "McaFilingDocuments",
                        principalColumn: "FilingDocumentId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McaFilingDocuments_McaFilings_FilingId",
                        column: x => x.FilingId,
                        principalTable: "McaFilings",
                        principalColumn: "FilingId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "McaFilingExtractions",
                columns: table => new
                {
                    ExtractionId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FilingId = table.Column<long>(type: "bigint", nullable: true),
                    FilingDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    Model = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PromptVersion = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SchemaName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SchemaVersion = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExtractedJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RawModelResponse = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ValidationStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ValidationErrors = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExtractedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McaFilingExtractions", x => x.ExtractionId);
                    table.ForeignKey(
                        name: "FK_McaFilingExtractions_McaFilingDocuments_FilingDocumentId",
                        column: x => x.FilingDocumentId,
                        principalTable: "McaFilingDocuments",
                        principalColumn: "FilingDocumentId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_McaFilingExtractions_McaFilings_FilingId",
                        column: x => x.FilingId,
                        principalTable: "McaFilings",
                        principalColumn: "FilingId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_McaFilingBatches_RequestId",
                table: "McaFilingBatches",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_McaFilingDocuments_BatchId_FileHash",
                table: "McaFilingDocuments",
                columns: new[] { "BatchId", "FileHash" });

            migrationBuilder.CreateIndex(
                name: "IX_McaFilingDocuments_DuplicateOfDocumentId",
                table: "McaFilingDocuments",
                column: "DuplicateOfDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_McaFilingDocuments_FilingId",
                table: "McaFilingDocuments",
                column: "FilingId");

            migrationBuilder.CreateIndex(
                name: "IX_McaFilingDocuments_ProcessingStatus",
                table: "McaFilingDocuments",
                column: "ProcessingStatus");

            migrationBuilder.CreateIndex(
                name: "IX_McaFilingExtractions_FilingDocumentId",
                table: "McaFilingExtractions",
                column: "FilingDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_McaFilingExtractions_FilingId",
                table: "McaFilingExtractions",
                column: "FilingId");

            migrationBuilder.CreateIndex(
                name: "IX_McaFilings_BatchId_Srn",
                table: "McaFilings",
                columns: new[] { "BatchId", "Srn" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "McaFilingExtractions");

            migrationBuilder.DropTable(
                name: "McaFilingDocuments");

            migrationBuilder.DropTable(
                name: "McaFilings");

            migrationBuilder.DropTable(
                name: "McaFilingBatches");
        }
    }
}
