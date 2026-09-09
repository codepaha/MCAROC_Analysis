using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace MCAROC_Analysis.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditorObservations",
                columns: table => new
                {
                    ObservationId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FinancialYear = table.Column<int>(type: "int", nullable: false),
                    AuditorName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    HasQualificationOrAdverseRemark = table.Column<bool>(type: "bit", nullable: false),
                    ObservationText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditorObservations", x => x.ObservationId);
                });

            migrationBuilder.CreateTable(
                name: "Clients",
                columns: table => new
                {
                    ClientId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ClientCode = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ClientName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ContactPerson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ContactEmail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clients", x => x.ClientId);
                });

            migrationBuilder.CreateTable(
                name: "CompanyProfiles",
                columns: table => new
                {
                    CompanyProfileId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CompanyName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Cin = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Pan = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Llpin = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CompanyStatus = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ComplianceStatus = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IncorporationDate = table.Column<DateOnly>(type: "date", nullable: true),
                    RegisteredAddress = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AuthorisedCapital = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    PaidUpCapital = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Industry = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BusinessActivity = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CompanyProfiles", x => x.CompanyProfileId);
                });

            migrationBuilder.CreateTable(
                name: "DirectorAssociations",
                columns: table => new
                {
                    AssociationId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DirectorDin = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DirectorNameRaw = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConnectedCompanyRaw = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConnectedCompanyNormalized = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConnectedCin = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CorporateType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PaidUpCapitalCrore = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    SumOfChargesCrore = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    DateOfIncorporation = table.Column<DateOnly>(type: "date", nullable: true),
                    CompanyStatus = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ActiveCompliance = table.Column<string>(type: "nvarchar(max)", nullable: true),
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
                    table.PrimaryKey("PK_DirectorAssociations", x => x.AssociationId);
                });

            migrationBuilder.CreateTable(
                name: "Directors",
                columns: table => new
                {
                    DirectorId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Din = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NameRaw = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    NameNormalized = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Designation = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DesignationAppointmentDate = table.Column<DateOnly>(type: "date", nullable: true),
                    OriginalAppointmentDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CessationDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Flags = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Directors", x => x.DirectorId);
                });

            migrationBuilder.CreateTable(
                name: "EpfoContributions",
                columns: table => new
                {
                    EpfoId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EstablishmentId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EstablishmentName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    WorkingStatus = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    WageMonth = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EmployeeCount = table.Column<int>(type: "int", nullable: true),
                    ContributionAmountCrore = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    PaymentDueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PaymentDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PaymentStatus = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EpfoContributions", x => x.EpfoId);
                });

            migrationBuilder.CreateTable(
                name: "FinancialYearData",
                columns: table => new
                {
                    FinancialId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FinancialYear = table.Column<int>(type: "int", nullable: false),
                    Revenue = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    OtherIncome = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Ebitda = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Ebit = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Pbt = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Pat = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    NetWorth = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    CurrentAssets = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    CurrentLiabilities = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Inventory = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    TradeReceivables = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    CashAndBank = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    TradePayables = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    LongTermBorrowings = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    ShortTermBorrowings = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    TotalDebt = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    FinanceCost = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Cfo = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Cfi = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    Cff = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    ShareCapital = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinancialYearData", x => x.FinancialId);
                });

            migrationBuilder.CreateTable(
                name: "GstRegistrations",
                columns: table => new
                {
                    GstId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Gstin = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    State = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RegistrationDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CancellationDate = table.Column<DateOnly>(type: "date", nullable: true),
                    TaxpayerType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TradeName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NatureOfBusinessActivities = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Flags = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GstRegistrations", x => x.GstId);
                });

            migrationBuilder.CreateTable(
                name: "Litigations",
                columns: table => new
                {
                    LitigationId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CaseType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CaseStatus = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CaseCategory = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Court = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Litigants = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CaseNumber = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastHearingDate = table.Column<DateOnly>(type: "date", nullable: true),
                    MatchStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Litigations", x => x.LitigationId);
                });

            migrationBuilder.CreateTable(
                name: "MsmePayments",
                columns: table => new
                {
                    MsmeId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReportingPeriod = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SupplierNameRaw = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SupplierPan = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AmountDueCrore = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MsmePayments", x => x.MsmeId);
                });

            migrationBuilder.CreateTable(
                name: "RocCharges",
                columns: table => new
                {
                    ChargeId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RocChargeNumber = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    LatestChargeHolderRaw = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LatestChargeHolderNormalized = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreationDate = table.Column<DateOnly>(type: "date", nullable: true),
                    CurrentAmount = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    CurrentAmountRaw = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ChargeStatus = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LatestModificationDate = table.Column<DateOnly>(type: "date", nullable: true),
                    SatisfactionDate = table.Column<DateOnly>(type: "date", nullable: true),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RocCharges", x => x.ChargeId);
                });

            migrationBuilder.CreateTable(
                name: "Shareholdings",
                columns: table => new
                {
                    ShareholdingId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FinancialYear = table.Column<int>(type: "int", nullable: false),
                    ShareholderNameRaw = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ShareholderNameNormalized = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ShareholderType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SharesHeld = table.Column<long>(type: "bigint", nullable: true),
                    HoldingPercentage = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: true),
                    IsPromoter = table.Column<bool>(type: "bit", nullable: false),
                    SourceType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shareholdings", x => x.ShareholdingId);
                });

            migrationBuilder.CreateTable(
                name: "Requests",
                columns: table => new
                {
                    RequestId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestNumber = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ClientId = table.Column<long>(type: "bigint", nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CompanyName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Cin = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Llpin = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Pan = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RequestStatus = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AnalysisStartedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AnalysisCompletedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsManualReviewRequired = table.Column<bool>(type: "bit", nullable: false),
                    ManualReviewReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FailureReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LatestCompletedIngestionRunId = table.Column<long>(type: "bigint", nullable: true),
                    HasIngestionWarnings = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Requests", x => x.RequestId);
                    table.ForeignKey(
                        name: "FK_Requests_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "ClientId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GstFilings",
                columns: table => new
                {
                    GstFilingId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GstRegistrationId = table.Column<long>(type: "bigint", nullable: false),
                    Gstin = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReturnType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FinancialYear = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TaxPeriod = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: true),
                    FilingDate = table.Column<DateOnly>(type: "date", nullable: true),
                    FilingStatus = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DelayDays = table.Column<int>(type: "int", nullable: true),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GstFilings", x => x.GstFilingId);
                    table.ForeignKey(
                        name: "FK_GstFilings_GstRegistrations_GstRegistrationId",
                        column: x => x.GstRegistrationId,
                        principalTable: "GstRegistrations",
                        principalColumn: "GstId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RocChargeEvents",
                columns: table => new
                {
                    ChargeEventId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RocChargeId = table.Column<long>(type: "bigint", nullable: false),
                    SerialNumber = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EventDate = table.Column<DateOnly>(type: "date", nullable: true),
                    FilingDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ChargeAmount = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: true),
                    ChargeAmountRaw = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    HolderNameRaw = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    HolderNameNormalized = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PropertyType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NumberOfHolders = table.Column<int>(type: "int", nullable: true),
                    InstrumentDescription = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RateOfInterest = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TermsOfPayment = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PropertyParticulars = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExtentAndOperation = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OtherTerms = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModificationParticulars = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    JointHolding = table.Column<bool>(type: "bit", nullable: true),
                    ConsortiumHolding = table.Column<bool>(type: "bit", nullable: true),
                    MatchConfidence = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    MatchMethod = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceSheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceRowNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RocChargeEvents", x => x.ChargeEventId);
                    table.ForeignKey(
                        name: "FK_RocChargeEvents_RocCharges_RocChargeId",
                        column: x => x.RocChargeId,
                        principalTable: "RocCharges",
                        principalColumn: "ChargeId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IngestionRuns",
                columns: table => new
                {
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    RunNumber = table.Column<int>(type: "int", nullable: false),
                    StartedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    ParserVersion = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SourceRocDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SourceChargeDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    RowsExtracted = table.Column<int>(type: "int", nullable: false),
                    WarningsCount = table.Column<int>(type: "int", nullable: false),
                    ErrorsCount = table.Column<int>(type: "int", nullable: false),
                    FailureReason = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestionRuns", x => x.IngestionRunId);
                    table.ForeignKey(
                        name: "FK_IngestionRuns_Requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "Requests",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RequestDocuments",
                columns: table => new
                {
                    DocumentId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RequestId = table.Column<long>(type: "bigint", nullable: false),
                    DocumentType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    OriginalFileName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StoredFileName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StoragePath = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    FileHash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UploadStatus = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    QuarantineReason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UploadedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UploadedDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequestDocuments", x => x.DocumentId);
                    table.ForeignKey(
                        name: "FK_RequestDocuments_Requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "Requests",
                        principalColumn: "RequestId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IngestionIssues",
                columns: table => new
                {
                    IssueId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    IngestionRunId = table.Column<long>(type: "bigint", nullable: false),
                    Severity = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    DocumentId = table.Column<long>(type: "bigint", nullable: true),
                    SheetName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowNumber = table.Column<int>(type: "int", nullable: true),
                    ParserName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FieldName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RawValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IssueCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestionIssues", x => x.IssueId);
                    table.ForeignKey(
                        name: "FK_IngestionIssues_IngestionRuns_IngestionRunId",
                        column: x => x.IngestionRunId,
                        principalTable: "IngestionRuns",
                        principalColumn: "IngestionRunId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "Clients",
                columns: new[] { "ClientId", "ClientCode", "ClientName", "ContactEmail", "ContactPerson", "CreatedDate", "IsActive", "UpdatedDate" },
                values: new object[,]
                {
                    { 1L, "HDFC", "HDFC Bank", null, null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), true, null },
                    { 2L, "KOTAK", "Kotak Mahindra Bank", null, null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), true, null },
                    { 3L, "AUSFB", "AU Small Finance Bank", null, null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), true, null },
                    { 4L, "FEDERAL", "Federal Bank", null, null, new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), true, null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditorObservations_RequestId_IngestionRunId",
                table: "AuditorObservations",
                columns: new[] { "RequestId", "IngestionRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_Clients_ClientCode",
                table: "Clients",
                column: "ClientCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CompanyProfiles_RequestId_IngestionRunId",
                table: "CompanyProfiles",
                columns: new[] { "RequestId", "IngestionRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectorAssociations_RequestId_IngestionRunId",
                table: "DirectorAssociations",
                columns: new[] { "RequestId", "IngestionRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_Directors_RequestId_IngestionRunId",
                table: "Directors",
                columns: new[] { "RequestId", "IngestionRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_EpfoContributions_RequestId_IngestionRunId",
                table: "EpfoContributions",
                columns: new[] { "RequestId", "IngestionRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_FinancialYearData_RequestId_IngestionRunId_FinancialYear",
                table: "FinancialYearData",
                columns: new[] { "RequestId", "IngestionRunId", "FinancialYear" });

            migrationBuilder.CreateIndex(
                name: "IX_GstFilings_GstRegistrationId",
                table: "GstFilings",
                column: "GstRegistrationId");

            migrationBuilder.CreateIndex(
                name: "IX_GstFilings_RequestId_IngestionRunId",
                table: "GstFilings",
                columns: new[] { "RequestId", "IngestionRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_GstRegistrations_RequestId_IngestionRunId_Gstin",
                table: "GstRegistrations",
                columns: new[] { "RequestId", "IngestionRunId", "Gstin" });

            migrationBuilder.CreateIndex(
                name: "IX_IngestionIssues_IngestionRunId",
                table: "IngestionIssues",
                column: "IngestionRunId");

            migrationBuilder.CreateIndex(
                name: "IX_IngestionRuns_RequestId_RunNumber",
                table: "IngestionRuns",
                columns: new[] { "RequestId", "RunNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Litigations_RequestId_IngestionRunId",
                table: "Litigations",
                columns: new[] { "RequestId", "IngestionRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_MsmePayments_RequestId_IngestionRunId",
                table: "MsmePayments",
                columns: new[] { "RequestId", "IngestionRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_RequestDocuments_RequestId",
                table: "RequestDocuments",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_Requests_ClientId",
                table: "Requests",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_Requests_RequestNumber",
                table: "Requests",
                column: "RequestNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RocChargeEvents_RequestId_IngestionRunId",
                table: "RocChargeEvents",
                columns: new[] { "RequestId", "IngestionRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_RocChargeEvents_RocChargeId",
                table: "RocChargeEvents",
                column: "RocChargeId");

            migrationBuilder.CreateIndex(
                name: "IX_RocCharges_RequestId_IngestionRunId_RocChargeNumber",
                table: "RocCharges",
                columns: new[] { "RequestId", "IngestionRunId", "RocChargeNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_Shareholdings_RequestId_IngestionRunId",
                table: "Shareholdings",
                columns: new[] { "RequestId", "IngestionRunId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditorObservations");

            migrationBuilder.DropTable(
                name: "CompanyProfiles");

            migrationBuilder.DropTable(
                name: "DirectorAssociations");

            migrationBuilder.DropTable(
                name: "Directors");

            migrationBuilder.DropTable(
                name: "EpfoContributions");

            migrationBuilder.DropTable(
                name: "FinancialYearData");

            migrationBuilder.DropTable(
                name: "GstFilings");

            migrationBuilder.DropTable(
                name: "IngestionIssues");

            migrationBuilder.DropTable(
                name: "Litigations");

            migrationBuilder.DropTable(
                name: "MsmePayments");

            migrationBuilder.DropTable(
                name: "RequestDocuments");

            migrationBuilder.DropTable(
                name: "RocChargeEvents");

            migrationBuilder.DropTable(
                name: "Shareholdings");

            migrationBuilder.DropTable(
                name: "GstRegistrations");

            migrationBuilder.DropTable(
                name: "IngestionRuns");

            migrationBuilder.DropTable(
                name: "RocCharges");

            migrationBuilder.DropTable(
                name: "Requests");

            migrationBuilder.DropTable(
                name: "Clients");
        }
    }
}
