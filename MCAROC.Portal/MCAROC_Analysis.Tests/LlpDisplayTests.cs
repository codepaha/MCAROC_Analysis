using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Analysis.Rules;
using MCAROC_Analysis.Services.Chat;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Tests.Checks;
using MCAROC_Analysis.Tests.Dossier;
using Microsoft.EntityFrameworkCore;
using UglyToad.PdfPig;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#539 PR 3: an LLP's request page, dossier and chat speak of its LLPIN, partners' contribution, partners' funds and designated
/// partners, show its partners' contributions, and leave out what an LLP's export never has (shareholding and the other company-only sheets),
/// instead of empty company sections and "not in this upload" notes.</summary>
public class LlpDisplayTests : IAsyncLifetime
{
    [Fact]
    public void The_wording_follows_the_entity_type()
    {
        Assert.Equal(("CIN", "Share Capital", "Total Equity", "Directors"),
            (EntityWording.Company.Identifier, EntityWording.Company.Capital, EntityWording.Company.NetWorth, EntityWording.Company.Directors));
        var llp = EntityWording.For(EntityType.LLP);
        Assert.Equal(("LLPIN", "Partners' contribution", "Partners' funds", "Designated partners", "designated partner"),
            (llp.Identifier, llp.Capital, llp.NetWorth, llp.Directors, llp.Director));
        Assert.Same(EntityWording.Company, EntityWording.For(EntityType.Company));
    }

    [Fact]
    public void An_LLPs_coverage_leaves_out_the_sheets_its_export_never_has()
    {
        var absent = new[] { "Director Shareholding", "Shareholding More Than 5%", "Securities Allotment", "Related Corporates", "EPFO Establishments" };
        var run = new IngestionRun { AbsentOptionalSheetsJson = System.Text.Json.JsonSerializer.Serialize(absent) };

        var company = SheetCoverage.From(run);
        var llp = SheetCoverage.From(run, isLlp: true);

        Assert.Equal(SheetAliases.TrackedOptionalSheets.Count, company.TotalOptionalSheets);
        Assert.Equal(5, company.AbsentSheets.Count);
        Assert.Equal(SheetAliases.TrackedOptionalSheets.Count - SheetAliases.CompanyOnlySheets.Count, llp.TotalOptionalSheets);
        Assert.Equal(["EPFO Establishments"], llp.AbsentSheets);   // still a sheet an LLP can have
        Assert.False(llp.WasAbsent(SheetAliases.DirectorShareholding));
    }

    private static DossierModel Llp(DossierModel model) => model with { Cover = model.Cover with { IsLlp = true, Cin = "ZZZ-1234" } };

    [Fact]
    public void An_LLPs_metrics_have_no_shareholding_and_measure_charges_against_its_partners_contribution()
    {
        var company = CalculationCheckTestSupport.CreateMinimalDossier(
            openCharges: [CalculationCheckTestSupport.OpenCharge(1, "BANK", 3m)], paidUpCapital: 2m);
        var llp = Llp(company);

        Assert.Contains(DossierComputations.BuildMetricGroups(company), g => g.Title == "Shareholding");
        var groups = DossierComputations.BuildMetricGroups(llp);
        Assert.DoesNotContain(groups, g => g.Title == "Shareholding");
        Assert.All(groups, g => Assert.Contains(g.Metrics, m => m.HasValue));   // nothing but "not reported" is dropped for an LLP

        var ratio = DossierComputations.ChargeRegisterMetrics(llp).Metrics.Single(m => m.Label.StartsWith("Charge-to-", StringComparison.Ordinal));
        Assert.Equal(("Charge-to-partners'-contribution ratio", 1.5m), (ratio.Label, ratio.Value));
        Assert.Equal("MCA master-data partners' contribution vs statement's contribution received",
            DossierComputations.CapitalReconciliationMetrics(llp).Metrics.Single().Label);
    }

    [Fact]
    public void An_LLPs_directors_group_is_headed_designated_partners_but_keeps_its_key()
    {
        var group = new MetricGroup("Directors", [MetricResult.Ok("Average board tenure", 3m, MetricUnit.Years, "now", "Director.OriginalAppointmentDate")]);
        Assert.Equal("Directors", group.Heading);
        var llp = group with { Label = "Designated partners" };
        Assert.Equal(("Directors", "Designated partners"), (llp.Title, llp.Heading));   // tabs still find it by its key
    }

    [Fact]
    public void An_LLP_is_not_reported_short_of_a_promoter_holding_or_a_compliance_status_it_cannot_have()
    {
        var llp = AnalysisTestHelpers.BuildContext(companyProfile: new CompanyProfile { CompanyName = "SAMPLE FARMS LLP", CompanyStatus = "Active" });
        llp.Request.EntityType = EntityType.LLP;
        Assert.Empty(OwnershipRules.Evaluate(llp, RuleThresholds.Default));
        Assert.DoesNotContain(CompanyProfileRules.Evaluate(llp, RuleThresholds.Default), o => o.Code == CompanyProfileRules.ComplianceNonCompliantCode);

        // A company is told, as before; and an LLP that does report a compliance status is assessed.
        var company = AnalysisTestHelpers.BuildContext(companyProfile: new CompanyProfile { CompanyName = "SAMPLE LIMITED", CompanyStatus = "Active" });
        Assert.Contains(OwnershipRules.Evaluate(company, RuleThresholds.Default), o => o.Code == OwnershipRules.PromoterHoldingDeclineCode);
        Assert.Contains(CompanyProfileRules.Evaluate(company, RuleThresholds.Default), o => o.Code == CompanyProfileRules.ComplianceNonCompliantCode);
        var reporting = AnalysisTestHelpers.BuildContext(companyProfile: new CompanyProfile { CompanyName = "SAMPLE FARMS LLP", CompanyStatus = "Active", ComplianceStatus = "ACTIVE non-compliant" });
        reporting.Request.EntityType = EntityType.LLP;
        Assert.Contains(CompanyProfileRules.Evaluate(reporting, RuleThresholds.Default), o => o.Code == CompanyProfileRules.ComplianceNonCompliantCode);
    }

    // ---- the dossier PDF ----------------------------------------------------------------------------------------------------------------------

    private static AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = Ctx();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string WebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "MCAROC.Portal"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "MCAROC.Portal", "MCAROC_Analysis", "wwwroot");
    }

    [SkippableFact]
    public async Task An_LLPs_dossier_says_LLPIN_partners_and_contribution_and_lists_its_contributors()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "PdfPig text extraction from SkiaSharp subset fonts is unreliable on Linux; covered by the windows-tests job.");

        await using var seed = Ctx();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);
        await using var db = Ctx();
        var company = (await new DossierAssembler(db).BuildAsync(requestId))!;
        company.Profile!.LlpObligationOfContributionCrore = 2.0m;
        company.Profile.LlpContributionReceivedCrore = 1.75m;
        var llp = Llp(company) with
        {
            Corporate = company.Corporate with
            {
                Structure = new CompanyStructure { LlpPartners = 1, LlpDesignatedPartners = 2 },
                LlpContributions =
                [
                    new LlpContribution { Name = "PARTNER ONE", Kind = LlpContributorKind.Partner, Designation = "Designated Partner", ObligationOfContributionCrore = 1.25m, ContributionReceivedCrore = 1.0m, AsOnDate = new DateOnly(2026, 3, 31) },
                    new LlpContribution { Name = "SAMPLE HOLDINGS PRIVATE LIMITED", Kind = LlpContributorKind.BodyCorporate, NomineeName = "NOMINEE PERSON", ObligationOfContributionCrore = 0.75m, ContributionReceivedCrore = 0.75m },
                ],
            },
        };

        var pdf = new DossierPdfRenderer(WebRoot()).Render(llp, DossierVariant.Executive);
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var text = string.Concat(doc.GetPages().Select(p => p.Text));

        Assert.Contains("LLPIN", text);
        Assert.Contains("ZZZ-1234", text);
        Assert.DoesNotContain("CIN ZZZ-1234", text);
        Assert.Contains("Partners & Governance", text);
        Assert.Contains("Partners' contributions", text);
        Assert.Contains("PARTNER ONE", text);
        Assert.Contains("(nominee NOMINEE", text);   // the role cell wraps there, and the extracted text drops the space at a wrap
        Assert.Contains("Contribution received", text);
        Assert.Contains("Designated partners", text);
        Assert.DoesNotContain("Paid-up capital", text);
        Assert.DoesNotContain("Authorised capital", text);
        Assert.DoesNotContain("Directors register", text);
    }

    // ---- the chat ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_LLPs_chat_facts_give_its_LLPIN_contribution_and_contributors()
    {
        long requestId;
        await using (var db = Ctx())
        {
            var request = new McaRequest
            {
                Client = new Client { ClientCode = "LD" + Guid.NewGuid().ToString("N")[..8], ClientName = "LLP display", CreatedDate = DateTime.UtcNow },
                EntityType = EntityType.LLP, CompanyName = "SAMPLE FARMS LLP", Llpin = "ZZZ-1234", Cin = "ZZZ-1234",
                RequestNumber = $"LD-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
            };
            db.Requests.Add(request);
            await db.SaveChangesAsync();
            var run = new IngestionRun { RequestId = request.RequestId, RunNumber = 1, StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow, Status = IngestionRunStatus.CompletedClean };
            db.IngestionRuns.Add(run);
            await db.SaveChangesAsync();
            request.LatestCompletedIngestionRunId = run.IngestionRunId;
            db.CompanyProfiles.Add(new CompanyProfile { RequestId = request.RequestId, IngestionRunId = run.IngestionRunId, CompanyName = "SAMPLE FARMS LLP", Llpin = "ZZZ-1234", CompanyStatus = "Active", LlpObligationOfContributionCrore = 2.0m, LlpContributionReceivedCrore = 1.75m });
            db.CompanyStructures.Add(new CompanyStructure { RequestId = request.RequestId, IngestionRunId = run.IngestionRunId, LlpPartners = 1, LlpDesignatedPartners = 2 });
            db.LlpContributions.Add(new LlpContribution { RequestId = request.RequestId, IngestionRunId = run.IngestionRunId, Name = "PARTNER ONE", Kind = LlpContributorKind.Partner, Designation = "Designated Partner", ObligationOfContributionCrore = 1.25m, ContributionReceivedCrore = 1.0m, AsOnDate = new DateOnly(2026, 3, 31), DisplayOrder = 1 });
            db.Directors.Add(new Director { RequestId = request.RequestId, IngestionRunId = run.IngestionRunId, Din = "1234567", NameRaw = "PARTNER ONE", Designation = "Designated Partner" });
            await db.SaveChangesAsync();
            requestId = request.RequestId;
        }

        await using var read = Ctx();
        var facts = await new StructuredFactsProvider(read).BuildDigestAsync(requestId, QuestionHintExtractor.Extract("Who are the partners and what did they contribute?", [], []), default);

        var profile = Assert.Single(facts, f => f.DomainKey == "CompanyProfile" && f.Text.StartsWith("SAMPLE FARMS LLP", StringComparison.Ordinal));
        Assert.Contains("LLPIN: ZZZ-1234", profile.Text);
        Assert.Contains("Partners' contribution: obligation ₹2.0 Cr, received ₹1.75 Cr", profile.Text.Replace("2.0000", "2.0").Replace("1.7500", "1.75"));
        Assert.DoesNotContain("CIN:", profile.Text);
        Assert.Contains(facts, f => f.Text.StartsWith("2 designated partner(s), 1 other partner(s). Contributions: PARTNER ONE (Designated Partner): received", StringComparison.Ordinal));
        Assert.Contains(facts, f => f.Text.Contains("designated partner(s) on record", StringComparison.Ordinal) || f.Text.StartsWith("Designated partner: PARTNER ONE", StringComparison.Ordinal));
    }
}
