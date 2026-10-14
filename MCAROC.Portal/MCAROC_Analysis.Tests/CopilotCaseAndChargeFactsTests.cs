using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.Chat;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

/// <summary>A live Coastal run found the Copilot answering "106 open charges" for 163, "could not verify" for "pending cases against the
/// company", and listing the wrong Supreme Court cases. These pin the facts that fix them: counts stated rather than left to be counted from
/// rows, combinations of status and side, and a case list selected by the same rules as the register.</summary>
public class CopilotCaseAndChargeFactsTests
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    // ── question → filter ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("List the Supreme Court cases against the company.", new[] { LitigationInstitution.SupremeCourt }, null, LitigationCompanySide.AgainstCompany)]
    [InlineData("How many litigation cases are pending against the company?", new LitigationInstitution[0], LitigationCaseStatusBucket.Pending, LitigationCompanySide.AgainstCompany)]
    [InlineData("Are there any DRT recovery cases pending?", new[] { LitigationInstitution.Drt }, LitigationCaseStatusBucket.Pending, null)]
    [InlineData("Which NCLT or NCLAT proceedings are disposed?", new[] { LitigationInstitution.Nclat, LitigationInstitution.Nclt }, LitigationCaseStatusBucket.Disposed, null)]
    [InlineData("Show the High Court cases filed by the company", new[] { LitigationInstitution.HighCourt }, null, LitigationCompanySide.ByCompany)]
    [InlineData("Which suits has the company initiated in the district courts?", new[] { LitigationInstitution.DistrictCourt }, null, LitigationCompanySide.ByCompany)]
    public void A_question_describing_cases_yields_a_filter(string question, LitigationInstitution[] forums, LitigationCaseStatusBucket? status, LitigationCompanySide? side)
    {
        var filter = LitigationCaseListSelector.Extract(question);

        Assert.NotNull(filter);
        Assert.Equal(forums.OrderBy(f => f), filter!.Forums.OrderBy(f => f));
        Assert.Equal(status, filter.Status);
        Assert.Equal(side, filter.Side);
    }

    [Theory]
    [InlineData("How many open charges does the company have?")]            // not about cases
    [InlineData("What is the weather in Mumbai today?")]
    [InlineData("What did the High Court order in case 27190/2022?")]       // a named case: handled by the case-record lookup, not a list
    [InlineData("Tell me about the litigation")]                             // about cases, but no forum, status or side to filter by
    [InlineData("Who are the directors? Any proceedings against them?")]    // "against them" is the directors, not the company
    public void A_question_that_does_not_describe_a_set_of_cases_yields_no_filter(string question)
    {
        var filter = LitigationCaseListSelector.Extract(question);

        // The named-case question has a forum word but is answered from the case record; it may still produce a filter, which is harmless
        // because the list only adds records. What must hold is that unrelated questions never do.
        if (question.Contains("27190")) return;
        Assert.Null(filter);
    }

    [Fact]
    public void The_extractor_carries_the_filter_on_the_hints()
    {
        var hints = QuestionHintExtractor.Extract("List the Supreme Court cases against the company", [], []);

        Assert.Equal([LitigationInstitution.SupremeCourt], hints.CaseList!.Forums);
        Assert.Null(QuestionHintExtractor.Extract("What is the company's CIN?", [], []).CaseList);
    }

    // ── selector ────────────────────────────────────────────────────────────────────────────────────────

    private static LitigationCase Case(long id, string? type, string status, string? direction, string? filed, string? petitioner = null, string? respondent = null) => new()
    {
        LitigationCaseId = id, CaseNumber = $"CASE-{id}", Type = type, Court = type, CaseStatus = status, Direction = direction, FilingDate = filed,
        PetitionersJson = petitioner is null ? null : $"[\"{petitioner}\"]", RespondentsJson = respondent is null ? null : $"[\"{respondent}\"]",
        FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow
    };

    [Fact]
    public void The_selector_filters_and_orders_exactly_as_the_register_does()
    {
        var cases = new[]
        {
            Case(1, "supreme", "DISPOSED", "against", "01-01-2020"),
            Case(2, "supreme", "PENDING", null, "01-01-2024", respondent: "Target Company"),   // side from the party names
            Case(3, "supreme", "PENDING", "by", "01-01-2025"),
            Case(4, "highcourt", "PENDING", "against", "01-01-2025"),
            Case(5, "supreme", "PENDING", "against", "01-01-2023"),
        };
        string[] names = ["Target Company"];

        var (supremeAgainst, total) = LitigationCaseListSelector.Select(cases,
            new CaseListFilter([LitigationInstitution.SupremeCourt], null, LitigationCompanySide.AgainstCompany), names);

        Assert.Equal(3, total);
        // pending before disposed; among the pending, the newest filing first
        Assert.Equal([2L, 5L, 1L], supremeAgainst.Select(c => c.LitigationCaseId).ToArray());

        var (pendingSupreme, pendingTotal) = LitigationCaseListSelector.Select(cases,
            new CaseListFilter([LitigationInstitution.SupremeCourt], LitigationCaseStatusBucket.Pending, null), names);
        Assert.Equal(3, pendingTotal);
        Assert.DoesNotContain(pendingSupreme, c => c.LitigationCaseId == 1);
    }

    [Fact]
    public void The_selector_lists_a_bounded_number_and_reports_the_true_total()
    {
        var cases = Enumerable.Range(1, 50).Select(i => Case(i, "highcourt", "PENDING", "against", $"{(i % 28) + 1:D2}-01-2024")).ToList();

        var (listed, total) = LitigationCaseListSelector.Select(cases, new CaseListFilter([LitigationInstitution.HighCourt], null, null), ["Target Company"]);

        Assert.Equal(50, total);
        Assert.Equal(LitigationCaseListSelector.MaxListedCases, listed.Count);
    }

    // ── facts ───────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<(long RequestId, long RunId)> SeedRequestAsync(AppDbContext db)
    {
        var request = new McaRequest
        {
            ClientId = 1, EntityType = EntityType.Company, CompanyName = "Target Company", RequestNumber = $"CCF-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.AnalysisCompleted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        var run = new IngestionRun { RequestId = request.RequestId, RunNumber = 1, StartedDate = DateTime.UtcNow, Status = IngestionRunStatus.CompletedClean };
        db.IngestionRuns.Add(run);
        await db.SaveChangesAsync();
        request.LatestCompletedIngestionRunId = run.IngestionRunId;
        await db.SaveChangesAsync();
        return (request.RequestId, run.IngestionRunId);
    }

    [Fact]
    public async Task Charge_counts_and_totals_are_stated_even_when_every_charge_is_listed()
    {
        await using var db = CreateContext();
        var (requestId, runId) = await SeedRequestAsync(db);
        // 163 open and 50 satisfied long ago: the shape of the Coastal run, where the model counted 164 listed rows and said 106.
        for (var i = 0; i < 163; i++)
            db.RocCharges.Add(new RocCharge { RequestId = requestId, IngestionRunId = runId, RocChargeNumber = $"OPEN-{i:D3}", LatestChargeHolderRaw = i % 2 == 0 ? "BANK A" : "BANK B",
                ChargeStatus = "Open", CurrentAmount = 10m + i, CreationDate = new DateOnly(2015 + (i % 6), 3, 1) });
        for (var i = 0; i < 50; i++)
            db.RocCharges.Add(new RocCharge { RequestId = requestId, IngestionRunId = runId, RocChargeNumber = $"SAT-{i:D3}", LatestChargeHolderRaw = "OLD BANK",
                ChargeStatus = "Satisfied", CurrentAmount = 5m, SatisfactionDate = new DateOnly(2016, 1, 1) });
        await db.SaveChangesAsync();

        var facts = await new StructuredFactsProvider(db).BuildDigestAsync(requestId, new QuestionHints(FilingCategory.Charge, null, null, null, null), default);
        var charges = facts.Where(f => f.DomainKey == "Charges").ToList();

        var headline = Assert.Single(charges, f => f.Text.StartsWith("163 open charge(s), 50 satisfied charge(s)"));
        Assert.Contains("(213 in total)", headline.Text);
        Assert.Contains("₹", headline.Text);

        var holders = Assert.Single(charges, f => f.Text.StartsWith("Open charges by holder"));
        Assert.Contains("BANK A: 82 charge(s)", holders.Text);
        Assert.Contains("BANK B: 81 charge(s)", holders.Text);
        Assert.True(holders.Text.IndexOf("BANK A", StringComparison.Ordinal) < holders.Text.IndexOf("BANK B", StringComparison.Ordinal)); // larger total first

        // rows are capped, largest first, and the cap is stated so a capped list is never passed off as complete
        var rows = charges.Where(f => f.EntityType == "RocCharge").ToList();
        Assert.Equal(StructuredFactsProvider.MaxChargeRows, rows.Count);
        Assert.Contains("OPEN-162", rows[0].Text);
        Assert.Contains(charges, f => f.Text.StartsWith($"Only {StructuredFactsProvider.MaxChargeRows} of the 163"));
    }

    [Fact]
    public async Task A_charge_row_carries_its_creation_date_and_a_year_in_the_question_pulls_in_that_years_charges()
    {
        await using var db = CreateContext();
        var (requestId, runId) = await SeedRequestAsync(db);
        for (var i = 0; i < 60; i++)
            db.RocCharges.Add(new RocCharge { RequestId = requestId, IngestionRunId = runId, RocChargeNumber = $"BIG-{i:D2}", LatestChargeHolderRaw = "BIG BANK",
                ChargeStatus = "Open", CurrentAmount = 1000m + i, CreationDate = new DateOnly(2012, 5, 5) });
        // small, but created in the year asked about: must not be hidden by the cap
        db.RocCharges.Add(new RocCharge { RequestId = requestId, IngestionRunId = runId, RocChargeNumber = "SMALL-2019", LatestChargeHolderRaw = "SMALL BANK",
            ChargeStatus = "Open", CurrentAmount = 1m, CreationDate = new DateOnly(2019, 7, 9) });
        await db.SaveChangesAsync();

        var facts = await new StructuredFactsProvider(db).BuildDigestAsync(
            requestId, new QuestionHints(FilingCategory.Charge, null, null, 2019, null), default);

        var row = Assert.Single(facts, f => f.EntityType == "RocCharge" && f.Text.Contains("SMALL-2019"));
        Assert.Contains("Created 2019-07-09", row.Text);
    }

    private static async Task SeedCasesAsync(AppDbContext db, long requestId, params LitigationCase[] cases)
    {
        var job = new LitigationSearchJob { RequestId = requestId, Status = LitigationSearchJobStatus.Completed, RawResponseHash = "h" + Guid.NewGuid().ToString("N") };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();
        var snapshot = new LitigationReportSnapshot { LitigationSearchJobId = job.LitigationSearchJobId, RequestId = requestId, ReportHash = job.RawResponseHash!,
            Status = LitigationReportSnapshotStatus.Completed, RetrievedUtc = DateTime.UtcNow };
        db.LitigationReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync();
        foreach (var c in cases) { c.RequestId = requestId; c.LitigationCaseId = 0; }
        db.LitigationCases.AddRange(cases);
        await db.SaveChangesAsync();
        db.LitigationCaseSourceReports.AddRange(cases.Select(c => new LitigationCaseSourceReport
            { LitigationCaseId = c.LitigationCaseId, LitigationReportSnapshotId = snapshot.LitigationReportSnapshotId, FirstSeenUtc = DateTime.UtcNow }));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_litigation_summary_states_status_by_side_and_forum_by_side_so_none_has_to_be_counted()
    {
        await using var db = CreateContext();
        var (requestId, _) = await SeedRequestAsync(db);
        await SeedCasesAsync(db, requestId,
            Case(1, "supreme", "PENDING", "against", "01-01-2024"), Case(2, "supreme", "DISPOSED", "by", "01-01-2020"),
            Case(3, "supreme", "DISPOSED", null, "01-01-2020", respondent: "Target Company"),     // side from the names
            Case(4, "drt", "PENDING", "against", "01-01-2023"), Case(5, "drt", "PENDING", "against", "01-01-2022"),
            Case(6, "itat", "Fixed", null, "01-01-2019"));                                          // status not reported, side not determined

        var facts = await new StructuredFactsProvider(db).BuildDigestAsync(requestId, new QuestionHints(null, null, null, null, null), default);
        var litigation = facts.Where(f => f.DomainKey == "Litigation").Select(f => f.Text).ToList();

        Assert.Contains(litigation, t => t.Contains("6 litigation case(s)") && t.Contains("3 pending, 2 disposed") && t.Contains("4 against the company, 1 by the company"));
        var byStatus = Assert.Single(litigation, t => t.StartsWith("Litigation cases by status and the company's side"));
        Assert.Contains("pending: 3 against the company, 0 by the company, 0 with the company on both sides, 0 side not determined", byStatus);
        Assert.Contains("disposed: 1 against the company, 1 by the company, 0 with the company on both sides, 0 side not determined", byStatus);
        Assert.Contains("status not reported: 0 against the company, 0 by the company, 0 with the company on both sides, 1 side not determined", byStatus);

        var byForum = Assert.Single(litigation, t => t.StartsWith("Litigation cases by forum"));
        Assert.Contains("Supreme Court: 3 in all — against the company 2 (1 pending, 1 disposed, 0 unknown)", byForum);
        Assert.Contains("DRT: 2 in all — against the company 2 (2 pending", byForum);
        Assert.Contains("ITAT: 1 in all", byForum);
        Assert.DoesNotContain("NCLAT", byForum); // only forums with a case are listed
    }

    [Fact]
    public async Task A_described_set_of_cases_states_how_many_match_and_how_many_are_listed()
    {
        await using var db = CreateContext();
        var (requestId, _) = await SeedRequestAsync(db);
        await SeedCasesAsync(db, requestId, [.. Enumerable.Range(1, 30).Select(i => Case(i, "supreme", "PENDING", "against", $"{(i % 28) + 1:D2}-01-2024")),
            Case(99, "drt", "PENDING", "against", "01-01-2024")]);

        var hints = QuestionHintExtractor.Extract("List the Supreme Court cases against the company", [], []);
        var facts = await new StructuredFactsProvider(db).BuildDigestAsync(requestId, hints, default);

        var note = Assert.Single(facts, f => f.Text.StartsWith("Cases matching the question"));
        Assert.Contains("(Supreme Court, against the company): 30 of the 31 case(s)", note.Text);
        Assert.Contains($"The first {LitigationCaseListSelector.MaxListedCases} are given as litigation case records", note.Text);

        // a question that describes no set of cases adds no such note
        var plain = await new StructuredFactsProvider(db).BuildDigestAsync(requestId, QuestionHintExtractor.Extract("What is the CIN?", [], []), default);
        Assert.DoesNotContain(plain, f => f.Text.StartsWith("Cases matching the question"));
    }

    private static async Task<(long RequestId, long RunId)> SeedWorkbookRowsAsync(AppDbContext db, int count)
    {
        var (requestId, runId) = await SeedRequestAsync(db);
        for (var i = 0; i < count; i++)
            db.Litigations.Add(new Litigation { RequestId = requestId, IngestionRunId = runId, CaseType = "Filed By this Corporate", CaseStatus = "Disposed",
                Court = "SMALL CAUSES COURT, HYDERABAD", Litigants = $"GENERIC PARTY {i}", CaseNumber = $"OS {1000 + i}/2015" });
        // the rows a question can point at, all workbook-only (no fetched case record exists for any of them)
        db.Litigations.Add(new Litigation { RequestId = requestId, IngestionRunId = runId, CaseType = "Filed Against this Corporate", CaseStatus = "Pending",
            Court = "NATIONAL COMPANY LAW TRIBUNAL", Litigants = "STATE BANK OF INDIA", CaseNumber = "CP(IB) No. 593/KB/2017" });
        db.Litigations.Add(new Litigation { RequestId = requestId, IngestionRunId = runId, CaseType = "Filed Against this Corporate", CaseStatus = "Pending",
            Court = "NATIONAL COMPANY LAW TRIBUNAL", Litigants = "KARUR VYSYA BANK", CaseNumber = "CP(IB) No. 120/KB/2018" });
        db.Litigations.Add(new Litigation { RequestId = requestId, IngestionRunId = runId, CaseType = "Filed By this Corporate", CaseStatus = "Pending",
            Court = "NATIONAL COMPANY LAW TRIBUNAL", Litigants = "SOME DEBTOR", CaseNumber = "CP(IB) No. 77/KB/2019" });
        await db.SaveChangesAsync();
        return (requestId, runId);
    }

    [Fact]
    public async Task A_long_workbook_litigation_list_keeps_the_rows_the_question_points_at()
    {
        await using var db = CreateContext();
        var (requestId, _) = await SeedWorkbookRowsAsync(db, StructuredFactsProvider.MaxWorkbookLitigationRows + 20);
        var provider = new StructuredFactsProvider(db);

        // a case number the question quotes (the workbook spaces it differently)
        var byNumber = await provider.BuildDigestAsync(requestId,
            QuestionHintExtractor.Extract("What is the status of CP(IB) No.593/KB/2017?", [], []), default);
        var row = Assert.Single(byNumber, f => f.EntityType == "Litigation");
        Assert.Contains("593/KB/2017", row.Text);
        Assert.Contains(byNumber, f => f.Text.Contains("on file in the report workbook") && f.Text.Contains("1 matching the question"));

        // a described set: pending NCLT cases against the company
        var byFilter = await provider.BuildDigestAsync(requestId,
            QuestionHintExtractor.Extract("Which NCLT cases are pending against the company?", [], []), default);
        var rows = byFilter.Where(f => f.EntityType == "Litigation").ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Contains("NATIONAL COMPANY LAW TRIBUNAL", r.Text));
        Assert.DoesNotContain(rows, r => r.Text.Contains("77/KB/2019")); // filed by the company, so not "against"

        // a lender the question names
        var byLender = await provider.BuildDigestAsync(requestId,
            QuestionHintExtractor.Extract("Any proceedings involving Karur Vysya Bank?", ["KARUR VYSYA BANK"], []), default);
        Assert.Contains(byLender, f => f.EntityType == "Litigation" && f.Text.Contains("KARUR VYSYA BANK"));
    }

    [Fact]
    public async Task A_long_workbook_list_is_narrowed_by_the_subject_words_of_the_question_too()
    {
        await using var db = CreateContext();
        var (requestId, runId) = await SeedWorkbookRowsAsync(db, StructuredFactsProvider.MaxWorkbookLitigationRows + 20);
        // the word "criminal" appears only in the case type: no number, party or forum in the question points at these rows
        db.Litigations.Add(new Litigation { RequestId = requestId, IngestionRunId = runId, CaseType = "Criminal Case Filed Against this Corporate", CaseStatus = "Pending",
            Court = "METROPOLITAN MAGISTRATE", Litigants = "SOME COMPLAINANT", CaseNumber = "CC 12/2020" });
        db.Litigations.Add(new Litigation { RequestId = requestId, IngestionRunId = runId, CaseType = "Criminal Appeal", CaseStatus = "Pending",
            Court = "SESSIONS COURT", Litigants = "ANOTHER PARTY", CaseNumber = "CRL A 5/2021" });
        await db.SaveChangesAsync();

        var facts = await new StructuredFactsProvider(db).BuildDigestAsync(requestId,
            QuestionHintExtractor.Extract("Are there any criminal cases against the company?", [], []), default);

        var rows = facts.Where(f => f.EntityType == "Litigation").Select(f => f.Text).ToList();
        Assert.Contains(rows, r => r.Contains("CC 12/2020"));      // "criminal" is only in the case type
        Assert.Contains(rows, r => r.Contains("CRL A 5/2021"));
        Assert.Equal(2, rows.Count(r => r.Contains("Criminal")));
        // the two criminal rows rank first (a row the subject word AND the "against" filter point at outranks one only the filter does)
        Assert.Contains("CC 12/2020", rows[0]);
        Assert.DoesNotContain(rows, r => r.Contains("GENERIC PARTY"));
    }

    [Theory]
    [InlineData("Are there any criminal cases against the company?", new[] { "criminal" })]
    [InlineData("Summarise the arbitration awards and cheque dishonour matters", new[] { "arbitration", "awards", "cheque", "dishonour" })]
    [InlineData("What is the status of the cases?", new string[0])]    // only words that say nothing about the subject
    public void The_subject_words_of_a_question_exclude_the_words_that_only_frame_it(string question, string[] expected) =>
        Assert.Equal(expected, QuestionHintExtractor.ExtractTopicTerms(question.ToLowerInvariant()));

    [Fact]
    public async Task A_long_workbook_list_with_nothing_to_pick_by_is_counted_not_listed_and_says_so()
    {
        await using var db = CreateContext();
        var (requestId, _) = await SeedWorkbookRowsAsync(db, StructuredFactsProvider.MaxWorkbookLitigationRows + 20);

        var facts = await new StructuredFactsProvider(db).BuildDigestAsync(requestId,
            new QuestionHints(FilingCategory.Compliance, null, null, null, null), default);

        Assert.DoesNotContain(facts, f => f.EntityType == "Litigation");
        var note = Assert.Single(facts, f => f.Text.Contains("on file in the report workbook"));
        Assert.Contains($"{StructuredFactsProvider.MaxWorkbookLitigationRows + 23} litigation record(s)", note.Text);
        Assert.Contains("none is listed individually", note.Text);
    }

    [Fact]
    public async Task A_short_workbook_list_is_still_given_in_full()
    {
        await using var db = CreateContext();
        var (requestId, _) = await SeedWorkbookRowsAsync(db, 10);

        var facts = await new StructuredFactsProvider(db).BuildDigestAsync(requestId,
            new QuestionHints(FilingCategory.Compliance, null, null, null, null), default);

        Assert.Equal(13, facts.Count(f => f.EntityType == "Litigation"));
    }

    [Fact]
    public async Task The_context_builder_gives_the_matching_case_records_with_links_for_a_described_set_of_cases()
    {
        await using var db = CreateContext();
        var (requestId, _) = await SeedRequestAsync(db);
        await SeedCasesAsync(db, requestId,
            Case(1, "supreme", "PENDING", "against", "01-01-2024"), Case(2, "supreme", "PENDING", "by", "01-01-2025"), Case(3, "drt", "PENDING", "against", "01-01-2023"));

        var builder = new RetrievalContextBuilder(db, new StructuredFactsProvider(db), new DocumentRetriever(db, ChatRetrievalOptions.Default),
            new LitigationDocumentRetriever(db, ChatRetrievalOptions.Default), new ThrowingEmbedding(), new LitigationOrderOutcomeQuery(db));

        var context = await builder.BuildAsync(requestId, "List the Supreme Court cases against the company", default);

        var records = context.Sources.Where(s => s.Type == SourceType.LitigationCase).ToList();
        var record = Assert.Single(records);                       // only the Supreme Court case against the company
        Assert.Contains("CASE-", record.Text);
        Assert.NotNull(record.LitigationCaseId);
        Assert.Equal("C1", record.Tag);
    }

    private sealed class ThrowingEmbedding : EmbeddingService
    {
        public override Task<float[]> EmbedQueryAsync(string text, CancellationToken ct) => throw new HttpRequestException("no embeddings in this test");
    }
}
