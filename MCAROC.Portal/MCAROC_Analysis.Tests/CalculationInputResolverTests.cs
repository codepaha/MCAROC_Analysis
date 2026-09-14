using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services.CalculationAssurance;

namespace MCAROC_Analysis.Tests;

/// <summary>Covers a follow-up review finding on PR #170: a metric mixing one resolved input with one
/// genuinely-unresolved <em>known</em> source-entity input must not read as fully traceable overall —
/// the earlier fix only corrected InputHash's per-input tracking, not the CalculationLedgerEntry.
/// HasUnresolvedProvenance flag itself, which still used "all inputs unresolved" (not "any") semantics
/// and so could bypass PR #171's ProvenanceCompleteness check on exactly this mixed case.</summary>
public class CalculationInputResolverTests
{
    private static DossierModel CreateMinimalDossier(List<FinancialYearData>? standalone = null, List<FinancialParameter>? parameters = null)
    {
        var reportDate = new DateTime(2022, 10, 28, 0, 0, 0, DateTimeKind.Utc);
        return new DossierModel(
            RequestId: 1, IngestionRunId: 1, AnalysisRunId: 1,
            Cover: new DossierCover("Test Company", "U12345AB2020PTC123456", "ABCDE1234F",
                new DateOnly(2020, 1, 1), "Active", "Client", reportDate, reportDate),
            Corporate: new DossierCorporate([], [], [], [], [], [], [], null, null, [], []),
            Financials: new DossierFinancials(standalone ?? [], [], [], parameters ?? [], [], []),
            Charges: new DossierCharges([], [], [], [], 0),
            Compliance: new DossierCompliance([], [], [], [], [], [], []),
            Litigation: new DossierLitigation([], [], new Dictionary<long, LitigationRole>()),
            ExecSummary: new DossierExecSummary(ReviewPriority.Medium, 0, 0, 0, 0, [], null, []),
            SourceSheets: [],
            SourceCoverage: SheetCoverage.Empty,
            Metrics: []);
    }

    private static FinancialYearData Year(int year, decimal revenue) =>
        new() { FinancialId = year, FinancialYear = year, Basis = FinancialBasis.Standalone, Revenue = revenue };

    [Fact]
    public void All_inputs_resolving_is_AllKnownInputsResolved()
    {
        var model = CreateMinimalDossier([Year(2025, 400m)]);

        Assert.True(CalculationInputResolver.AllKnownInputsResolved(["FinancialYearData.Revenue"], model, null));
    }

    [Fact]
    public void One_resolved_and_one_genuinely_unresolved_known_entity_input_is_NOT_AllKnownInputsResolved()
    {
        // "Employee cost % of revenue"-shaped inputs: Revenue resolves, but no FinancialParameter row
        // exists for "Employee benefits expense" this year — the whole metric must read as unresolved,
        // not "fine because Revenue resolved."
        var model = CreateMinimalDossier([Year(2025, 400m)]); // no FinancialParameter rows at all

        var result = CalculationInputResolver.AllKnownInputsResolved(
            ["FinancialParameter['Employee benefits expense']", "FinancialYearData.Revenue"], model, null);

        Assert.False(result);
    }

    [Fact]
    public void A_resolved_known_input_plus_an_unresolvable_derived_concept_input_IS_AllKnownInputsResolved()
    {
        // "Total open charge amount"-shaped inputs: "DossierComputations.SecurityTypeLabels" is a
        // computed classification, never a source row to begin with — it must not count against
        // completeness just because it can never resolve.
        var model = CreateMinimalDossier([Year(2025, 400m)]);

        var result = CalculationInputResolver.AllKnownInputsResolved(
            ["DossierComputations.SecurityTypeLabels", "FinancialYearData.Revenue"], model, null);

        Assert.True(result);
    }

    [Fact]
    public void IsKnownSourceEntityInput_distinguishes_real_entity_types_from_derived_concepts()
    {
        Assert.True(CalculationInputResolver.IsKnownSourceEntityInput("FinancialYearData.Revenue"));
        Assert.True(CalculationInputResolver.IsKnownSourceEntityInput("FinancialParameter['Employee benefits expense']"));
        Assert.False(CalculationInputResolver.IsKnownSourceEntityInput("DossierComputations.SecurityTypeLabels"));
        Assert.False(CalculationInputResolver.IsKnownSourceEntityInput("DossierCover.McaDataAsOf"));
    }
}
