using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class AddPhase6DomainData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FinancialYearData_RequestId_IngestionRunId_FinancialYear",
                table: "FinancialYearData");

            migrationBuilder.DropIndex(
                name: "IX_AuditorObservations_RequestId_IngestionRunId",
                table: "AuditorObservations");

            migrationBuilder.AddColumn<string>(
                name: "LatestArrangement",
                table: "RocCharges",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LatestPrimaryFacilityType",
                table: "RocCharges",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LatestSecurityConfidence",
                table: "RocCharges",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LatestSecurityTypesJson",
                table: "RocCharges",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Arrangement",
                table: "RocChargeEvents",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FacilityTypesJson",
                table: "RocChargeEvents",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PrimaryFacilityType",
                table: "RocChargeEvents",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecurityClassificationConfidence",
                table: "RocChargeEvents",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecurityMatchedRulesJson",
                table: "RocChargeEvents",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Basis",
                table: "FinancialYearData",
                type: "nvarchar(15)",
                maxLength: 15,
                nullable: false,
                defaultValue: "Standalone");

            migrationBuilder.AddColumn<string>(
                name: "Basis",
                table: "AuditorObservations",
                type: "nvarchar(15)",
                maxLength: 15,
                nullable: false,
                defaultValue: "Standalone");

            migrationBuilder.CreateTable(
                name: "ChargeSecurityComponents",
                columns: table => new
                {
                    ChargeSecurityComponentId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RocChargeEventId = table.Column<long>(type: "bigint", nullable: false),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SecurityType = table.Column<string>(type: "nvarchar(25)", maxLength: 25, nullable: false),
                    Ranking = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    AssetDescriptionRaw = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsPrimarySecurity = table.Column<bool>(type: "bit", nullable: true),
                    Confidence = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    MatchedRule = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChargeSecurityComponents", x => x.ChargeSecurityComponentId);
                    table.ForeignKey(
                        name: "FK_ChargeSecurityComponents_RocChargeEvents_RocChargeEventId",
                        column: x => x.RocChargeEventId,
                        principalTable: "RocChargeEvents",
                        principalColumn: "ChargeEventId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CompanyStructures",
                columns: table => new
                {
                    CompanyStructureId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PromoterHoldingPercent = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    PublicHoldingPercent = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    TotalShareholders = table.Column<int>(type: "int", nullable: true),
                    PromoterShareholders = table.Column<int>(type: "int", nullable: true),
                    TotalEquityShares = table.Column<long>(type: "bigint", nullable: true),
                    TotalPreferenceShares = table.Column<long>(type: "bigint", nullable: true),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyStructures", x => x.CompanyStructureId);
                });

            migrationBuilder.CreateTable(
                name: "ComplianceRecords",
                columns: table => new
                {
                    ComplianceRecordId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RecordType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RecordDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Source = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Bank = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AmountCrore = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    DefaulterType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComplianceRecords", x => x.ComplianceRecordId);
                });

            migrationBuilder.CreateTable(
                name: "DirectorAssignmentHistories",
                columns: table => new
                {
                    DirectorAssignmentHistoryId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DirectorDin = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DirectorNameRaw = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Designation = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AppointmentDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CessationDate = table.Column<DateOnly>(type: "date", nullable: true),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectorAssignmentHistories", x => x.DirectorAssignmentHistoryId);
                });

            migrationBuilder.CreateTable(
                name: "FinancialParameters",
                columns: table => new
                {
                    FinancialParameterId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ParameterName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FinancialYear = table.Column<int>(type: "int", nullable: true),
                    RawValue = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NumericValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    TextValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Unit = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialParameters", x => x.FinancialParameterId);
                });

            migrationBuilder.CreateTable(
                name: "PeerComparisonMetrics",
                columns: table => new
                {
                    PeerComparisonMetricId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MetricName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FinancialYear = table.Column<int>(type: "int", nullable: false),
                    CompanyValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    PeerMedianValue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    PeerCount = table.Column<int>(type: "int", nullable: true),
                    Position = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    Industry = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Segment = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerComparisonMetrics", x => x.PeerComparisonMetricId);
                });

            migrationBuilder.CreateTable(
                name: "ProprietorshipAssociations",
                columns: table => new
                {
                    ProprietorshipAssociationId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DirectorDin = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DirectorNameRaw = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LegalName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BusinessNames = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Pan = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProprietorshipAssociations", x => x.ProprietorshipAssociationId);
                });

            migrationBuilder.CreateTable(
                name: "RelatedCorporates",
                columns: table => new
                {
                    RelatedCorporateId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FinancialYearEnding = table.Column<DateOnly>(type: "date", nullable: true),
                    EntityNameRaw = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EntityNameNormalized = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Cin = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RelationshipRaw = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RelationshipType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CorporateType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    HoldingPercent = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Location = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PaidUpCapitalCrore = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    SumOfChargesCrore = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    DateOfIncorporation = table.Column<DateOnly>(type: "date", nullable: true),
                    CompanyStatus = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RelatedCorporates", x => x.RelatedCorporateId);
                });

            migrationBuilder.CreateTable(
                name: "SecurityAllotments",
                columns: table => new
                {
                    SecurityAllotmentId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AllotmentDate = table.Column<DateOnly>(type: "date", nullable: true),
                    AllotmentType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    InstrumentType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AmountCrore = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    NumberOfSecurities = table.Column<long>(type: "bigint", nullable: true),
                    NominalValuePerShare = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    PremiumValuePerShare = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityAllotments", x => x.SecurityAllotmentId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialYearData_RequestId_IngestionRunId_FinancialYear_Basis",
                table: "FinancialYearData",
                columns: new[] { "RequestId", "IngestionRunId", "FinancialYear", "Basis" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditorObservations_RequestId_IngestionRunId_Basis",
                table: "AuditorObservations",
                columns: new[] { "RequestId", "IngestionRunId", "Basis" });

            migrationBuilder.CreateIndex(
                name: "IX_ChargeSecurityComponents_RequestId_IngestionRunId",
                table: "ChargeSecurityComponents",
                columns: new[] { "RequestId", "IngestionRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_ChargeSecurityComponents_RocChargeEventId",
                table: "ChargeSecurityComponents",
                column: "RocChargeEventId");

            migrationBuilder.CreateIndex(
                name: "IX_CompanyStructures_RequestId_IngestionRunId",
                table: "CompanyStructures",
                columns: new[] { "RequestId", "IngestionRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_ComplianceRecords_RequestId_IngestionRunId",
                table: "ComplianceRecords",
                columns: new[] { "RequestId", "IngestionRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectorAssignmentHistories_RequestId_IngestionRunId",
                table: "DirectorAssignmentHistories",
                columns: new[] { "RequestId", "IngestionRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialParameters_RequestId_IngestionRunId",
                table: "FinancialParameters",
                columns: new[] { "RequestId", "IngestionRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_PeerComparisonMetrics_RequestId_IngestionRunId",
                table: "PeerComparisonMetrics",
                columns: new[] { "RequestId", "IngestionRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProprietorshipAssociations_RequestId_IngestionRunId",
                table: "ProprietorshipAssociations",
                columns: new[] { "RequestId", "IngestionRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_RelatedCorporates_RequestId_IngestionRunId",
                table: "RelatedCorporates",
                columns: new[] { "RequestId", "IngestionRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityAllotments_RequestId_IngestionRunId",
                table: "SecurityAllotments",
                columns: new[] { "RequestId", "IngestionRunId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChargeSecurityComponents");

            migrationBuilder.DropTable(
                name: "CompanyStructures");

            migrationBuilder.DropTable(
                name: "ComplianceRecords");

            migrationBuilder.DropTable(
                name: "DirectorAssignmentHistories");

            migrationBuilder.DropTable(
                name: "FinancialParameters");

            migrationBuilder.DropTable(
                name: "PeerComparisonMetrics");

            migrationBuilder.DropTable(
                name: "ProprietorshipAssociations");

            migrationBuilder.DropTable(
                name: "RelatedCorporates");

            migrationBuilder.DropTable(
                name: "SecurityAllotments");

            migrationBuilder.DropIndex(
                name: "IX_FinancialYearData_RequestId_IngestionRunId_FinancialYear_Basis",
                table: "FinancialYearData");

            migrationBuilder.DropIndex(
                name: "IX_AuditorObservations_RequestId_IngestionRunId_Basis",
                table: "AuditorObservations");

            migrationBuilder.DropColumn(
                name: "LatestArrangement",
                table: "RocCharges");

            migrationBuilder.DropColumn(
                name: "LatestPrimaryFacilityType",
                table: "RocCharges");

            migrationBuilder.DropColumn(
                name: "LatestSecurityConfidence",
                table: "RocCharges");

            migrationBuilder.DropColumn(
                name: "LatestSecurityTypesJson",
                table: "RocCharges");

            migrationBuilder.DropColumn(
                name: "Arrangement",
                table: "RocChargeEvents");

            migrationBuilder.DropColumn(
                name: "FacilityTypesJson",
                table: "RocChargeEvents");

            migrationBuilder.DropColumn(
                name: "PrimaryFacilityType",
                table: "RocChargeEvents");

            migrationBuilder.DropColumn(
                name: "SecurityClassificationConfidence",
                table: "RocChargeEvents");

            migrationBuilder.DropColumn(
                name: "SecurityMatchedRulesJson",
                table: "RocChargeEvents");

            migrationBuilder.DropColumn(
                name: "Basis",
                table: "FinancialYearData");

            migrationBuilder.DropColumn(
                name: "Basis",
                table: "AuditorObservations");

            migrationBuilder.CreateIndex(
                name: "IX_FinancialYearData_RequestId_IngestionRunId_FinancialYear",
                table: "FinancialYearData",
                columns: new[] { "RequestId", "IngestionRunId", "FinancialYear" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditorObservations_RequestId_IngestionRunId",
                table: "AuditorObservations",
                columns: new[] { "RequestId", "IngestionRunId" });
        }
    }
}
