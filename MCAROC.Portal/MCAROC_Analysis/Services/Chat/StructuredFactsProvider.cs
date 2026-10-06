using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.Chat;

public record StructuredFact(string DomainKey, string Text, string? EntityType, long? EntityId, long? IngestionRunId = null);

/// <summary>Builds a source-traceable structured-facts digest from Phase 1 entities for one request.
/// Broadened beyond a single "latest 2 years, current directors" snapshot — includes current AND historical
/// directors, 3-5 years of financials, open AND recently-satisfied charges — so a question like "who
/// resigned in 2022?" or "what was FY2022 revenue?" is actually answerable from the digest, not just the
/// newest state. Reuses QuestionHintExtractor's output to decide *emphasis*, not exclusion: a domain the
/// hints flag as relevant gets full per-row detail; every other domain still gets at least a one-line
/// headline (never dropped entirely) so the digest never goes silent on a domain the hints didn't catch.</summary>
public class StructuredFactsProvider(AppDbContext db)
{
    private static readonly string[] AllDomains = ["Directors", "Financial", "Charges", "Gst", "Epfo", "Litigation"];

    /// <summary>Charge rows listed individually in one prompt (the largest, plus any the question points at).</summary>
    internal const int MaxChargeRows = 40;
    internal const int MaxHoldersListed = 12;
    internal const int MaxWorkbookLitigationRows = 40;

    public async Task<List<StructuredFact>> BuildDigestAsync(long requestId, QuestionHints hints, CancellationToken ct)
    {
        var request = await db.Requests.FirstAsync(r => r.RequestId == requestId, ct);
        var facts = new List<StructuredFact>();
        if (request.LatestCompletedIngestionRunId is not { } ingestionRunId)
        {
            // No MCA ingestion, but the litigation data fetched from the data service stands on its own.
            facts.AddRange(await BuildFetchedLitigationFactsAsync(requestId, hints, request.CompanyName, null, ct));
            return facts;
        }

        var detailed = DetermineDetailedDomains(hints);

        var profile = await db.CompanyProfiles.FirstOrDefaultAsync(x => x.IngestionRunId == ingestionRunId, ct);
        if (profile is not null)
            facts.Add(new StructuredFact("CompanyProfile",
                $"{profile.CompanyName} | CIN: {profile.Cin} | Status: {profile.CompanyStatus} | Compliance: {profile.ComplianceStatus} | Incorporated: {profile.IncorporationDate}",
                "CompanyProfile", profile.CompanyProfileId));

        var directors = await db.Directors.Where(x => x.IngestionRunId == ingestionRunId).ToListAsync(ct);
        if (directors.Count > 0)
        {
            if (detailed.Contains("Directors"))
                foreach (var d in directors)
                    facts.Add(new StructuredFact("Directors",
                        $"Director: {d.NameRaw} ({d.Designation}) | Appointed: {d.OriginalAppointmentDate} | "
                            + (d.CessationDate is null ? "Current" : $"Ceased: {d.CessationDate}"),
                        "Director", d.DirectorId));
            else
            {
                var current = directors.Count(d => d.CessationDate is null);
                facts.Add(new StructuredFact("Directors",
                    $"{current} current director(s), {directors.Count - current} historical (ceased) director(s) on record.", null, null));
            }
        }

        // Standalone is the default basis. Every financial fact is tagged with its basis so a question
        // about "consolidated revenue" resolves to the consolidated figure and an untagged question to
        // standalone.
        var standalone = await db.FinancialYearData
            .Where(x => x.IngestionRunId == ingestionRunId && x.Basis == FinancialBasis.Standalone)
            .OrderByDescending(x => x.FinancialYear).Take(5).ToListAsync(ct);
        if (standalone.Count > 0)
        {
            if (detailed.Contains("Financial"))
                foreach (var f in standalone)
                    facts.Add(new StructuredFact("Financial",
                        $"FY{f.FinancialYear} (Standalone): Revenue ₹{f.Revenue} Cr, EBITDA ₹{f.Ebitda} Cr, PAT ₹{f.Pat} Cr, Net Worth ₹{f.NetWorth} Cr, Total Debt ₹{f.TotalDebt} Cr",
                        "FinancialYearData", f.FinancialId));
            else
            {
                var latest = standalone[0];
                facts.Add(new StructuredFact("Financial",
                    $"Latest standalone financials: FY{latest.FinancialYear} revenue ₹{latest.Revenue} Cr, PAT ₹{latest.Pat} Cr. ({standalone.Count} year(s) of data on record.)",
                    "FinancialYearData", latest.FinancialId));
            }
        }

        var consolidated = await db.FinancialYearData
            .Where(x => x.IngestionRunId == ingestionRunId && x.Basis == FinancialBasis.Consolidated)
            .OrderByDescending(x => x.FinancialYear).Take(5).ToListAsync(ct);
        foreach (var f in consolidated)
            facts.Add(new StructuredFact("Financial",
                $"FY{f.FinancialYear} (Consolidated): Revenue ₹{f.Revenue} Cr, EBITDA ₹{f.Ebitda} Cr, PAT ₹{f.Pat} Cr, Net Worth ₹{f.NetWorth} Cr, Total Debt ₹{f.TotalDebt} Cr",
                "FinancialYearData", f.FinancialId));

        var charges = await db.RocCharges.Where(x => x.IngestionRunId == ingestionRunId).ToListAsync(ct);
        if (charges.Count > 0)
        {
            // The counts and totals are always stated, never left for the model to work out from rows: counting a few hundred
            // rows is exactly where a language model drifts (it once answered 106 for 163 open charges).
            var open = charges.Where(c => c.SatisfactionDate is null).ToList();
            var openTotal = open.Sum(c => c.CurrentAmount ?? 0m);
            facts.Add(new StructuredFact("Charges",
                $"{open.Count} open charge(s), {charges.Count - open.Count} satisfied charge(s) on record ({charges.Count} in total); the open charges total ₹{openTotal:N2} Cr.", null, null));
            if (open.Count > 0)
            {
                var holders = open.GroupBy(c => c.LatestChargeHolderRaw).Select(g => (Holder: g.Key, Count: g.Count(), Amount: g.Sum(c => c.CurrentAmount ?? 0m)))
                    .OrderByDescending(h => h.Amount).ThenBy(h => h.Holder, StringComparer.Ordinal).ToList();
                facts.Add(new StructuredFact("Charges",
                    $"Open charges by holder, largest total first: " + string.Join("; ", holders.Take(MaxHoldersListed).Select(h => $"{h.Holder}: {h.Count} charge(s), ₹{h.Amount:N2} Cr"))
                        + (holders.Count > MaxHoldersListed ? $"; and {holders.Count - MaxHoldersListed} other holder(s)." : "."), null, null));
            }

            var recentCutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-2));
            var relevant = charges.Where(c => c.SatisfactionDate is null || c.SatisfactionDate >= recentCutoff).ToList();
            // Per-row detail only when the question is about charges and there is an open or recent charge to show.
            if (detailed.Contains("Charges") && relevant.Count > 0)
            {
                // The largest charges, plus every charge the question points at (a lender it names, a year it asks about), so a capped
                // list never hides the row asked for.
                bool Asked(RocCharge c) =>
                    (hints.LenderNameKeyword is { } lender && c.LatestChargeHolderRaw.Contains(lender, StringComparison.OrdinalIgnoreCase))
                    || (hints.Year is { } year && (c.CreationDate?.Year == year || c.SatisfactionDate?.Year == year));
                var listed = relevant.OrderByDescending(Asked).ThenByDescending(c => c.CurrentAmount ?? 0m).ThenBy(c => c.RocChargeNumber, StringComparer.Ordinal)
                    .Take(Math.Max(MaxChargeRows, relevant.Count(Asked))).ToList();
                foreach (var c in listed.OrderByDescending(c => c.CurrentAmount ?? 0m).ThenBy(c => c.RocChargeNumber, StringComparer.Ordinal))
                    facts.Add(new StructuredFact("Charges",
                        $"Charge {c.RocChargeNumber}: Holder {c.LatestChargeHolderRaw}, Amount ₹{c.CurrentAmount} Cr, Status {c.ChargeStatus}"
                            + (c.CreationDate is { } created ? $", Created {created:yyyy-MM-dd}" : "")
                            + (c.SatisfactionDate is { } sd ? $", Satisfied {sd:yyyy-MM-dd}" : ""),
                        "RocCharge", c.ChargeId));
                if (listed.Count < relevant.Count)
                    facts.Add(new StructuredFact("Charges",
                        $"Only {listed.Count} of the {relevant.Count} open or recently satisfied charges are listed individually (the largest, and any the question points at); the counts and totals above cover all of them.", null, null));
            }
        }

        var gstRegs = await db.GstRegistrations.Where(x => x.IngestionRunId == ingestionRunId).ToListAsync(ct);
        if (gstRegs.Count > 0)
        {
            if (detailed.Contains("Gst"))
                foreach (var g in gstRegs)
                    facts.Add(new StructuredFact("Gst", $"GSTIN {g.Gstin} ({g.State}): {g.Status}", "GstRegistration", g.GstId));
            else
                facts.Add(new StructuredFact("Gst",
                    $"{gstRegs.Count(GstRegistrationStatus.IsActive)} active GST registration(s) of {gstRegs.Count} on record.", null, null));
        }

        var epfo = await db.EpfoContributions.Where(x => x.IngestionRunId == ingestionRunId)
            .OrderByDescending(x => x.WageMonth).Take(6).ToListAsync(ct);
        if (epfo.Count > 0)
        {
            if (detailed.Contains("Epfo"))
                foreach (var e in epfo)
                    facts.Add(new StructuredFact("Epfo",
                        $"{e.WageMonth}: {e.EmployeeCount} employees, ₹{e.ContributionAmountCrore} Cr, {e.PaymentStatus}", "EpfoContribution", e.EpfoId));
            else
                facts.Add(new StructuredFact("Epfo", $"EPFO contribution data available for {epfo.Count} recent month(s) on record.", null, null));
        }

        var litigations = await db.Litigations.Where(x => x.IngestionRunId == ingestionRunId).ToListAsync(ct);
        if (litigations.Count > 0)
        {
            if (detailed.Contains("Litigation") && litigations.Count <= MaxWorkbookLitigationRows)
                foreach (var l in litigations)
                {
                    var courtSuffix = string.IsNullOrWhiteSpace(l.Court) ? "" : $", Court: {l.Court}";
                    facts.Add(new StructuredFact("Litigation",
                        $"{l.CaseType} ({l.CaseStatus}): {l.Litigants}, Case No. {l.CaseNumber}{courtSuffix}", "Litigation", l.LitigationId));
                }
            else
                facts.Add(new StructuredFact("Litigation", $"{litigations.Count} litigation record(s) on file in the report workbook"
                    + (litigations.Count > MaxWorkbookLitigationRows ? " (too many to list one by one here; the litigation cases summary and case records cover the court proceedings)." : "."), null, null));
        }

        var withRun = facts.Select(f => f with { IngestionRunId = ingestionRunId }).ToList();
        withRun.AddRange(await BuildFetchedLitigationFactsAsync(requestId, hints, request.CompanyName, ingestionRunId, ct));
        return withRun;
    }

    /// <summary>The litigation cases fetched from the data service (the Litigation tab's cases), summarised: how many, how many pending
    /// and disposed, how many are by or against the company, by court type, and how many have a next hearing. The structured facts above
    /// read only the report sheet's Litigations rows, which a fetched search never fills — so without this the assistant would say
    /// "no litigation on file" for a company whose tab lists dozens of cases. The cases are those of the latest completed report.
    /// The company's side and each case's forum are worked out exactly as the page and the report do (the same rules), and the summary
    /// states the combinations a question asks about (pending cases against the company, cases per forum), so none has to be counted from
    /// a list. When the question describes a set of cases, the size of that set is stated too.</summary>
    private async Task<List<StructuredFact>> BuildFetchedLitigationFactsAsync(
        long requestId, QuestionHints hints, string companyName, long? ingestionRunId, CancellationToken ct)
    {
        var snapshotId = await db.LitigationReportSnapshots.AsNoTracking()
            .Where(s => s.RequestId == requestId && s.Status == LitigationReportSnapshotStatus.Completed)
            .OrderByDescending(s => s.RetrievedUtc).ThenByDescending(s => s.LitigationReportSnapshotId)
            .Select(s => (long?)s.LitigationReportSnapshotId).FirstOrDefaultAsync(ct);
        if (snapshotId is null) return [];

        var cases = await db.LitigationCaseSourceReports.AsNoTracking()
            .Where(l => l.LitigationReportSnapshotId == snapshotId)
            .Select(l => l.Case!).ToListAsync(ct);
        if (cases.Count == 0) return [];

        var names = await CompanyNamesAsync(requestId, companyName, ingestionRunId, ct);
        var summary = new LitigationForumSummary();
        var pending = 0; var disposed = 0; var by = 0; var against = 0; var withNext = 0;
        // status x side: [pending, disposed, unknown] x [against, by, not determined]
        var crossTab = new int[3, 3];
        foreach (var c in cases)
        {
            var bucket = LitigationCaseStatusClassifier.Classify(c.CaseStatus, c.CaseStage);
            var side = LitigationCasePresentation.Side(c.Direction, c.PetitionersJson, c.RespondentsJson, names);
            summary.Add(LitigationCasePresentation.Resolve(c.Type, c.Court, c.CourtCategory), side, bucket);
            switch (bucket)
            {
                case LitigationCaseStatusBucket.Pending: pending++; break;
                case LitigationCaseStatusBucket.Disposed: disposed++; break;
            }
            if (side == LitigationCompanySide.ByCompany) by++;
            else if (side == LitigationCompanySide.AgainstCompany) against++;
            if (!string.IsNullOrWhiteSpace(c.NextHearingDate)) withNext++;
            crossTab[(int)bucket, (int)LitigationForumSummary.ColumnOf(side)]++;
        }
        var courts = string.Join(", ", cases.GroupBy(c => string.IsNullOrWhiteSpace(c.Type) ? "other" : c.Type!.Trim().ToLowerInvariant())
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"));
        var text = $"{cases.Count} litigation case(s) fetched from the litigation data service for this company: {pending} pending, {disposed} disposed, "
            + $"{cases.Count - pending - disposed} of unknown status; {against} against the company, {by} by the company"
            + (by + against < cases.Count ? $", {cases.Count - by - against} of unstated direction" : "")
            + $"; {withNext} with a next hearing date. By court type: {courts}.";
        var facts = new List<StructuredFact> { new("Litigation", text, null, null) };

        string Row(string label, LitigationCaseStatusBucket bucket) =>
            $"{label}: {crossTab[(int)bucket, 0]} against the company, {crossTab[(int)bucket, 1]} by the company, {crossTab[(int)bucket, 2]} side not determined";
        facts.Add(new StructuredFact("Litigation",
            $"Litigation cases by status and the company's side: {Row("pending", LitigationCaseStatusBucket.Pending)}; {Row("disposed", LitigationCaseStatusBucket.Disposed)}; "
            + $"{Row("status not reported", LitigationCaseStatusBucket.Unknown)}.", null, null));

        var forums = Enum.GetValues<LitigationInstitution>().Where(f => summary.ForForum(f).Total > 0).Select(f =>
        {
            string Part(LitigationForumSummary.SideColumn side)
            {
                var c = summary.Cell(f, side);
                return $"{c.Total} ({c.Pending} pending, {c.Disposed} disposed, {c.Unknown} unknown)";
            }
            return $"{LitigationCasePresentation.Label(f)}: {summary.ForForum(f).Total} in all — against the company {Part(LitigationForumSummary.SideColumn.Against)}; "
                + $"by the company {Part(LitigationForumSummary.SideColumn.By)}; side not determined {Part(LitigationForumSummary.SideColumn.NotDetermined)}";
        });
        facts.Add(new StructuredFact("Litigation", "Litigation cases by forum: " + string.Join(". ", forums) + ".", null, null));

        if (hints.CaseList is { } filter)
        {
            var (listed, total) = LitigationCaseListSelector.Select(cases, filter, names);
            facts.Add(new StructuredFact("Litigation",
                $"Cases matching the question ({filter.Describe()}): {total} of the {cases.Count} case(s)."
                + (total == 0 ? " None match." : total > listed.Count
                    ? $" The first {listed.Count} are given as litigation case records (pending first, then by forum seniority, cases against the company first, newest filing first); the rest are not listed."
                    : " All of them are given as litigation case records."), null, null));
        }
        return facts;
    }

    /// <summary>The company's current name and the names it has had, which a party list may use.</summary>
    internal async Task<List<string>> CompanyNamesAsync(long requestId, string companyName, long? ingestionRunId, CancellationToken ct)
    {
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(companyName)) names.Add(companyName);
        if (ingestionRunId is { } runId)
            names.AddRange(await db.CompanyNameHistories.AsNoTracking().Where(h => h.RequestId == requestId && h.IngestionRunId == runId)
                .Select(h => h.PreviousName).ToListAsync(ct));
        return names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
    }

    private static HashSet<string> DetermineDetailedDomains(QuestionHints hints)
    {
        // No hint signal at all: keep today's full breadth rather than guessing what's relevant.
        if (!hints.HasSoftHints)
            return [.. AllDomains];

        var set = new HashSet<string>();
        if (hints.Category == FilingCategory.Charge) set.Add("Charges");
        if (hints.Category == FilingCategory.Financial) set.Add("Financial");
        if (hints.Category == FilingCategory.Compliance)
        {
            set.Add("Directors");
            set.Add("Gst");
            set.Add("Epfo");
            set.Add("Litigation");
        }
        if (hints.LenderNameKeyword is not null) set.Add("Charges");
        if (hints.Year is not null) set.Add("Financial");

        // A hint fired but matched nothing domain-specific (e.g. only a form-type token) — still show a
        // sane default core rather than leaving every domain at headline level.
        if (set.Count == 0)
        {
            set.Add("Directors");
            set.Add("Financial");
        }
        return set;
    }
}
