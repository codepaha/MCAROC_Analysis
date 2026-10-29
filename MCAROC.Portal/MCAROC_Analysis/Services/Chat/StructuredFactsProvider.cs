using MCAROC_Analysis.Services.PropertyParticulars;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MCAROC_Analysis.Services.Chat;

/// <summary>One fact for the prompt. <see cref="DocumentId"/> and <see cref="PageNumber"/> name the filing document page it is read from, when it
/// is read from one (a certificate of change of name), so its citation opens that page.</summary>
public record StructuredFact(string DomainKey, string Text, string? EntityType, long? EntityId, long? IngestionRunId = null,
    long? DocumentId = null, int? PageNumber = null, string? DocumentName = null);

/// <summary>Builds a source-traceable structured-facts digest from Phase 1 entities for one request.
/// Broadened beyond a single "latest 2 years, current directors" snapshot — includes current AND historical
/// directors, 3-5 years of financials, open AND recently-satisfied charges — so a question like "who
/// resigned in 2022?" or "what was FY2022 revenue?" is actually answerable from the digest, not just the
/// newest state. Reuses QuestionHintExtractor's output to decide *emphasis*, not exclusion: a domain the
/// hints flag as relevant gets full per-row detail; every other domain still gets at least a one-line
/// headline (never dropped entirely) so the digest never goes silent on a domain the hints didn't catch.</summary>
public class StructuredFactsProvider(AppDbContext db, IMemoryCache? cache = null)
{
    private static readonly string[] AllDomains = ["Directors", "Financial", "Charges", "Gst", "Epfo", "Litigation"];

    /// <summary>Charge rows listed individually in one prompt (the largest, plus any the question points at).</summary>
    internal const int MaxChargeRows = 40;
    internal const int MaxHoldersListed = 12;

    public async Task<List<StructuredFact>> BuildDigestAsync(long requestId, QuestionHints hints, CancellationToken ct, string? question = null)
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
        var wording = EntityWording.For(request.EntityType);
        if (profile is not null && wording.IsLlp)
        {
            // An LLP (#539): its LLPIN, its partners' contribution in place of capital, and who contributed what.
            facts.Add(new StructuredFact("CompanyProfile",
                $"{profile.CompanyName} | Limited Liability Partnership | LLPIN: {profile.Llpin ?? request.Llpin ?? request.Cin} | Status: {profile.CompanyStatus} | Incorporated: {profile.IncorporationDate} | " +
                $"Partners' contribution: obligation ₹{profile.LlpObligationOfContributionCrore} Cr, received ₹{profile.LlpContributionReceivedCrore} Cr",
                "CompanyProfile", profile.CompanyProfileId));
            var structure = await db.CompanyStructures.AsNoTracking().FirstOrDefaultAsync(x => x.IngestionRunId == ingestionRunId, ct);
            var contributions = await db.LlpContributions.AsNoTracking().Where(x => x.IngestionRunId == ingestionRunId)
                .OrderBy(x => x.Kind).ThenBy(x => x.DisplayOrder).ToListAsync(ct);
            if (structure is not null || contributions.Count > 0)
                facts.Add(new StructuredFact("Directors",
                    $"{structure?.LlpDesignatedPartners?.ToString() ?? "?"} designated partner(s), {structure?.LlpPartners?.ToString() ?? "?"} other partner(s). Contributions: "
                    + (contributions.Count == 0 ? "none listed." : string.Join("; ", contributions.Select(c =>
                        $"{c.Name} ({(c.Kind == LlpContributorKind.BodyCorporate ? "body corporate" : c.Designation ?? "partner")}): received ₹{c.ContributionReceivedCrore} Cr of ₹{c.ObligationOfContributionCrore} Cr"
                        + (c.AsOnDate is { } d ? $" as on {d:d MMM yyyy}" : ""))) + "."),
                    "LlpContribution", null, ingestionRunId));
        }
        else if (profile is not null)
            facts.Add(new StructuredFact("CompanyProfile",
                $"{profile.CompanyName} | CIN: {profile.Cin} | Status: {profile.CompanyStatus} | Compliance: {profile.ComplianceStatus} | Incorporated: {profile.IncorporationDate}",
                "CompanyProfile", profile.CompanyProfileId));

        facts.AddRange(await NameChangeFactsAsync(requestId, profile?.CompanyName ?? request.CompanyName, ingestionRunId, ct));

        var directors = await db.Directors.Where(x => x.IngestionRunId == ingestionRunId).ToListAsync(ct);
        if (directors.Count > 0)
        {
            if (detailed.Contains("Directors"))
                foreach (var d in directors)
                    facts.Add(new StructuredFact("Directors",
                        $"{(wording.IsLlp ? "Designated partner" : "Director")}: {d.NameRaw} ({d.Designation}) | Appointed: {d.OriginalAppointmentDate} | "
                            + (d.CessationDate is null ? "Current" : $"Ceased: {d.CessationDate}"),
                        "Director", d.DirectorId));
            else
            {
                var current = directors.Count(d => d.CessationDate is null);
                facts.Add(new StructuredFact("Directors",
                    $"{current} current {wording.Director}(s), {directors.Count - current} historical (ceased) {wording.Director}(s) on record.", null, null));
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
                // What each charge says about its property (the register's own particulars, latest event): the only link from a question about a
                // piece of land to the charges on it. Without it the model can name only the charge whose deed text it happened to retrieve.
                var latestEvents = await LatestPropertyEventsAsync(relevant.Select(c => c.ChargeId).ToList(), ct);
                var propertyByCharge = latestEvents.ToDictionary(kv => kv.Key, kv => kv.Value.PropertyParticulars!);
                // Gemini has already split each charge's particulars into properties with grounded survey numbers (stored, so no new call). Its reading and the
                // regex reading are complementary: Gemini recovers long lists the regex stops early on, the regex finds numbers Gemini drops (on a sample of 300
                // real addresses each found identifiers the other missed), so a charge's identifiers are both.
                var extractions = await PropertyParticularsExtractionService.LoadCompletedAsync(db, latestEvents.Values, ct);
                var idsByCharge = latestEvents.ToDictionary(kv => kv.Key, kv => ChargeIds(kv.Value, extractions));
                var questionIds = PropertyIds(question);
                var asksAboutProperty = AsksAboutProperty(question);
                bool NamesProperty(RocCharge c) => questionIds.Count > 0 && idsByCharge.TryGetValue(c.ChargeId, out var charged)
                    && SharesProperty(questionIds, charged);
                bool Asked(RocCharge c) =>
                    (hints.LenderNameKeyword is { } lender && c.LatestChargeHolderRaw.Contains(lender, StringComparison.OrdinalIgnoreCase))
                    || (hints.Year is { } year && (c.CreationDate?.Year == year || c.SatisfactionDate?.Year == year))
                    || NamesProperty(c);
                var listed = relevant.OrderByDescending(Asked).ThenByDescending(c => c.CurrentAmount ?? 0m).ThenBy(c => c.RocChargeNumber, StringComparer.Ordinal)
                    .Take(Math.Max(MaxChargeRows, relevant.Count(Asked))).ToList();
                foreach (var c in listed.OrderByDescending(c => c.CurrentAmount ?? 0m).ThenBy(c => c.RocChargeNumber, StringComparer.Ordinal))
                    facts.Add(new StructuredFact("Charges",
                        $"Charge {c.RocChargeNumber}: Holder {c.LatestChargeHolderRaw}, Amount ₹{c.CurrentAmount} Cr, Status {c.ChargeStatus}"
                            + (c.CreationDate is { } created ? $", Created {created:yyyy-MM-dd}" : "")
                            + (c.SatisfactionDate is { } sd ? $", Satisfied {sd:yyyy-MM-dd}" : "")
                            + (propertyByCharge.TryGetValue(c.ChargeId, out var property) && (asksAboutProperty || NamesProperty(c))
                                ? $", Property per register: {Clip(property, MaxPropertyChars)}" : ""),
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

        var withRun = facts.Select(f => f with { IngestionRunId = ingestionRunId }).ToList();
        withRun.AddRange(await BuildFetchedLitigationFactsAsync(requestId, hints, request.CompanyName, ingestionRunId, ct));
        return withRun;
    }

    /// <summary>The litigation cases fetched from the data service (the Litigation tab's cases), summarised: how many, how many pending
    /// and disposed, how many are by or against the company, by court type, and how many have a next hearing. These are the request's only
    /// litigation facts: the workbook's own legal-history sheets are not read. The cases are those of the latest completed report.
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
            .Where(l => l.LitigationReportSnapshotId == snapshotId && l.Case!.MergedIntoCaseId == null)
            .Where(MCAROC_Analysis.Services.LitigationData.Attribution.LitigationAttribution.IsShownLink)   // #489: only the company's cases
            .Select(l => l.Case!).ToListAsync(ct);
        if (cases.Count == 0) return [];

        var names = await CompanyNamesAsync(requestId, companyName, ingestionRunId, ct);
        var summary = new LitigationForumSummary();
        var pending = 0; var disposed = 0; var by = 0; var against = 0; var both = 0; var withNext = 0;
        // status x side: [pending, disposed, unknown] x [against, by, both sides, not determined]
        var crossTab = new int[3, Enum.GetValues<LitigationForumSummary.SideColumn>().Length];
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
            else if (side == LitigationCompanySide.Both) both++;
            if (!string.IsNullOrWhiteSpace(c.NextHearingDate)) withNext++;
            crossTab[(int)bucket, (int)LitigationForumSummary.ColumnOf(side)]++;
        }
        var courts = string.Join(", ", cases.GroupBy(c => string.IsNullOrWhiteSpace(c.Type) ? "other" : c.Type!.Trim().ToLowerInvariant())
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"));
        var text = $"{cases.Count} litigation case(s) fetched from the litigation data service for this company: {pending} pending, {disposed} disposed, "
            + $"{cases.Count - pending - disposed} of unknown status; {against} against the company, {by} by the company"
            + (both > 0 ? $", {both} with the company on both sides" : "")
            + (by + against + both < cases.Count ? $", {cases.Count - by - against - both} of unstated direction" : "")
            + $"; {withNext} with a next hearing date. By court type: {courts}.";
        var facts = new List<StructuredFact> { new("Litigation", text, null, null) };

        string Row(string label, LitigationCaseStatusBucket bucket) =>
            $"{label}: {crossTab[(int)bucket, (int)LitigationForumSummary.SideColumn.Against]} against the company, {crossTab[(int)bucket, (int)LitigationForumSummary.SideColumn.By]} by the company, "
            + $"{crossTab[(int)bucket, (int)LitigationForumSummary.SideColumn.Both]} with the company on both sides, {crossTab[(int)bucket, (int)LitigationForumSummary.SideColumn.NotDetermined]} side not determined";
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
                + $"by the company {Part(LitigationForumSummary.SideColumn.By)}; company on both sides {Part(LitigationForumSummary.SideColumn.Both)}; "
                + $"side not determined {Part(LitigationForumSummary.SideColumn.NotDetermined)}";
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

    /// <summary>#497: every change of the company's name, oldest first, as the name history and the certificates of change of name in its filings state
    /// it (the Name History table's rows). A change a certificate states is dated by it and cites its page; a date the two disagree on, or one only
    /// one of them gives, is said. Always given, whatever the question: a few lines, and the names decide whose a case or a document is.</summary>
    internal async Task<List<StructuredFact>> NameChangeFactsAsync(long requestId, string companyName, long ingestionRunId, CancellationToken ct)
    {
        var history = await db.CompanyNameHistories.AsNoTracking().Where(h => h.RequestId == requestId && h.IngestionRunId == ingestionRunId)
            .OrderBy(h => h.DisplayOrder).ToListAsync(ct);
        IReadOnlyList<CertificateNameChange> certificates = [];
        try { certificates = await NameChangeCertificateLoader.LoadAsync(db, requestId, ct, cache); }
        catch (Exception ex) when (ex is not OperationCanceledException) { }   // the name history alone, as the page shows it then
        var changes = NameChanges.Merge(history, companyName, certificates);
        if (changes.Count == 0) return [];

        var documents = certificates.Count == 0 ? new Dictionary<long, string>()
            : await db.McaFilingDocuments.AsNoTracking().Where(d => certificates.Select(c => c.FilingDocumentId).Contains(d.FilingDocumentId))
                .ToDictionaryAsync(d => d.FilingDocumentId, d => d.OriginalFileName, ct);
        var facts = new List<StructuredFact>
        {
            new("CompanyProfile", $"The company has changed its name {changes.Count} time(s); its current name is {companyName}. Former names, oldest first: "
                + string.Join("; ", changes.Select(c => c.FromName)) + ".", null, null)
        };
        foreach (var c in changes)
        {
            var when = c.Date is { } d ? $" on {d:d MMM yyyy}" : " (date not known)";
            var source = c.Check switch
            {
                NameChangeCheck.Confirmed => $"the name history and the certificate of change of name (page {c.Certificate!.Page}) agree",
                NameChangeCheck.DatesDiffer => $"the date is the certificate of change of name's (page {c.Certificate!.Page}); the name history gives {c.HistoryDate:d MMM yyyy}",
                NameChangeCheck.CertificateUndated => $"the certificate of change of name (page {c.Certificate!.Page}) states the change but its date could not be read; the date is the name history's",
                NameChangeCheck.HistoryUndated => $"the date is the certificate of change of name's (page {c.Certificate!.Page}); the name history gives no date",
                NameChangeCheck.CertificateOnly => $"from the certificate of change of name (page {c.Certificate!.Page}); the name history does not list it",
                _ => "from the name history; no certificate of change of name was read for it",
            };
            var text = $"Name change: renamed from \"{c.FromName}\" to \"{c.ToName}\"{when} ({source}).";
            facts.Add(c.Certificate is { } cert
                ? new StructuredFact("CompanyProfile", text, nameof(McaFilingDocument), cert.FilingDocumentId, ingestionRunId, cert.FilingDocumentId, cert.Page,
                    documents.TryGetValue(cert.FilingDocumentId, out var file) ? file : null)
                : new StructuredFact("CompanyProfile", text, nameof(CompanyNameHistory), c.HistoryRow?.CompanyNameHistoryId, ingestionRunId));
        }
        return facts;
    }

    /// <summary>Characters of a charge's property particulars put in one prompt row.</summary>
    internal const int MaxPropertyChars = 320;

    private static string Clip(string text, int max)
    {
        var flat = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    // Property identifiers as Indian address and deed text writes them (checked against ~143,000 delivered property addresses): a list introduced by a label
    // (S NO, SY.NO., SURVEY NOS., RS NO, KH NO, KHASRA, KHATA, GAT, GUT, PLOT, FP, TPS, CTS, C.S.NO., RECT, HISSA), e.g. "S NO 358, 364" or
    // "Survey Nos. 3/ 2840, 3/ 2841 & 2842". An identifier may be multi-part (3/2840, 43/1/3/2, 1-2-2870) and may end in one letter (2A, 204/P). Numbers are
    // identifiers only here: a year, an area, a floor or a PIN code is never read as one because of its digits, but any number a label introduces is.
    private const string IdPattern = @"(?>\d+)[A-Za-z]?(?![A-Za-z])(?:\s*[/\-.]\s*(?:(?>\d+)[A-Za-z]?(?![A-Za-z])|[A-Za-z](?![A-Za-z])))*";
    private static readonly System.Text.RegularExpressions.Regex IdListAfterLabel = new(
        @"(?<![A-Za-z])(?:r\.?\s*s\.?\s*nos?|re\.?\s*sy\.?\s*nos?|sy\.?\s*nos?|s\.?\s*nos?|survey(?:\s*(?:nos?|numbers?))?|kh\.?\s*nos?|khas+a?ra(?:\s*nos?)?|khat+h?a(?:\s*nos?)?"
        + @"|gat|gut|plot(?:\s*nos?)?|f\.?\s*p(?:\s*nos?)?|t\.?\s*p\.?\s*s(?:\s*nos?)?|c\.?\s*t\.?\s*s(?:\s*nos?)?|c\.?\s*s\.?\s*nos?|rect(?:\s*nos?)?|hissa(?:\s*nos?)?|t\.?\s*s\.?\s*nos?)"
        + @"(?![A-Za-z])(?:\s*(?:nos?|numbers?)(?![A-Za-z]))?[.:\-\s]*(" + IdPattern + @"(?:\s*(?:,|&|;|\+|and|to)\s*" + IdPattern + @")*)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex OneId = new(IdPattern, System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex SlashPair = new(@"(?<![\w/])\d{1,3}\s*/\s*\d{3,6}(?![\d/])", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>A property identifier: its normalised text (spaces removed, upper case) and its numeric parts, so 3/2840 and 4/2840 are different identifiers.</summary>
    internal readonly record struct PropertyId(string Text, string[] Parts)
    {
        public bool IsBare => Parts.Length == 1;
        public bool Equals(PropertyId other) => Text == other.Text;
        public override int GetHashCode() => Text.GetHashCode();
    }

    internal static List<PropertyId> PropertyIds(string? text)
    {
        var ids = new Dictionary<string, PropertyId>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return [];
        void Add(string raw)
        {
            // Whatever a label introduces is an identifier, however short ("Survey No. 42") or long ("Survey No. 123456"): the text says it is one.
            var normalised = System.Text.RegularExpressions.Regex.Replace(raw, @"\s+", "").ToUpperInvariant();
            var parts = System.Text.RegularExpressions.Regex.Matches(normalised, @"\d+").Select(m => m.Value).ToArray();
            if (parts.Length > 0) ids.TryAdd(normalised, new PropertyId(normalised, parts));
        }
        foreach (System.Text.RegularExpressions.Match list in IdListAfterLabel.Matches(text))
            foreach (System.Text.RegularExpressions.Match one in OneId.Matches(list.Groups[1].Value)) Add(one.Value);
        foreach (System.Text.RegularExpressions.Match pair in SlashPair.Matches(text)) Add(pair.Value);
        return [.. ids.Values];
    }

    /// <summary>True when the question and a charge's particulars name the same parcel: an identical identifier, or a bare number on one side that is a part of
    /// the other ("survey 2840" names 3/2840 and 4/2840; "3/2840" does not name 4/2840).</summary>
    internal static bool SharesProperty(IReadOnlyCollection<PropertyId> question, IReadOnlyCollection<PropertyId> particulars) =>
        question.Any(q => particulars.Any(p => p.Text == q.Text
            || (q.IsBare && !p.IsBare && p.Parts.Contains(q.Parts[0]) && q.Parts[0].Length >= 3)
            || (p.IsBare && !q.IsBare && q.Parts.Contains(p.Parts[0]) && p.Parts[0].Length >= 3)));

    /// <summary>True when the question is about a piece of land or other security, so every listed charge should show what it says about its property.</summary>
    internal static bool AsksAboutProperty(string? question) => question is not null
        && System.Text.RegularExpressions.Regex.IsMatch(question, @"\b(propert(y|ies)|land|plot|survey|CTS|premises|security|secured|collateral|asset|mortgage[ds]?)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>The most recent event of each charge that has property particulars (text and property type, which the stored extraction is keyed by).</summary>
    private async Task<Dictionary<long, RocChargeEvent>> LatestPropertyEventsAsync(IReadOnlyCollection<long> chargeIds, CancellationToken ct)
    {
        if (chargeIds.Count == 0) return [];
        var rows = await db.RocChargeEvents.AsNoTracking()
            .Where(e => chargeIds.Contains(e.RocChargeId) && e.PropertyParticulars != null && e.PropertyParticulars != "")
            .Select(e => new { e.RocChargeId, e.EventDate, e.ChargeEventId, e.PropertyParticulars, e.PropertyType }).ToListAsync(ct);
        return rows.GroupBy(r => r.RocChargeId).ToDictionary(g => g.Key, g =>
        {
            var latest = g.OrderByDescending(r => r.EventDate).ThenByDescending(r => r.ChargeEventId).First();
            return new RocChargeEvent { RocChargeId = latest.RocChargeId, PropertyParticulars = latest.PropertyParticulars, PropertyType = latest.PropertyType };
        });
    }

    /// <summary>A charge's property identifiers: the regex reading of its particulars plus the survey numbers of its stored Gemini extraction, if any.</summary>
    internal static List<PropertyId> ChargeIds(RocChargeEvent latest, IReadOnlyDictionary<string, PropertyParticularsExtraction> extractions)
    {
        var ids = PropertyIds(latest.PropertyParticulars);
        if (PropertyParticularsAi.IsExtractable(latest.PropertyParticulars)
            && extractions.TryGetValue(PropertyParticularsAi.HashOf(latest.PropertyParticulars!, latest.PropertyType), out var stored)
            && PropertyParticularsAi.Deserialize(stored.ExtractionJson) is { } result)
            foreach (var number in result.Properties.SelectMany(p => p.SurveyNumbers).SelectMany(g => g.Numbers))
            {
                var id = AsPropertyId(number);
                if (id is { } parsed && !ids.Contains(parsed)) ids.Add(parsed);
            }
        return ids;
    }

    private static PropertyId? AsPropertyId(string number)
    {
        var normalised = System.Text.RegularExpressions.Regex.Replace(number, @"\s+", "").ToUpperInvariant();
        var parts = System.Text.RegularExpressions.Regex.Matches(normalised, @"\d+").Select(m => m.Value).ToArray();
        return parts.Length == 0 ? null : new PropertyId(normalised, parts);
    }

    /// <summary>The company's current name and the names it has had, which a party list may use.</summary>
    internal Task<List<string>> CompanyNamesAsync(long requestId, string companyName, long? ingestionRunId, CancellationToken ct) =>
        LitigationCompanyNames.ForRequestAsync(db, requestId, ct);

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
