using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;

namespace MCAROC_Analysis.Tests.Checks;

/// <summary>Shared, DB-free DossierModel/CalculationLedgerEntry builders for the deterministic-check unit
/// tests — same minimal-model pattern as FinancialTrendMetricsTests/CalculationInputCanonicalizerTests.</summary>
internal static class CalculationCheckTestSupport
{
    public static DossierModel CreateMinimalDossier(
        List<FinancialYearData>? standalone = null, List<RocCharge>? openCharges = null, decimal? paidUpCapital = null)
    {
        standalone ??= [];
        openCharges ??= [];
        var reportDate = new DateTime(2022, 10, 28, 0, 0, 0, DateTimeKind.Utc);
        return new DossierModel(
            RequestId: 1, IngestionRunId: 1, AnalysisRunId: 1,
            Cover: new DossierCover("Test Company", "U12345AB2020PTC123456", "ABCDE1234F",
                new DateOnly(2020, 1, 1), "Active", "Client", reportDate, reportDate),
            Corporate: new DossierCorporate([], [], [], [], [], [], [], null, paidUpCapital, [], []),
            Financials: new DossierFinancials(standalone, [], [], [], [], []),
            Charges: new DossierCharges(openCharges, openCharges, [], [], 0),
            Compliance: new DossierCompliance([], [], [], [], [], [], []),
            Litigation: new DossierLitigation([], [], new Dictionary<long, LitigationRole>()),
            ExecSummary: new DossierExecSummary(ReviewPriority.Medium, 0, 0, 0, 0, [], null, []),
            SourceSheets: [],
            SourceCoverage: SheetCoverage.Empty,
            Metrics: []);
    }

    public static FinancialYearData Year(int financialId, int year, decimal? netWorth = null, decimal? shareCapital = null) =>
        new() { FinancialId = financialId, FinancialYear = year, Basis = FinancialBasis.Standalone, NetWorth = netWorth, ShareCapital = shareCapital };

    public static RocCharge OpenCharge(long chargeId, string holder, decimal amount) => new()
    {
        ChargeId = chargeId, RocChargeNumber = $"C{chargeId}", ChargeStatus = "Open",
        LatestChargeHolderRaw = holder, LatestChargeHolderNormalized = holder, CurrentAmount = amount
    };

    public static CalculationLedgerEntry LedgerEntry(long id, string calculationKey, decimal? value, string? insufficiencyReason = null) => new()
    {
        CalculationLedgerEntryId = id,
        CalculationKey = calculationKey,
        MetricLabel = calculationKey,
        Period = "FY2025",
        Unit = MetricUnit.Percent,
        ValueNumeric = value,
        InsufficiencyReason = insufficiencyReason,
        InputsJson = "[]",
        SourceRowRefsJson = "[]",
        InputHash = new string('a', 64),
        OutputHash = new string('b', 64),
        CreatedUtc = DateTime.UtcNow
    };
}
