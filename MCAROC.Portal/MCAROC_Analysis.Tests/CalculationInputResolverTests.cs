using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services.CalculationAssurance;

namespace MCAROC_Analysis.Tests;

/// <summary>Covers two follow-up review findings on PR #170:
/// <list type="bullet">
/// <item>(round 5) A metric mixing one resolved input with one genuinely-unresolved <em>known</em>
/// source-entity input must not read as fully traceable overall — the earlier fix only corrected
/// InputHash's per-input tracking, not CalculationLedgerEntry.HasUnresolvedProvenance itself, which still
/// used "all inputs unresolved" (not "any") semantics and so could bypass PR #171's
/// ProvenanceCompleteness check on exactly this mixed case.</item>
/// <item>(round 6) "DossierComputations.SecurityTypeLabels" was wrongly excluded as a pure derived
/// concept, when it is really a thin read of the real, persisted RocCharge.LatestSecurityTypesJson field
/// that can change a metric's actual output (see ChargeRegisterMetrics's "Unclassified open charge
/// amount"). "DossierCover.McaDataAsOf" has the identical shape (IngestionRun.CompletedDate) and is fixed
/// alongside it.</item>
/// </list></summary>
public class CalculationInputResolverTests
{
    private static DossierModel CreateMinimalDossier(
        List<FinancialYearData>? standalone = null, List<FinancialParameter>? parameters = null, List<RocCharge>? charges = null)
    {
        var reportDate = new DateTime(2022, 10, 28, 0, 0, 0, DateTimeKind.Utc);
        charges ??= [];
        return new DossierModel(
            RequestId: 1, IngestionRunId: 1, AnalysisRunId: 1,
            Cover: new DossierCover("Test Company", "U12345AB2020PTC123456", "ABCDE1234F",
                new DateOnly(2020, 1, 1), "Active", "Client", reportDate, reportDate),
            Corporate: new DossierCorporate([], [], [], [], [], [], [], null, null, [], []),
            Financials: new DossierFinancials(standalone ?? [], [], [], parameters ?? [], [], []),
            Charges: new DossierCharges(charges, charges, [], [], 0),
            Compliance: new DossierCompliance([], [], [], [], [], [], []),
            Litigation: new DossierLitigation([], [], new Dictionary<long, LitigationRole>()),
            ExecSummary: new DossierExecSummary(ReviewPriority.Medium, 0, 0, 0, 0, [], null, []),
            SourceSheets: [],
            SourceCoverage: SheetCoverage.Empty,
            Metrics: []);
    }

    private static FinancialYearData Year(int year, decimal revenue) =>
        new() { FinancialId = year, FinancialYear = year, Basis = FinancialBasis.Standalone, Revenue = revenue };

    private static RocCharge Charge(long id, string securityTypesJson) => new()
    {
        ChargeId = id, RocChargeNumber = $"C{id}", ChargeStatus = "Open",
        LatestChargeHolderRaw = "BANK", LatestChargeHolderNormalized = "BANK",
        CurrentAmount = 100m, LatestSecurityTypesJson = securityTypesJson
    };

    private static IngestionRun Run(long id, DateTime completedDate) =>
        new() { IngestionRunId = id, RequestId = 1, RunNumber = 1, StartedDate = completedDate, CompletedDate = completedDate };

    [Fact]
    public void All_inputs_resolving_is_AllKnownInputsResolved()
    {
        var model = CreateMinimalDossier([Year(2025, 400m)]);

        Assert.True(CalculationInputResolver.AllKnownInputsResolved(["FinancialYearData.Revenue"], model, null, null));
    }

    [Fact]
    public void One_resolved_and_one_genuinely_unresolved_known_entity_input_is_NOT_AllKnownInputsResolved()
    {
        // "Employee cost % of revenue"-shaped inputs: Revenue resolves, but no FinancialParameter row
        // exists for "Employee benefits expense" this year — the whole metric must read as unresolved,
        // not "fine because Revenue resolved."
        var model = CreateMinimalDossier([Year(2025, 400m)]); // no FinancialParameter rows at all

        var result = CalculationInputResolver.AllKnownInputsResolved(
            ["FinancialParameter['Employee benefits expense']", "FinancialYearData.Revenue"], model, null, null);

        Assert.False(result);
    }

    [Fact]
    public void A_resolved_known_input_plus_a_genuinely_unresolvable_synthetic_input_IS_AllKnownInputsResolved()
    {
        // A made-up entity name standing in for a genuinely derived/computed concept with no backing
        // field at all — this lane has not found a real example left in the 3 covered MetricGroups (both
        // SecurityTypeLabels and McaDataAsOf turned out to have real backing fields, fixed below), but the
        // exclusion mechanism itself must still work for whatever the next one turns out to be.
        var model = CreateMinimalDossier([Year(2025, 400m)]);

        var result = CalculationInputResolver.AllKnownInputsResolved(
            ["SomeFutureHelper.PurelyComputedLabel", "FinancialYearData.Revenue"], model, null, null);

        Assert.True(result);
    }

    [Fact]
    public void SecurityTypeLabels_resolves_to_the_real_RocCharge_LatestSecurityTypesJson_value()
    {
        var modelA = CreateMinimalDossier(charges: [Charge(1, "[\"CurrentAssets\"]")]);
        var modelB = CreateMinimalDossier(charges: [Charge(1, "[\"MovableFixedAssets\"]")]);

        Assert.True(CalculationInputResolver.AllKnownInputsResolved(["DossierComputations.SecurityTypeLabels"], modelA, null, null));

        var payloadA = CalculationInputCanonicalizer.BuildCanonicalInputPayload(["DossierComputations.SecurityTypeLabels"], modelA, null);
        var payloadB = CalculationInputCanonicalizer.BuildCanonicalInputPayload(["DossierComputations.SecurityTypeLabels"], modelB, null);
        Assert.NotEqual(payloadA, payloadB); // a security-type-only change must change the hash
    }

    [Fact]
    public void SecurityTypeLabels_with_no_charges_at_all_is_a_genuine_unresolved_gap()
    {
        var model = CreateMinimalDossier(); // no RocCharge rows at all

        Assert.False(CalculationInputResolver.AllKnownInputsResolved(["DossierComputations.SecurityTypeLabels"], model, null, null));
    }

    [Fact]
    public void McaDataAsOf_resolves_to_the_real_IngestionRun_CompletedDate_value()
    {
        var model = CreateMinimalDossier();
        var runA = Run(1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var runB = Run(1, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.True(CalculationInputResolver.AllKnownInputsResolved(["DossierCover.McaDataAsOf"], model, null, runA));

        var payloadA = CalculationInputCanonicalizer.BuildCanonicalInputPayload(["DossierCover.McaDataAsOf"], model, null, runA);
        var payloadB = CalculationInputCanonicalizer.BuildCanonicalInputPayload(["DossierCover.McaDataAsOf"], model, null, runB);
        Assert.NotEqual(payloadA, payloadB); // a re-ingestion's new CompletedDate must change the hash
    }

    [Fact]
    public void McaDataAsOf_with_no_ingestion_run_available_is_a_genuine_unresolved_gap()
    {
        var model = CreateMinimalDossier();

        Assert.False(CalculationInputResolver.AllKnownInputsResolved(["DossierCover.McaDataAsOf"], model, null, null));
    }

    [Fact]
    public void IsKnownSourceEntityInput_recognizes_aliased_computed_inputs_as_known()
    {
        Assert.True(CalculationInputResolver.IsKnownSourceEntityInput("FinancialYearData.Revenue"));
        Assert.True(CalculationInputResolver.IsKnownSourceEntityInput("FinancialParameter['Employee benefits expense']"));
        Assert.True(CalculationInputResolver.IsKnownSourceEntityInput("DossierComputations.SecurityTypeLabels"));
        Assert.True(CalculationInputResolver.IsKnownSourceEntityInput("DossierCover.McaDataAsOf"));
        Assert.False(CalculationInputResolver.IsKnownSourceEntityInput("SomeFutureHelper.PurelyComputedLabel"));
    }
}
