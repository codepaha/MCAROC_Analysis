using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddLitigationLiveProjection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "CaseObservationIds",
                schema: "litigation");

            migrationBuilder.CreateTable(
                name: "CaseLive",
                schema: "litigation",
                columns: table => new
                {
                    CaseId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Stage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Court = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FilingDate = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastHearingDate = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NextHearingDate = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DecisionDate = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DisposalNature = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SuitAmount = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    HearingCount = table.Column<int>(type: "int", nullable: false),
                    LastRetrievedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastObservationId = table.Column<long>(type: "bigint", nullable: true),
                    FieldSourcesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    HeldChange = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseLive", x => x.CaseId);
                    table.ForeignKey(
                        name: "FK_CaseLive_Cases_CaseId",
                        column: x => x.CaseId,
                        principalSchema: "litigation",
                        principalTable: "Cases",
                        principalColumn: "CaseId");
                });

            migrationBuilder.CreateTable(
                name: "CaseObservations",
                schema: "litigation",
                columns: table => new
                {
                    ObservationId = table.Column<long>(type: "bigint", nullable: false, defaultValueSql: "NEXT VALUE FOR [litigation].[CaseObservationIds]"),
                    CaseId = table.Column<long>(type: "bigint", nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    Verified = table.Column<bool>(type: "bit", nullable: false),
                    ParserVersion = table.Column<int>(type: "int", nullable: false),
                    RetrievedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EventWatermark = table.Column<long>(type: "bigint", nullable: true),
                    SourceUpdatedUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AppliedAs = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseObservations", x => x.ObservationId);
                    table.ForeignKey(
                        name: "FK_CaseObservations_Cases_CaseId",
                        column: x => x.CaseId,
                        principalSchema: "litigation",
                        principalTable: "Cases",
                        principalColumn: "CaseId");
                });

            migrationBuilder.CreateIndex(
                name: "IX_CaseObservations_CaseId_RetrievedUtc",
                schema: "litigation",
                table: "CaseObservations",
                columns: new[] { "CaseId", "RetrievedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CaseLive",
                schema: "litigation");

            migrationBuilder.DropTable(
                name: "CaseObservations",
                schema: "litigation");

            migrationBuilder.DropSequence(
                name: "CaseObservationIds",
                schema: "litigation");
        }
    }
}
