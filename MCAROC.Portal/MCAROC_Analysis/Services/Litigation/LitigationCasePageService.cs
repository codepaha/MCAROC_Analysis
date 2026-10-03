using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using Microsoft.EntityFrameworkCore;

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
    IReadOnlyList<RelatedLitigationCase> RelatedCases, LitigationIdentityEvidence Identity);

/// <summary>Loads one case of a request's current litigation report for its own page: the same card the Litigation tab builds
/// (identity, age, baseline risk tier, parties, orders with outcomes and property matches, any stored analysis), plus the charge it
/// bears on and the other cases it shares a party with. A case outside the request's current report is not found.</summary>
public sealed class LitigationCasePageService(AppDbContext db, ChargeLitigationService? chargeLitigation = null, LitigationOrderOutcomeQuery? outcomeQuery = null)
{
    public async Task<LitigationCasePage?> GetAsync(long requestId, long caseId, CancellationToken ct)
    {
        var request = await db.Requests.AsNoTracking().FirstOrDefaultAsync(r => r.RequestId == requestId, ct);
        if (request is null) return null;

        var snapshot = await AuthoritativeSnapshotAsync(requestId, ct);
        if (snapshot is null) return null;
        var inReport = db.LitigationCaseSourceReports.AsNoTracking()
            .Where(sr => sr.LitigationReportSnapshotId == snapshot.LitigationReportSnapshotId).Select(sr => sr.LitigationCaseId);
        if (!await inReport.AnyAsync(id => id == caseId, ct)) return null;

        var theCase = await db.LitigationCases.AsNoTracking().Include(c => c.Orders).FirstOrDefaultAsync(c => c.LitigationCaseId == caseId, ct);
        if (theCase is null) return null;

        var orderIds = theCase.Orders.Select(o => o.LitigationCaseOrderId).ToList();
        var orderDocs = (await db.LitigationOrderDocuments.AsNoTracking().Where(d => orderIds.Contains(d.LitigationCaseOrderId)).ToListAsync(ct))
            .GroupBy(d => d.LitigationCaseOrderId).ToDictionary(g => g.Key, g => g.First());

        var companyProfile = await db.CompanyProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.RequestId == requestId, ct);
        var establishments = await db.EpfoEstablishments.AsNoTracking().Where(e => e.RequestId == requestId).ToListAsync(ct);
        var charges = await db.RocCharges.AsNoTracking().Where(c => c.RequestId == requestId && c.SatisfactionDate == null).Include(c => c.Events).ToListAsync(ct);
        var propertyMatches = LitigationOrderAddressMatcher.MatchCases(LitigationOrderAddressMatcher.BuildAddressPool(companyProfile, establishments, charges), [theCase], orderDocs)
            .GroupBy(m => m.LitigationCaseOrderId).ToDictionary(g => g.Key, g => g.ToList());

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

        IReadOnlyList<ChargeLitigationLink> links = [];
        if (chargeLitigation is not null)
        {
            try { links = (await chargeLitigation.GetAsync(requestId, ct)).Links.Where(l => l.Case.LitigationCaseId == caseId).ToList(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* the page stands without the charge links */ }
        }

        var identity = await IdentityEvidenceAsync(request, companyProfile, theCase, orderDocs, companyNames, ct);
        return new LitigationCasePage(request, card, snapshot.RetrievedUtc, links, await RelatedAsync(inReport, card, companyNames, ct), identity);
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
        foreach (var order in theCase.Orders.OrderByDescending(o => o.OrderDate))
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

    private async Task<List<string>> CompanyNamesAsync(McaRequest request, CancellationToken ct)
    {
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.CompanyName)) names.Add(request.CompanyName);
        if (request.LatestCompletedIngestionRunId is { } runId)
            names.AddRange(await db.CompanyNameHistories.AsNoTracking().Where(h => h.IngestionRunId == runId && h.PreviousName != null && h.PreviousName != "")
                .Select(h => h.PreviousName!).ToListAsync(ct));
        return names;
    }

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
                c.ProceedingType, c.PetitionersJson, c.RespondentsJson, c.Direction
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
                o.Type, o.Court, o.CourtCategory, o.CaseType, o.Act, o.CaseStage, o.CaseClassification, o.ProceedingType, status, side, petitioners)).Tier;
            related.Add(new RelatedLitigationCase(o.LitigationCaseId, o.CaseNumber, o.Court, status, tier, mine[shared]));
        }
        return related.OrderByDescending(r => r.Tier).ThenBy(r => r.LitigationCaseId).Take(10).ToList();
    }
}
