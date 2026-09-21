using System.Text.Json;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Analysis.Rules;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Services.Excel;
using Microsoft.EntityFrameworkCore;
using UglyToad.PdfPig;

namespace MCAROC_Analysis.Tests.Dossier;

/// <summary>Renders the dossier PDF for the seed graph and asserts the document shape — the sections are
/// present and ordered, and (the hard rule) there is no risk score anywhere.</summary>
public class DossierPdfComposerTests : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var db = DossierGoldenMasterTests.CreateContext();
        await global::MCAROC_Analysis.Tests.TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string WebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "MCAROC.Portal")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "MCAROC.Portal", "MCAROC_Analysis", "wwwroot");
    }

    private static string TextOf(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return string.Concat(doc.GetPages().Select(p => p.Text));
    }

    /// <summary>Smoke test: the dossier renders to a non-empty multi-page PDF without a SkiaSharp
    /// native failure. The content assertions live in <see cref="Renders_the_dossier_with_no_risk_score"/>,
    /// which is Windows-only because PdfPig text extraction is unreliable against Linux subset fonts.</summary>
    [Fact]
    public async Task Renders_a_multi_page_pdf()
    {
        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);
        Assert.NotNull(model);

        var pdf = new DossierPdfRenderer(WebRoot()).Render(model!, DossierVariant.Executive);

        Assert.NotEmpty(pdf);
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        Assert.True(doc.NumberOfPages >= 2);
    }

    [SkippableFact]
    public async Task Renders_the_dossier_with_no_risk_score()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction from SkiaSharp subset fonts is unreliable on Linux; covered by the windows-tests job.");

        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);
        Assert.NotNull(model);

        var pdf = new DossierPdfRenderer(WebRoot()).Render(model!, DossierVariant.Executive);
        Assert.NotEmpty(pdf);

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        Assert.True(doc.NumberOfPages >= 2);

        var text = string.Concat(doc.GetPages().Select(p => p.Text));
        Assert.Contains("DUE DILIGENCE DOSSIER", text);
        Assert.Contains("Golden Master Ltd", text);
        Assert.Contains("Contents", text);
        Assert.Contains("Snapshot", text);

        // D12 — the "Not assessed" coverage block is deterministic, so "no flag" is never mistaken for "verified clean".
        Assert.Contains("Not assessed", text);
        Assert.Contains("leverage-trend checks were not run", text);

        // New attribution line (cover footer + "Prepared by").
        Assert.Contains("Gaba Projects Private Limited", text);

        // #197: "Annexure A-E" lettering is gone from the rendered document; sections carry plain names.
        Assert.Contains("Financial Profile", text);
        Assert.Contains("Borrowing & Security", text);
        Assert.Contains("Directors & Governance", text);
        Assert.Contains("Statutory Compliance", text);
        Assert.Contains("Litigation", text);
        Assert.Contains("Executive Summary", text);
        Assert.DoesNotContain("Annexure", text);

        // The hard rule: no score, no gauge, no document index.
        var lower = text.ToLowerInvariant();
        Assert.DoesNotContain("risk score", lower);
        Assert.DoesNotContain("/100", lower);
        Assert.DoesNotContain("documents index", lower);

        // Product decision: the client-facing PDF names "CT AI" and nothing more specific — no model
        // name, no vendor, no generic "AI-assisted" phrasing that isn't the branded term.
        Assert.Contains("CT AI", text);
        Assert.DoesNotContain("gemini", lower);
        Assert.DoesNotContain("ai-assisted", lower);
        Assert.DoesNotContain("vertex", lower);
    }

    /// <summary>#152/#197: the source-reported ratios (catalogue A1.x) get their own "Ratios, as
    /// reported" block in Financial Profile instead of sitting in the flat "Additional line items"
    /// catch-all — and must appear there exactly once, not in both places. The seed already carries one
    /// real Ratios-section FinancialFact ("Debt / Equity Ratio") alongside one non-Ratios fact
    /// ("Reserves and Surplus"), so this exercises the real split without adding fixture data.</summary>
    [SkippableFact]
    public async Task Source_reported_ratios_get_their_own_block_not_the_flat_catch_all()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction from SkiaSharp subset fonts is unreliable on Linux; covered by the windows-tests job.");

        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);
        Assert.NotNull(model);

        var pdf = new DossierPdfRenderer(WebRoot()).Render(model!, DossierVariant.Executive);
        var text = TextOf(pdf);

        Assert.Contains("Ratios, as reported", text);
        Assert.Contains("Debt / Equity Ratio", text);
        Assert.Contains("3.44", text);
        Assert.Contains("Reserves and Surplus", text); // still in the flat catch-all — it isn't a ratio

        // The whole point: the ratio appears exactly once (in its own block), not duplicated below.
        var occurrences = System.Text.RegularExpressions.Regex.Matches(text, "Debt / Equity Ratio").Count;
        Assert.Equal(1, occurrences);
    }

    /// <summary>#197: a cross-section finding (CrossSectionRules) lists its component finding codes in
    /// SupportingSignalsJson; the component must render nested as "Evidence" under the cross-section
    /// card, not also as its own separate flat sibling card — that duplication is exactly the "flag
    /// inflation" #197 was raised to fix. Overrides just ExecSummary on the real seeded model (via
    /// record `with`) rather than hand-building a full DossierModel.</summary>
    [SkippableFact]
    public async Task Cross_section_finding_nests_its_component_as_evidence_not_a_separate_card()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction from SkiaSharp subset fonts is unreliable on Linux; covered by the windows-tests job.");

        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);
        Assert.NotNull(model);

        var component = new AnalysisFinding
        {
            AnalysisRunId = model!.AnalysisRunId ?? 0, RequestId = requestId,
            Section = FindingSection.Charges, Severity = FindingSeverity.Review, TemporalStatus = TemporalStatus.Current,
            Code = "TEST_COMPONENT", Title = "Test Component Finding", SummaryText = "Component summary text.",
            DisplayPriority = 100, ObservationDate = new DateOnly(2026, 1, 1)
        };
        var parent = new AnalysisFinding
        {
            AnalysisRunId = model.AnalysisRunId ?? 0, RequestId = requestId,
            Section = FindingSection.CrossSection, Severity = FindingSeverity.Review, TemporalStatus = TemporalStatus.Current,
            Code = "TEST_CROSS_SECTION", Title = "Test Cross-Section Finding", SummaryText = "Parent summary text.",
            DisplayPriority = 101, ObservationDate = new DateOnly(2026, 1, 1),
            SupportingSignalsJson = JsonSerializer.Serialize(new[] { "TEST_COMPONENT" })
        };
        var testModel = model with
        {
            ExecSummary = model.ExecSummary with { FindingsInDisplayOrder = [parent, component] }
        };

        var pdf = new DossierPdfRenderer(WebRoot()).Render(testModel, DossierVariant.Executive);
        var text = TextOf(pdf);

        Assert.Contains("Test Cross-Section Finding", text);
        Assert.Contains("Evidence", text);
        Assert.Contains("Test Component Finding", text);
        Assert.Contains("Component summary text.", text);
        Assert.Contains("Borrowing & Security", text); // the component's own section cited on its evidence line

        // The whole point: the component appears exactly once (nested), never as a second, separate card.
        var occurrences = System.Text.RegularExpressions.Regex.Matches(text, "Test Component Finding").Count;
        Assert.Equal(1, occurrences);
    }

    /// <summary>G18/#161: the Auditor's-comments Annexure B cell folds Section/Directors' Comments/
    /// Footnotes into the existing Comment column as inline text (deliberately no new dense-table
    /// columns — see the issue's plan for why). Real render-and-extract, not manual PDF inspection.</summary>
    [SkippableFact]
    public async Task Auditor_comment_cell_includes_section_directors_comments_and_footnote_inline()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction from SkiaSharp subset fonts is unreliable on Linux; covered by the windows-tests job.");

        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);
        Assert.NotNull(model);

        var pdf = new DossierPdfRenderer(WebRoot()).Render(model!, DossierVariant.Executive);
        var text = TextOf(pdf);

        // PdfPig's raw glyph-concatenation extraction can drop a space at certain punctuation
        // boundaries (e.g. "700600(Disclosures)", "Footnote:See annexure") even though the rendered PDF
        // itself is correctly spaced — this file's own existing tests already work around the same
        // extraction quirk elsewhere. Assert on the pieces independently rather than one exact string.
        Assert.Contains("Section: 700600", text);
        Assert.Contains("Disclosures", text);
        Assert.Contains("Directors' comments: Self explanatory", text);
        Assert.Contains("Footnote:", text);
        Assert.Contains("See annexure", text);
    }

    /// <summary>Real-file E2E testing (PRUKSA INDIA HOUSING PRIVATE LIMITED) found a workbook where the
    /// only source of auditor identity (name/firm/FRN/membership) was a block the ingestion orchestrator
    /// merges into AuditorObservation — for a year with no qualitative remark, ObservationText is null
    /// and identity is the only real content, so it must not silently disappear from the final report.
    /// Folded into the existing Comment cell rather than a new column (#161's dense-table-columns note).</summary>
    [SkippableFact]
    public async Task Auditor_comment_cell_includes_identity_when_no_qualitative_remark_exists()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction from SkiaSharp subset fonts is unreliable on Linux; covered by the windows-tests job.");

        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, ingestionRunId, _) = await DossierTestSeed.SeedAsync(seed);

        seed.AuditorObservations.Add(new AuditorObservation
        {
            RequestId = requestId, IngestionRunId = ingestionRunId,
            FinancialYear = 2018, Basis = FinancialBasis.Standalone,
            AuditorName = "UMANG BANKA", FirmName = "B S R & CO LLP",
            FirmRegistrationNumber = "101248W/W100022", MembershipNumber = "223018"
        });
        await seed.SaveChangesAsync();

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);
        Assert.NotNull(model);

        var pdf = new DossierPdfRenderer(WebRoot()).Render(model!, DossierVariant.Executive);
        var text = TextOf(pdf);

        Assert.Contains("UMANG BANKA", text);
        Assert.Contains("B S R & CO LLP", text);
        Assert.Contains("101248W/W100022", text);
        Assert.Contains("223018", text);
    }

    /// <summary>#213 review: the new Company Profile block (first table on the Snapshot page) must render
    /// every field the source workbook's "Company Information" sheet carries, and — since a request can be
    /// analysed before any ROC report was ever parsed — must not crash or render anything when there is no
    /// CompanyProfile at all, rather than assuming one always exists.</summary>
    [SkippableFact]
    public async Task Company_profile_table_renders_full_detail_and_is_absent_when_no_profile_was_ingested()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction from SkiaSharp subset fonts is unreliable on Linux; covered by the windows-tests job.");

        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);

        // The base seed only sets a handful of CompanyProfile fields (enough for the cover/corporate
        // blocks that predate #213) — extend it here, scoped to this one uniquely-numbered request, so
        // this test actually exercises every Company Profile row without perturbing the golden-master
        // fixture other tests in this file depend on.
        await seed.CompanyProfiles.Where(p => p.RequestId == requestId).ExecuteUpdateAsync(s => s
            .SetProperty(p => p.RegisteredAddress, "1 Test Street")
            .SetProperty(p => p.RegisteredAddressCity, "Bengaluru")
            .SetProperty(p => p.RegisteredAddressState, "Karnataka")
            .SetProperty(p => p.RegisteredAddressPinCode, "560001")
            .SetProperty(p => p.BusinessAddress, "2 Business Park")
            .SetProperty(p => p.Website, "https://goldenmaster.example")
            .SetProperty(p => p.Phone, "+91-80-99999999")
            .SetProperty(p => p.EntityType, "Private Limited Company")
            .SetProperty(p => p.ListingStatus, "Unlisted")
            .SetProperty(p => p.Industry, "Testing")
            .SetProperty(p => p.Segment, "Quality Assurance")
            .SetProperty(p => p.NarrativeDescription, "Golden Master Ltd is a fixture used exclusively for automated tests."));

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);
        Assert.NotNull(model);
        Assert.NotNull(model!.Profile);

        var pdf = new DossierPdfRenderer(WebRoot()).Render(model, DossierVariant.Executive);
        var text = TextOf(pdf);

        // "Company Pro?le"/"REGISTERED ADDRESS" not "Profile": PdfPig's glyph extraction mangles the "fi"
        // ligature into "?" (a pre-existing, documented quirk of this file's other tests) — assert on the
        // surrounding ligature-free kicker labels instead of the section title itself.
        Assert.Contains("REGISTERED ADDRESS", text);
        Assert.Contains("CONTACT & CLASSIFICATION", text);
        Assert.Contains("1 Test Street", text);
        Assert.Contains("Bengaluru", text);
        Assert.Contains("Karnataka", text);
        Assert.Contains("560001", text);
        Assert.Contains("2 Business Park", text);
        Assert.Contains("https://goldenmaster.example", text);
        Assert.Contains("+91-80-99999999", text);
        Assert.Contains("Unlisted", text);
        Assert.Contains("Quality Assurance", text);
        Assert.Contains("automated tests", text);

        // Absent-profile behaviour: no CompanyProfile row at all must not crash the composer, and the
        // block must not render (not even an empty header) — checked via the same ligature-free markers.
        var noProfileModel = model with { Profile = null };
        var noProfileText = TextOf(new DossierPdfRenderer(WebRoot()).Render(noProfileModel, DossierVariant.Executive));
        Assert.DoesNotContain("REGISTERED ADDRESS", noProfileText);
        Assert.DoesNotContain("CONTACT & CLASSIFICATION", noProfileText);
    }

    /// <summary>#213 review: the "Source coverage" / "Not assessed" detail must only exist as Section 7 —
    /// both the Contents anchor and the appendix page itself — when there is an actual gap to disclose.
    /// The seeded fixture's real assembled model already has both an absent-sheet and a not-assessed-notes
    /// gap (proving the "has a gap" path); forcing both to empty proves the "no gap" path never renders an
    /// empty appendix page instead of just being silently correct by coincidence.</summary>
    [SkippableFact]
    public async Task Coverage_appendix_and_contents_anchor_exist_only_when_there_is_a_gap_to_disclose()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction from SkiaSharp subset fonts is unreliable on Linux; covered by the windows-tests job.");

        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);
        Assert.NotNull(model);

        // "Suf?ciency" not "Sufficiency": PdfPig mangles the "ffi" ligature into "?" (same pre-existing
        // quirk noted in the Company Profile test above) — assert on the ligature-free "SECTION 7" kicker
        // and the "7. Coverage & Data" contents-line prefix instead of the full title text.
        var gapText = TextOf(new DossierPdfRenderer(WebRoot()).Render(model!, DossierVariant.Executive));
        Assert.Contains("7. Coverage & Data", gapText);
        Assert.Contains("SECTION 7", gapText);
        Assert.Contains("Full detail in Section 7", gapText);

        var noGapModel = model! with
        {
            SourceCoverage = SheetCoverage.Empty,
            ExecSummary = model.ExecSummary with { NotAssessed = [] }
        };
        var noGapText = TextOf(new DossierPdfRenderer(WebRoot()).Render(noGapModel, DossierVariant.Executive));
        Assert.DoesNotContain("7. Coverage & Data", noGapText);
        Assert.DoesNotContain("SECTION 7", noGapText);
        Assert.DoesNotContain("Full detail in Section 7", noGapText);
    }

    /// <summary>#213 review: beyond <c>MaxYearColumnsPerTable</c> (6), both the typed Standalone financial
    /// table and the FinancialFact "Additional line items" catch-all must split into multiple tables
    /// without dropping or duplicating a single (label, year) value at the chunk boundary. Every planted
    /// value here is a distinctive two-decimal figure (10.18–10.25 / 20.18–20.25) that no other seeded
    /// figure in this fixture can coincidentally reproduce, so "appears exactly once anywhere in the
    /// rendered text" is a direct proof of neither dropping nor duplicating.</summary>
    [SkippableFact]
    public async Task Financial_grids_beyond_six_years_are_chunked_with_every_value_appearing_exactly_once()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction from SkiaSharp subset fonts is unreliable on Linux; covered by the windows-tests job.");

        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, ingestionRunId, _) = await DossierTestSeed.SeedAsync(seed);

        // The base seed already carries standalone 2023/2024/2025 — add five more years so the total (8)
        // exceeds the 6-per-table cap and must split into two chunks (2018-2023, 2024-2025).
        int[] extraYears = [2018, 2019, 2020, 2021, 2022];
        foreach (var year in extraYears)
            seed.FinancialYearData.Add(new FinancialYearData
            {
                RequestId = requestId, IngestionRunId = ingestionRunId,
                FinancialYear = year, Basis = FinancialBasis.Standalone,
                NetWorth = 10m + year % 100 * 0.01m
            });

        int[] allEightYears = [2018, 2019, 2020, 2021, 2022, 2023, 2024, 2025];
        foreach (var year in allEightYears)
            seed.FinancialFacts.Add(new FinancialFact
            {
                RequestId = requestId, IngestionRunId = ingestionRunId,
                Basis = FinancialBasis.Standalone, Section = FinancialStatementSection.BalanceSheet,
                Label = "Test Extra Line", FinancialYear = year,
                NumericValue = 20m + year % 100 * 0.01m,
                RawValue = (20m + year % 100 * 0.01m).ToString("0.00")
            });
        await seed.SaveChangesAsync();

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);
        Assert.NotNull(model);

        var pdf = new DossierPdfRenderer(WebRoot()).Render(model!, DossierVariant.Executive);
        var text = TextOf(pdf);

        foreach (var year in extraYears)
        {
            var netWorthValue = (10m + year % 100 * 0.01m).ToString("N2");
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(
                text, System.Text.RegularExpressions.Regex.Escape(netWorthValue)));
        }

        foreach (var year in allEightYears)
        {
            var factValue = (20m + year % 100 * 0.01m).ToString("N2");
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(
                text, System.Text.RegularExpressions.Regex.Escape(factValue)));
        }

        // The chunk boundary itself: an FY from each half of the split must appear as a table header.
        Assert.Contains("FY2018", text);
        Assert.Contains("FY2024", text);
    }

    /// <summary>#214 review: a handful of tracked optional categories (Credit Ratings, Related Party
    /// Transactions, Proprietorship, Legal Cases - Financial Dispute, Unaccepted Ratings) have no table
    /// anywhere in this dossier, present or absent — so unlike every other tracked category, there is no
    /// in-context section to carry a "not provided in this upload" note for them. Proves Section 7 still
    /// names one of these explicitly rather than folding it into the aggregate count with no way for a
    /// client to learn which specific category is missing.</summary>
    [SkippableFact]
    public async Task Unmapped_optional_category_with_no_dossier_table_gets_an_explicit_fallback_note()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction from SkiaSharp subset fonts is unreliable on Linux; covered by the windows-tests job.");

        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);
        Assert.NotNull(model);

        // #215 gave Credit Ratings, Unaccepted Ratings, Related Party Transactions and Proprietorship a
        // real table each — Structure remains one of the few still-unmapped categories.
        var structureName = SheetAliases.CanonicalName(SheetAliases.Structure);
        var fakeRun = new IngestionRun
        {
            AbsentOptionalSheetsJson = JsonSerializer.Serialize(new[] { structureName })
        };
        var forcedModel = model! with { SourceCoverage = SheetCoverage.From(fakeRun) };

        var text = TextOf(new DossierPdfRenderer(WebRoot()).Render(forcedModel, DossierVariant.Executive));

        Assert.Contains("Corporate structure", text);
        Assert.Contains("no dedicated section in this dossier", text);
    }

    /// <summary>#215: Credit Ratings, Unaccepted Ratings, Related Party Transactions and Proprietorship
    /// all moved from Section 7's "no table" fallback list to a real, dedicated table each. Proves the
    /// full round trip per category: present data renders, and an absent sheet shows the in-context
    /// "not provided in this upload" note right next to that specific empty table (not folded into
    /// Section 7's fallback list, since it now has a proper home).</summary>
    [SkippableFact]
    public async Task Newly_mapped_categories_render_present_data_and_disclose_absence_in_context()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction from SkiaSharp subset fonts is unreliable on Linux; covered by the windows-tests job.");

        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, ingestionRunId, _) = await DossierTestSeed.SeedAsync(seed);

        seed.RelatedPartyTransactions.Add(new RelatedPartyTransaction
        {
            RequestId = requestId, IngestionRunId = ingestionRunId,
            FinancialYearEnding = new DateOnly(2025, 3, 31), EntityType = "Subsidiary",
            EntityNameRaw = "Test RPT Counterparty Ltd", EntityNameNormalized = "TEST RPT COUNTERPARTY LTD",
            RelationshipRaw = "Subsidiary", TransactionType = "Sale of goods", AmountCrore = 12.34m
        });
        seed.CreditRatings.Add(new CreditRating
        {
            // Short values throughout — a long value wrapping onto two lines in this narrow-columned
            // table can jumble PdfPig's reading order with the single-line cells beside it (the same
            // extraction quirk this file's other tests already avoid), so nothing here should wrap.
            RequestId = requestId, IngestionRunId = ingestionRunId,
            Agency = "TestAgency", Instrument = "NCD", Rating = "BBB+",
            Action = "Held", Outlook = "Stable", Amount = 55.5m,
            RatingDate = new DateOnly(2025, 6, 1), IsAccepted = true
        });
        seed.CreditRatings.Add(new CreditRating
        {
            // #216 review: an unaccepted row (no Action column — the source sheet carries none, per
            // CreditRating.Action's own doc comment) must render "Not accepted" rather than a blank cell.
            RequestId = requestId, IngestionRunId = ingestionRunId,
            Agency = "UnacceptedAgency", Instrument = "NCD2", Rating = "-",
            Action = null, Outlook = null, RatingDate = new DateOnly(2025, 7, 1), IsAccepted = false
        });
        seed.ProprietorshipAssociations.Add(new ProprietorshipAssociation
        {
            RequestId = requestId, IngestionRunId = ingestionRunId,
            DirectorDin = "00000001", DirectorNameRaw = "ALICE RAO",
            LegalName = "Alice Rao Trading Co", Pan = "AAAAA1111A", Status = "Active"
        });
        seed.FinancialDisputeCases.Add(new FinancialDisputeCase
        {
            RequestId = requestId, IngestionRunId = ingestionRunId,
            Direction = "RECEIVABLE", DisputeType = "Money Claim", Court = "TestCourt",
            Litigants = "GML vs Debtor", CaseNumber = "TFD/2025/001",
            AmountUnderDefault = 7.89m, Verdict = "Pending", DateOfDefault = new DateOnly(2025, 1, 15)
        });
        await seed.SaveChangesAsync();

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);
        Assert.NotNull(model);

        var presentText = TextOf(new DossierPdfRenderer(WebRoot()).Render(model!, DossierVariant.Executive));
        Assert.Contains("Test RPT Counterparty Ltd", presentText);
        Assert.Contains("TestAgency", presentText);
        Assert.Contains("BBB+", presentText);
        Assert.Contains("Held", presentText);
        Assert.Contains("UnacceptedAgency", presentText);
        Assert.Contains("Not accepted", presentText);
        Assert.Contains("Alice Rao Trading Co", presentText);
        Assert.Contains("TestCourt", presentText);

        // A separate, freshly-seeded request with none of these four categories at all — proves the
        // in-context note for a genuinely empty table (the first request above has real rows in every
        // one of them now, so forcing its SourceCoverage flag alone would prove nothing: the empty-state
        // branch only runs when the row count is actually zero).
        await using var seed2 = DossierGoldenMasterTests.CreateContext();
        var (requestId2, _, _) = await DossierTestSeed.SeedAsync(seed2);

        await using var db2 = DossierGoldenMasterTests.CreateContext();
        var model2 = await new DossierAssembler(db2).BuildAsync(requestId2);
        Assert.NotNull(model2);

        var absentNames = new[]
        {
            SheetAliases.CanonicalName(SheetAliases.RelatedPartyTransactions),
            SheetAliases.CanonicalName(SheetAliases.CreditRatings),
            SheetAliases.CanonicalName(SheetAliases.UnacceptedRatings),
            SheetAliases.CanonicalName(SheetAliases.Proprietorship),
            SheetAliases.CanonicalName(SheetAliases.LegalCasesFinancialDispute),
        };
        var fakeRun = new IngestionRun { AbsentOptionalSheetsJson = JsonSerializer.Serialize(absentNames) };
        var absentModel = model2! with { SourceCoverage = SheetCoverage.From(fakeRun) };
        var absentText = TextOf(new DossierPdfRenderer(WebRoot()).Render(absentModel, DossierVariant.Executive));

        Assert.Contains("This upload did not include “Related Party Transactions”.", absentText);
        Assert.Contains("This upload did not include “Credit Ratings” or “Unaccepted Ratings”.", absentText);
        Assert.Contains("This upload did not include “Proprietorship”.", absentText);
        Assert.Contains("This upload did not include “Legal Cases - Financial Dispute”.", absentText);
        Assert.DoesNotContain("no dedicated section in this dossier", absentText);
    }

    // ── Feature: per-client Litigation toggle ────────────────────────────────

    /// <summary>When the client's IncludeLitigationInDossier flag is off, the PDF is a genuinely different
    /// document: no TOC entry, no Litigation annexure, no litigation-sourced finding, and a cross-section
    /// finding that cites a litigation code (via SupportingSignalsJson) is excluded too — not softened into
    /// a redirect citation. Built via DossierTestSeed's own flag parameter, proving the real
    /// Client → DossierAssembler → DossierPdfComposer wiring, not just a composer-level model override.</summary>
    [SkippableFact]
    public async Task Litigation_is_completely_absent_when_the_clients_flag_is_off()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction from SkiaSharp subset fonts is unreliable on Linux; covered by the windows-tests job.");

        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed, includeLitigationInDossier: false);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);
        Assert.NotNull(model);
        Assert.False(model!.IncludeLitigation);

        // A cross-section finding that cites one of the seed's litigation finding codes — must be excluded
        // too, not just the plain litigation-section findings.
        var crossSection = new AnalysisFinding
        {
            AnalysisRunId = model.AnalysisRunId ?? 0, RequestId = requestId,
            Section = FindingSection.CrossSection, Severity = FindingSeverity.Review, TemporalStatus = TemporalStatus.Current,
            Code = "TEST_CROSS_SECTION_LITIGATION_LINKED", Title = "Cross-Section Litigation Linked Finding",
            SummaryText = "Cross-section summary referencing litigation.", DisplayPriority = 99,
            ObservationDate = new DateOnly(2026, 1, 1),
            SupportingSignalsJson = JsonSerializer.Serialize(new[] { LitigationRules.PendingAgainstCompanyCode })
        };
        var testModel = model with
        {
            ExecSummary = model.ExecSummary with
            {
                FindingsInDisplayOrder = [.. model.ExecSummary.FindingsInDisplayOrder, crossSection]
            }
        };

        var text = TextOf(new DossierPdfRenderer(WebRoot()).Render(testModel, DossierVariant.Executive));

        Assert.DoesNotContain("6. Litigation", text);
        Assert.DoesNotContain("SECTION 6", text);
        Assert.DoesNotContain("Pending Litigation Against Company", text);
        Assert.DoesNotContain("Potential Litigation Requiring Role Verification", text);
        Assert.DoesNotContain("Cross-Section Litigation Linked Finding", text);
        Assert.DoesNotContain("and Litigation.", text);
    }

    /// <summary>Regression guard: with the flag on (the default), the Litigation TOC entry, annexure and
    /// Snapshot tile are all still present — the toggle only changes anything for the clients it's turned
    /// off for.</summary>
    [SkippableFact]
    public async Task Litigation_flag_on_by_default_is_unaffected()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction from SkiaSharp subset fonts is unreliable on Linux; covered by the windows-tests job.");

        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);
        Assert.NotNull(model);
        Assert.True(model!.IncludeLitigation);

        var text = TextOf(new DossierPdfRenderer(WebRoot()).Render(model, DossierVariant.Executive));

        Assert.Contains("6. Litigation", text);
        Assert.Contains("SECTION 6", text);
        Assert.Contains("Pending Litigation Against Company", text);
        Assert.Contains("and Litigation.", text);
    }

    /// <summary>Proves ReviewPriority and the flag-count strip are genuinely recomputed from the filtered
    /// finding set (via ReviewPriorityCalculator.Explain), not passed through from the stored, litigation-
    /// inclusive AnalysisRun columns — a single Litigation-section Critical finding (with a non-designated
    /// code, so it alone drives priority only via "single-domain critical", not a shortcut) is the only
    /// thing driving priority/critical-count above zero, so removing it must change both.</summary>
    [SkippableFact]
    public async Task Priority_and_counts_are_recomputed_without_litigation_when_the_flag_is_off()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction from SkiaSharp subset fonts is unreliable on Linux; covered by the windows-tests job.");

        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);
        Assert.NotNull(model);

        var litigationCritical = new AnalysisFinding
        {
            AnalysisRunId = model!.AnalysisRunId ?? 0, RequestId = requestId,
            Section = FindingSection.Litigation, Severity = FindingSeverity.Critical, TemporalStatus = TemporalStatus.Current,
            Code = "LITIGATION_TEST_CRITICAL", Title = "Test Litigation Critical Finding",
            SummaryText = "Test litigation critical summary.", DisplayPriority = 95, ObservationDate = new DateOnly(2026, 1, 1)
        };
        var customExec = model.ExecSummary with
        {
            ReviewPriority = ReviewPriority.High,
            CriticalCount = 1, ReviewCount = 0, WatchCount = 0, PositiveCount = 0,
            FindingsInDisplayOrder = [litigationCritical],
            Structured = null,
            NotAssessed = []
        };

        var flagOnModel = model with { IncludeLitigation = true, ExecSummary = customExec };
        var flagOffModel = model with { IncludeLitigation = false, ExecSummary = customExec };

        var onText = TextOf(new DossierPdfRenderer(WebRoot()).Render(flagOnModel, DossierVariant.Executive));
        var offText = TextOf(new DossierPdfRenderer(WebRoot()).Render(flagOffModel, DossierVariant.Executive));

        Assert.Contains("HIGH", onText);
        Assert.Contains("1 Critical", onText);

        Assert.Contains("LOW", offText);
        Assert.Contains("0 Critical", offText);
        Assert.DoesNotContain("Test Litigation Critical Finding", offText);
    }

    /// <summary>The stored AI executive summary ("Cross-section read") is free-text prose that cannot be
    /// safely filtered for litigation mentions, so it must be suppressed outright when the flag is off —
    /// reusing the same null-Structured fallback path used when AI synthesis itself failed.</summary>
    [SkippableFact]
    public async Task Ai_executive_summary_is_suppressed_when_the_flag_is_off()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction from SkiaSharp subset fonts is unreliable on Linux; covered by the windows-tests job.");

        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);
        Assert.NotNull(model);

        var structured = new ExecutiveSummary(
            "Business performance is stable.", "Financial position is under review.",
            "Borrowing and security look typical.", "Governance disclosures are on file.",
            ["Track the pending litigation matters closely before disbursement."]);
        var withSummary = model! with { ExecSummary = model.ExecSummary with { Structured = structured } };

        var onText = TextOf(new DossierPdfRenderer(WebRoot()).Render(
            withSummary with { IncludeLitigation = true }, DossierVariant.Executive));
        var offText = TextOf(new DossierPdfRenderer(WebRoot()).Render(
            withSummary with { IncludeLitigation = false }, DossierVariant.Executive));

        Assert.Contains("Cross-section read", onText);
        Assert.Contains("Track the pending litigation matters closely", onText);

        Assert.DoesNotContain("Cross-section read", offText);
        Assert.DoesNotContain("Track the pending litigation matters closely", offText);
    }

    /// <summary>D12's "Not assessed" coverage note for the litigation check
    /// (LitigationRules.PendingAgainstCompanyCode, "LITIGATION_..."-prefixed) must be excluded from the
    /// flag-off Coverage & Data Sufficiency section along with everything else litigation-derived.</summary>
    [SkippableFact]
    public async Task Litigation_not_assessed_note_is_excluded_when_the_flag_is_off()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction from SkiaSharp subset fonts is unreliable on Linux; covered by the windows-tests job.");

        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);
        Assert.NotNull(model);

        var litigationNote = new DataSufficiencyNote(
            LitigationRules.PendingAgainstCompanyCode, "No litigation records were available for cross-checking.");
        var withNote = model! with
        {
            ExecSummary = model.ExecSummary with { NotAssessed = [.. model.ExecSummary.NotAssessed, litigationNote] }
        };

        var onText = TextOf(new DossierPdfRenderer(WebRoot()).Render(
            withNote with { IncludeLitigation = true }, DossierVariant.Executive));
        var offText = TextOf(new DossierPdfRenderer(WebRoot()).Render(
            withNote with { IncludeLitigation = false }, DossierVariant.Executive));

        Assert.Contains("No litigation records were available for cross-checking.", onText);
        Assert.DoesNotContain("No litigation records were available for cross-checking.", offText);
    }

    /// <summary>The redaction must fail CLOSED on unparseable provenance: a cross-section finding whose
    /// SupportingSignalsJson is null, empty or malformed cannot be proven litigation-free, so it must be
    /// excluded — the opposite default from the existing SupportingCodes/ParseSupportingCodes helpers, which
    /// return an empty set (and so "no dependency") for the same inputs. Paired with a positive case: a
    /// cross-section finding that parses cleanly and demonstrably does not cite a litigation code must still
    /// be kept, proving the fix doesn't over-exclude every cross-section finding.</summary>
    [SkippableFact]
    public async Task Cross_section_finding_with_unparseable_provenance_is_excluded_when_the_flag_is_off()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction from SkiaSharp subset fonts is unreliable on Linux; covered by the windows-tests job.");

        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed, includeLitigationInDossier: false);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);
        Assert.NotNull(model);

        AnalysisFinding CrossSection(string suffix, string title, string? supportingSignalsJson) => new()
        {
            AnalysisRunId = model!.AnalysisRunId ?? 0, RequestId = requestId,
            Section = FindingSection.CrossSection, Severity = FindingSeverity.Review, TemporalStatus = TemporalStatus.Current,
            Code = $"TEST_CROSS_{suffix}", Title = title, SummaryText = title + ".",
            DisplayPriority = 60, ObservationDate = new DateOnly(2026, 1, 1), SupportingSignalsJson = supportingSignalsJson
        };

        var nullJson = CrossSection("NULL", "Cross-Section Null Provenance", null);
        var emptyJson = CrossSection("EMPTY", "Cross-Section Empty Provenance", "");
        var malformedJson = CrossSection("MALFORMED", "Cross-Section Malformed Provenance", "{not valid json");
        var provablyClean = CrossSection("CLEAN", "Cross-Section Provably Litigation Free",
            JsonSerializer.Serialize(new[] { "SOME_NON_LITIGATION_CODE" }));

        var testModel = model! with
        {
            ExecSummary = model.ExecSummary with
            {
                FindingsInDisplayOrder = [.. model.ExecSummary.FindingsInDisplayOrder, nullJson, emptyJson, malformedJson, provablyClean]
            }
        };

        var text = TextOf(new DossierPdfRenderer(WebRoot()).Render(testModel, DossierVariant.Executive));

        Assert.DoesNotContain("Cross-Section Null Provenance", text);
        Assert.DoesNotContain("Cross-Section Empty Provenance", text);
        Assert.DoesNotContain("Cross-Section Malformed Provenance", text);
        Assert.Contains("Cross-Section Provably Litigation Free", text);
    }

    /// <summary>DossierModel is shared with the portal's on-screen company page (DossierCache /
    /// RequestsController), so the redaction must live entirely inside DossierPdfComposer — the model
    /// DossierAssembler/DossierCache hand back for a flag-off client must still carry full, unfiltered
    /// litigation data.</summary>
    [Fact]
    public async Task Portal_company_page_is_unaffected_by_the_litigation_toggle()
    {
        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed, includeLitigationInDossier: false);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);

        Assert.NotNull(model);
        Assert.False(model!.IncludeLitigation);
        Assert.NotEmpty(model.Litigation.All);
        Assert.Contains(model.ExecSummary.FindingsInDisplayOrder, f => f.Section == FindingSection.Litigation);
    }

    // ── Feature: AI narrative for Charges & Security ─────────────────────────

    /// <summary>The charges-narrative callout renders with the "CT AI" attribution the rest of this dossier
    /// already locks in (see Renders_the_dossier_with_no_risk_score's own assertions) — never the model
    /// name or vendor — and shows the covered/total charge counts and each notable point's charge
    /// reference tag.</summary>
    [SkippableFact]
    public async Task Charges_narrative_renders_with_CT_AI_attribution_and_never_leaks_the_model_or_vendor()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction from SkiaSharp subset fonts is unreliable on Linux; covered by the windows-tests job.");

        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);
        Assert.NotNull(model);

        var narrative = new ChargesNarrative(
            "The largest open charge is held by State Bank of India over the current assets and movable fixed assets.",
            [new NotableCollateralPoint("Consortium arrangement across multiple lenders on the same asset pool.", [model!.Charges.Open.First().ChargeId])],
            CoveredChargeCount: model.Charges.Open.Count, TotalOpenChargeCount: model.Charges.Open.Count);
        var testModel = model with { ChargesNarrative = narrative };

        var text = TextOf(new DossierPdfRenderer(WebRoot()).Render(testModel, DossierVariant.Executive));

        Assert.Contains("CT AI", text);
        Assert.DoesNotContain("gemini", text.ToLowerInvariant());
        Assert.DoesNotContain("vertex", text.ToLowerInvariant());
        Assert.Contains("The largest open charge is held by State Bank of India", text);
        Assert.Contains("Consortium arrangement across multiple lenders", text);
        Assert.Contains($"Charge {model.Charges.Open.First().ChargeId}", text);
        Assert.Contains($"Largest {model.Charges.Open.Count} of {model.Charges.Open.Count} open charge", text);
    }

    /// <summary>No AnalysisRun.ChargesNarrativeJson (the AI call failed, or hasn't run for this analysis
    /// run) must not render an empty/broken callout — a no-op, same posture as the rest of this dossier's
    /// AI-narrative fallbacks (e.g. "Cross-section read").</summary>
    [SkippableFact]
    public async Task Charges_narrative_callout_is_absent_when_no_narrative_was_persisted()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction from SkiaSharp subset fonts is unreliable on Linux; covered by the windows-tests job.");

        await using var seed = DossierGoldenMasterTests.CreateContext();
        var (requestId, _, _) = await DossierTestSeed.SeedAsync(seed);

        await using var db = DossierGoldenMasterTests.CreateContext();
        var model = await new DossierAssembler(db).BuildAsync(requestId);
        Assert.NotNull(model);
        Assert.Null(model!.ChargesNarrative);

        var text = TextOf(new DossierPdfRenderer(WebRoot()).Render(model, DossierVariant.Executive));

        Assert.DoesNotContain("CT AI read of the largest open charges", text);
    }
}
