using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationModule.Reading;
using Microsoft.EntityFrameworkCore;

using MCAROC_Analysis.Services.LitigationModule.Freshness;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Another case in the same report that shares a party (other than the company) with this one.</summary>
public sealed record RelatedLitigationCase(
    long LitigationCaseId, string? CaseNumber, string? Court, LitigationCaseStatusBucket Status, LitigationRiskTier Tier, string SharedParty);

/// <summary>Whether the case's own orders tie it to the company by an identifier they print, and which identifiers they name.</summary>
public sealed record LitigationIdentityEvidence(
    IdentityEvidenceStatus Status, IReadOnlyList<MatchedOrderIdentifier> Identifiers, int OrdersWithText, int OrdersNamingCompany = 0);

/// <summary>Everything the standalone page of one litigation case shows.</summary>
public sealed record LitigationCasePage(
    McaRequest Request, LitigationCaseCardViewModel Card, DateTime RetrievedUtc, IReadOnlyList<ChargeLitigationLink> ChargeLinks,
    IReadOnlyList<RelatedLitigationCase> RelatedCases, LitigationIdentityEvidence Identity, CaseAttributionNote? Attribution = null,
    MCAROC_Analysis.Services.PropertyParticulars.CaseOrderProperties? OrderProperties = null);

/// <summary>#489: why the case is, or is not, the company's. <see cref="Counted"/> false: set aside, opened from the set-aside list, shown with its reason.</summary>
public sealed record CaseAttributionNote(bool Counted, LitigationAttributionOutcome? Outcome, string? Rule, string? Party, string? Reason);

/// <summary>Loads one case of a request's current litigation report for its own page: the same card the Litigation tab builds
/// (identity, age, baseline risk tier, parties, orders with outcomes and property matches, any stored analysis), plus the charge it
/// bears on and the other cases it shares a party with. A case outside the request's current report is not found.</summary>
public sealed class LitigationCasePageService(
    AppDbContext db,
    ChargeLitigationService? chargeLitigation = null,
    LitigationOrderOutcomeQuery? outcomeQuery = null,
    LitigationReaderGate? readerGate = null,
    ICourtRefreshStatusService? courtRefreshStatus = null)
{
    public async Task<LitigationCasePage?> GetAsync(long requestId, long caseId, CancellationToken ct)
    {
        var request = await db.Requests.AsNoTracking().FirstOrDefaultAsync(r => r.RequestId == requestId, ct);
        if (request is null) return null;

        var snapshot = await AuthoritativeSnapshotAsync(requestId, ct);
        if (snapshot is null) return null;
        // The same definition of "the request's cases" as the register (plan section 12.6): the gate picks legacy or the module.
        var caseSource = readerGate is null ? CaseSetSource.Legacy : await readerGate.ResolveCasePageAsync(requestId, ct);
        var inReport = await LitigationCaseSets.ForAsync(db, caseSource, requestId, snapshot.LitigationReportSnapshotId, ct);
        var counted = await inReport.AnyAsync(id => id == caseId, ct);
        // #489: a case the search returned but that is set aside (not the company's, or not confirmed) opens too, from the set-aside list, marked as not counted.
        if (!counted && !await db.LitigationCaseSourceReports.AsNoTracking().AnyAsync(sr => sr.LitigationCaseId == caseId
                && sr.LitigationReportSnapshotId == snapshot.LitigationReportSnapshotId && sr.Case!.MergedIntoCaseId == null
                && sr.Case.AttributionOutcome != null && sr.Case.AttributionOutcome != LitigationAttributionOutcome.Attributed, ct))
            return null;

        var theCase = await db.LitigationCases.AsNoTracking().Include(c => c.Orders).FirstOrDefaultAsync(c => c.LitigationCaseId == caseId, ct);
        if (theCase is null) return null;

        var orderIds = theCase.Orders.Select(o => o.LitigationCaseOrderId).ToList();
        var orderDocs = (await db.LitigationOrderDocuments.AsNoTracking().Where(d => orderIds.Contains(d.LitigationCaseOrderId)).ToListAsync(ct))
            .GroupBy(d => d.LitigationCaseOrderId).ToDictionary(g => g.Key, g => g.First());
        // #554: the properties the case's orders name, from the stored readings.
        var readingDocIds = orderDocs.Values.Select(d => d.LitigationOrderDocumentId).ToList();
        var readings = await db.LitigationOrderPropertyReadings.AsNoTracking().Where(r => readingDocIds.Contains(r.LitigationOrderDocumentId))
            .ToDictionaryAsync(r => r.LitigationOrderDocumentId, ct);
        var orderProperties = MCAROC_Analysis.Services.PropertyParticulars.CaseOrderProperties.Build(
            theCase.Orders.Where(o => o.CollapsedIntoOrderId == null && !o.IsSuperseded).OrderBy(o => o.LitigationCaseOrderId)
                .Where(o => orderDocs.TryGetValue(o.LitigationCaseOrderId, out var d) && d.Status == LitigationOrderDocumentStatus.Downloaded && d.ExtractedText != null)
                .Select(o => (o, orderDocs[o.LitigationCaseOrderId])).ToList(),
            readings);

        // Order text is no longer word-matched to the company's premises or charged property (#554): any number and any word made a "match"
        // (426 false links on one company). The properties each order names are read and compared field by field instead (#554 PR 3).
        var propertyMatches = new Dictionary<long, List<LitigationPropertyMatchResult>>();
        // Each property against the open charges (#554): the charged asset itself, a unit of a charged project, or not under an open charge.
        var openCharges = await ChargeLitigationService.OpenChargesAsync(db, requestId, request.LatestCompletedIngestionRunId, ct);
        var chargedProperties = MCAROC_Analysis.Services.PropertyParticulars.OrderPropertyChargeMatcher.ChargedProperties(openCharges,
            await MCAROC_Analysis.Services.PropertyParticulars.PropertyParticularsExtractionService.LoadCompletedAsync(db, openCharges.SelectMany(c => c.Events), ct));
        orderProperties = orderProperties.WithTies(chargedProperties, await ChargeLitigationService.CompanyNamesAsync(db, requestId, ct));
        var companyProfile = await db.CompanyProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.RequestId == requestId, ct);

        var outcomes = await (outcomeQuery ?? new LitigationOrderOutcomeQuery(db)).FindAsync(requestId, Enum.GetValues<LitigationOrderOutcome>(), ct);
        var outcomeByOrder = outcomes.Matches.Where(m => orderIds.Contains(m.LitigationCaseOrderId)).GroupBy(m => m.LitigationCaseOrderId).ToDictionary(g => g.Key, g => g.First());

        var run = await db.LitigationAiAnalysisRuns.AsNoTracking().Where(r => r.RequestId == requestId).OrderByDescending(r => r.RunNumber).FirstOrDefaultAsync(ct);
        var caseAi = new Dictionary<long, LitigationCaseAiAnalysis>();
        if (run is not null && await db.LitigationCaseAiAnalyses.AsNoTracking()
                .FirstOrDefaultAsync(a => a.LitigationAiAnalysisRunId == run.LitigationAiAnalysisRunId && a.LitigationCaseId == caseId, ct) is { } ai)
            caseAi[caseId] = ai;

        var companyNames = await CompanyNamesAsync(request, ct);
        var asOf = DateOnly.FromDateTime(Ist.FromUtc(snapshot.RetrievedUtc));
        var card = LitigationCaseCardMapper.Map(theCase, new LitigationCaseMapContext(
            orderDocs, propertyMatches, outcomeByOrder, outcomes, caseAi, run?.RunNumber ?? 0, asOf, companyNames));

        var statusSvc = courtRefreshStatus ?? new CourtRefreshStatusService(db);
        card.Freshness = await statusSvc.GetCaseFreshnessAsync(requestId, caseId, ct);

        IReadOnlyList<ChargeLitigationLink> links = [];
        if (chargeLitigation is not null)
        {
            try { links = (await chargeLitigation.GetAsync(requestId, ct)).Links.Where(l => l.Case.LitigationCaseId == caseId).ToList(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* the page stands without the charge links */ }
        }

        var identity = await IdentityEvidenceAsync(request, companyProfile, theCase, orderDocs, companyNames, ct);
        return new LitigationCasePage(request, card, snapshot.RetrievedUtc, links, await RelatedAsync(inReport, card, companyNames, ct), identity,
            new CaseAttributionNote(counted, theCase.AttributionOutcome, theCase.AttributionRule, theCase.AttributionParty, theCase.AttributionReason),
            orderProperties);
    }

    /// <summary>Where an old link to a merged duplicate goes: the surviving case of the same request. Only when the case page is served from the module (plan section 12.6,
    /// "old URLs keep resolving"); on legacy a merged loser is simply not found, as before. Follows a chain of up to <see cref="MaxMergeHops"/> merges and never loops.</summary>
    public const int MaxMergeHops = 8;

    public async Task<long?> GetSurvivorIdAsync(long requestId, long caseId, CancellationToken ct)
    {
        if (readerGate is null || await readerGate.ResolveCasePageAsync(requestId, ct) != CaseSetSource.Module) return null;
        var current = caseId;
        // MaxMergeHops links are MaxMergeHops + 1 cases, so the terminal case is looked up once more after the last allowed hop.
        for (var hop = 0; hop <= MaxMergeHops; hop++)
        {
            var next = await db.LitigationCases.AsNoTracking().Where(c => c.LitigationCaseId == current && c.RequestId == requestId)
                .Select(c => c.MergedIntoCaseId).FirstOrDefaultAsync(ct);
            if (next is not { } survivor) return current == caseId ? null : current;
            current = survivor;
        }
        return null;
    }

    /// <summary>Reads the identifiers printed in the case's own order texts and ties them to the company: its CIN/LLPIN, PAN, GSTINs and its
    /// directors' DINs. Order texts are in the database (they outlive the retained PDFs), so this needs no file.</summary>
    private async Task<LitigationIdentityEvidence> IdentityEvidenceAsync(
        McaRequest request, CompanyProfile? profile, LitigationCase theCase, IReadOnlyDictionary<long, LitigationOrderDocument> orderDocs,
        IReadOnlyCollection<string> companyNames, CancellationToken ct)
    {
        var runId = request.LatestCompletedIngestionRunId;
        var gstins = runId is { } gr ? await db.GstRegistrations.AsNoTracking().Where(g => g.IngestionRunId == gr && g.Gstin != "").Select(g => g.Gstin).Distinct().ToListAsync(ct) : [];
        var dins = runId is { } dr ? await db.Directors.AsNoTracking().Where(d => d.IngestionRunId == dr && d.Din != "").Select(d => d.Din).Distinct().ToListAsync(ct) : [];
        var company = new CompanyIdentity(
            request.Cin ?? profile?.Cin, request.Llpin ?? profile?.Llpin, request.Pan ?? profile?.Pan, gstins, dins);

        var cores = companyNames.Select(LitigationCompanySides.Core).Where(c => c.Length > 0).Distinct().ToList();
        var matched = new List<MatchedOrderIdentifier>();
        var withText = 0;
        var namingCompany = 0;
        foreach (var order in theCase.Orders.OrderByDescending(o => LitigationCaseAges.ParseDate(o.OrderDate) ?? DateOnly.MinValue))
        {
            if (!orderDocs.TryGetValue(order.LitigationCaseOrderId, out var doc) || string.IsNullOrWhiteSpace(doc.ExtractedText)) continue;
            withText++;
            if (LitigationCompanySides.NamesCompany(doc.ExtractedText, cores)) namingCompany++;
            foreach (var id in OrderIdentifiers.Extract(doc.ExtractedText))
                matched.Add(new MatchedOrderIdentifier(id, OrderIdentifiers.Classify(id, company), order.LitigationCaseOrderId, order.OrderDate, order.OrderType, doc.LitigationOrderDocumentId));
        }
        // The company's own identifiers first, then directors', then anyone else's.
        var ordered = matched.OrderBy(m => m.Match).ThenBy(m => m.Identifier.Type).ToList();
        return new LitigationIdentityEvidence(OrderIdentifiers.StatusOf(ordered, withText), ordered, withText, namingCompany);
    }

    /// <summary>The report the Litigation tab shows: the current attempt's snapshot when it completed, else the job's latest completed one.</summary>
    private async Task<LitigationReportSnapshot?> AuthoritativeSnapshotAsync(long requestId, CancellationToken ct)
    {
        var job = await db.LitigationSearchJobs.AsNoTracking().FirstOrDefaultAsync(j => j.RequestId == requestId, ct);
        if (job is null) return null;
        if (!string.IsNullOrWhiteSpace(job.RawResponseHash))
        {
            var current = await db.LitigationReportSnapshots.AsNoTracking()
                .FirstOrDefaultAsync(s => s.LitigationSearchJobId == job.LitigationSearchJobId && s.ReportHash == job.RawResponseHash, ct);
            if (current is { Status: LitigationReportSnapshotStatus.Completed }) return current;
        }
        return await db.LitigationReportSnapshots.AsNoTracking()
            .Where(s => s.LitigationSearchJobId == job.LitigationSearchJobId && s.Status == LitigationReportSnapshotStatus.Completed)
            .OrderByDescending(s => s.RetrievedUtc).FirstOrDefaultAsync(ct);
    }

    private Task<List<string>> CompanyNamesAsync(McaRequest request, CancellationToken ct) =>
        LitigationCompanyNames.ForAsync(db, request.RequestId, request.CompanyName, request.Cin ?? request.Llpin, request.LatestCompletedIngestionRunId, ct);

    /// <summary>The other cases of the report that name the same party as this one (the company itself excluded), up to ten, highest tier first.</summary>
    private async Task<IReadOnlyList<RelatedLitigationCase>> RelatedAsync(
        IQueryable<long> inReport, LitigationCaseCardViewModel card, IReadOnlyCollection<string> companyNames, CancellationToken ct)
    {
        var companyCores = companyNames.Select(LitigationCompanySides.Core).Where(c => c.Length > 0).ToList();
        var mine = card.Petitioners.Concat(card.Respondents)
            .Where(p => !LitigationCompanySides.IsCompany(p, companyCores))
            .Select(p => (Name: p, Core: LitigationCompanySides.Core(p))).Where(p => p.Core.Length >= 4)
            .GroupBy(p => p.Core).ToDictionary(g => g.Key, g => g.First().Name);
        if (mine.Count == 0) return [];

        var others = await db.LitigationCases.AsNoTracking()
            .Where(c => inReport.Contains(c.LitigationCaseId) && c.LitigationCaseId != card.LitigationCaseId)
            .Select(c => new
            {
                c.LitigationCaseId, c.CaseNumber, c.Court, c.CaseStatus, c.CaseStage, c.Type, c.CourtCategory, c.CaseType, c.Act, c.CaseClassification,
                c.ProceedingType, c.PetitionersJson, c.RespondentsJson, c.Direction, c.AttributionGrade
            }).ToListAsync(ct);

        var related = new List<RelatedLitigationCase>();
        foreach (var o in others)
        {
            var petitioners = LitigationCaseCardMapper.ParseNames(o.PetitionersJson);
            var respondents = LitigationCaseCardMapper.ParseNames(o.RespondentsJson);
            var shared = petitioners.Concat(respondents).Select(LitigationCompanySides.Core).FirstOrDefault(core => mine.ContainsKey(core));
            if (shared is null) continue;
            var status = LitigationCaseStatusClassifier.Classify(o.CaseStatus, o.CaseStage);
            var side = LitigationCompanySides.Determine(petitioners, respondents, companyNames, o.Direction);
            var tier = LitigationBaselineRisk.Assess(new LitigationRiskInput(
                o.Type, o.Court, o.CourtCategory, o.CaseType, o.Act, o.CaseStage, o.CaseClassification, o.ProceedingType, status, side, petitioners,
                o.AttributionGrade)).Tier;
            related.Add(new RelatedLitigationCase(o.LitigationCaseId, o.CaseNumber, o.Court, status, tier, mine[shared]));
        }
        return related.OrderByDescending(r => r.Tier).ThenBy(r => r.LitigationCaseId).Take(10).ToList();
    }
}
