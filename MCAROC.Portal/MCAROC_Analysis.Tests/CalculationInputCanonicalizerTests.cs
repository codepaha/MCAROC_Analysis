using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services.CalculationAssurance;

namespace MCAROC_Analysis.Tests;

/// <summary>Covers PR #170 review round 3, point 1 (InputHash must depend on actual resolved values, not
/// just field names) and the follow-up review's finding that FinancialParameter/FinancialFact — real
/// inputs FinancialTrendMetrics uses (e.g. "FinancialParameter['Employee benefits expense']",
/// "FinancialFact['Cost of materials consumed']") — were entirely unhandled, so a metric mixing one of
/// them with a resolved FinancialYearData input could hash/cite as if fully traceable while silently
/// ignoring the unresolved half.</summary>
public class CalculationInputCanonicalizerTests
{
    private static DossierModel CreateMinimalDossier(
        List<FinancialYearData>? standalone = null, CompanyProfile? profile = null,
        List<FinancialParameter>? parameters = null, List<FinancialFact>? facts = null)
    {
        var reportDate = new DateTime(2022, 10, 28, 0, 0, 0, DateTimeKind.Utc);
        return new DossierModel(
            RequestId: 1, IngestionRunId: 1, AnalysisRunId: 1,
            Cover: new DossierCover("Test Company", "U12345AB2020PTC123456", "ABCDE1234F",
                new DateOnly(2020, 1, 1), "Active", "Client", reportDate, reportDate),
            Corporate: new DossierCorporate([], [], [], [], [], [], [], null, profile?.PaidUpCapital, [], []),
            Financials: new DossierFinancials(standalone ?? [], [], facts ?? [], parameters ?? [], [], []),
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

    private static FinancialParameter Parameter(long id, int year, string name, decimal value) =>
        new() { FinancialParameterId = id, FinancialYear = year, ParameterName = name, RawValue = value.ToString(), NumericValue = value };

    private static FinancialFact Fact(long id, int year, string label, decimal value) => new()
    {
        FinancialFactId = id, FinancialYear = year, Basis = FinancialBasis.Standalone,
        Section = FinancialStatementSection.ProfitAndLoss, Label = label, RawValue = value.ToString(), NumericValue = value
    };

    [Fact]
    public void Changing_the_underlying_source_value_changes_the_canonical_payload()
    {
        var modelA = CreateMinimalDossier([Year(2024, 100m), Year(2025, 120m)]);
        var modelB = CreateMinimalDossier([Year(2024, 100m), Year(2025, 999m)]); // only 2025's Revenue differs

        var payloadA = CalculationInputCanonicalizer.BuildCanonicalInputPayload(["FinancialYearData.Revenue"], modelA, null);
        var payloadB = CalculationInputCanonicalizer.BuildCanonicalInputPayload(["FinancialYearData.Revenue"], modelB, null);

        Assert.NotEqual(payloadA, payloadB);
    }

    [Fact]
    public void Identical_source_values_produce_an_identical_canonical_payload()
    {
        var modelA = CreateMinimalDossier([Year(2024, 100m), Year(2025, 120m)]);
        var modelB = CreateMinimalDossier([Year(2024, 100m), Year(2025, 120m)]);

        var payloadA = CalculationInputCanonicalizer.BuildCanonicalInputPayload(["FinancialYearData.Revenue"], modelA, null);
        var payloadB = CalculationInputCanonicalizer.BuildCanonicalInputPayload(["FinancialYearData.Revenue"], modelB, null);

        Assert.Equal(payloadA, payloadB);
    }

    [Fact]
    public void Same_input_names_over_different_company_profile_values_do_not_collide()
    {
        var profileA = new CompanyProfile { CompanyProfileId = 1, PaidUpCapital = 80m };
        var profileB = new CompanyProfile { CompanyProfileId = 1, PaidUpCapital = 999m };
        var model = CreateMinimalDossier();

        var payloadA = CalculationInputCanonicalizer.BuildCanonicalInputPayload(["CompanyProfile.PaidUpCapital"], model, profileA);
        var payloadB = CalculationInputCanonicalizer.BuildCanonicalInputPayload(["CompanyProfile.PaidUpCapital"], model, profileB);

        Assert.NotEqual(payloadA, payloadB);
    }

    [Fact]
    public void An_input_naming_a_derived_non_source_concept_is_recorded_as_unresolved_not_silently_omitted()
    {
        // "DossierComputations.SecurityTypeLabels" is a real input ChargeRegisterMetrics uses, but it
        // names a computed classification, not a source row — correctly unresolvable, not a bug.
        var model = CreateMinimalDossier();

        var payload = CalculationInputCanonicalizer.BuildCanonicalInputPayload(
            ["DossierComputations.SecurityTypeLabels"], model, null);

        Assert.Contains("unresolved", payload);
    }

    [Fact]
    public void Changing_a_FinancialParameter_value_changes_the_hash()
    {
        var modelA = CreateMinimalDossier([Year(2025, 400m)], parameters: [Parameter(1, 2025, "Employee benefits expense", 50m)]);
        var modelB = CreateMinimalDossier([Year(2025, 400m)], parameters: [Parameter(1, 2025, "Employee benefits expense", 999m)]);

        var payloadA = CalculationInputCanonicalizer.BuildCanonicalInputPayload(["FinancialParameter['Employee benefits expense']"], modelA, null);
        var payloadB = CalculationInputCanonicalizer.BuildCanonicalInputPayload(["FinancialParameter['Employee benefits expense']"], modelB, null);

        Assert.NotEqual(payloadA, payloadB);
        Assert.DoesNotContain("unresolved", payloadA);
    }

    [Fact]
    public void Changing_a_FinancialFact_value_changes_the_hash()
    {
        var modelA = CreateMinimalDossier([Year(2025, 400m)], facts: [Fact(1, 2025, "Cost of Materials Consumed", 120m)]);
        var modelB = CreateMinimalDossier([Year(2025, 400m)], facts: [Fact(1, 2025, "Cost of Materials Consumed", 777m)]);

        var payloadA = CalculationInputCanonicalizer.BuildCanonicalInputPayload(["FinancialFact['Cost of materials consumed']"], modelA, null);
        var payloadB = CalculationInputCanonicalizer.BuildCanonicalInputPayload(["FinancialFact['Cost of materials consumed']"], modelB, null);

        Assert.NotEqual(payloadA, payloadB);
        Assert.DoesNotContain("unresolved", payloadA);

        // Label matching is case/whitespace-normalized, same as DossierComputations.Metrics.LookupFact's
        // own approvedLabel matching — the bracket text and the sheet's stored Label case differ for real.
        Assert.Contains("120", payloadA);
    }

    [Fact]
    public void A_mixed_metric_with_one_resolved_and_one_unresolved_input_still_marks_the_unresolved_half_explicitly()
    {
        // "Employee cost % of revenue" ships as ["FinancialParameter['Employee benefits expense']",
        // "FinancialYearData.Revenue"] — before this fix, FinancialYearData.Revenue alone resolving to a
        // non-empty ref list masked the FinancialParameter half being completely unhandled.
        var model = CreateMinimalDossier([Year(2025, 400m)]); // no FinancialParameter rows at all

        var payload = CalculationInputCanonicalizer.BuildCanonicalInputPayload(
            ["FinancialParameter['Employee benefits expense']", "FinancialYearData.Revenue"], model, null);

        Assert.Contains("FinancialParameter['Employee benefits expense']=unresolved", payload);
        Assert.Contains("FinancialYearData", payload);
        Assert.Contains("400", payload);
    }
}
