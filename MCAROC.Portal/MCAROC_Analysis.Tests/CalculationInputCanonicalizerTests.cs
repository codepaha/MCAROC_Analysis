using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services.CalculationAssurance;

namespace MCAROC_Analysis.Tests;

/// <summary>Covers PR #170 review round 3, point 1: InputHash must depend on the actual resolved
/// <em>values</em> behind a metric's inputs, not just which fields were used — two different companies'
/// figures computed via the identical formula/input-name list must never collide.</summary>
public class CalculationInputCanonicalizerTests
{
    private static DossierModel CreateMinimalDossier(List<FinancialYearData> standalone, CompanyProfile? profile = null)
    {
        var reportDate = new DateTime(2022, 10, 28, 0, 0, 0, DateTimeKind.Utc);
        return new DossierModel(
            RequestId: 1, IngestionRunId: 1, AnalysisRunId: 1,
            Cover: new DossierCover("Test Company", "U12345AB2020PTC123456", "ABCDE1234F",
                new DateOnly(2020, 1, 1), "Active", "Client", reportDate, reportDate),
            Corporate: new DossierCorporate([], [], [], [], [], [], [], null, profile?.PaidUpCapital, [], []),
            Financials: new DossierFinancials(standalone, [], [], [], [], []),
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
        var model = CreateMinimalDossier([]);

        var payloadA = CalculationInputCanonicalizer.BuildCanonicalInputPayload(["CompanyProfile.PaidUpCapital"], model, profileA);
        var payloadB = CalculationInputCanonicalizer.BuildCanonicalInputPayload(["CompanyProfile.PaidUpCapital"], model, profileB);

        Assert.NotEqual(payloadA, payloadB);
    }

    [Fact]
    public void An_input_this_lane_does_not_resolve_is_recorded_as_unresolved_not_silently_omitted()
    {
        var model = CreateMinimalDossier([]);

        var payload = CalculationInputCanonicalizer.BuildCanonicalInputPayload(
            ["FinancialParameter['Employee benefits expense']"], model, null);

        Assert.Contains("unresolved", payload);
    }
}
