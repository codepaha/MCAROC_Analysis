using System.Text.Json;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Everything the card of a case needs besides the case itself, gathered once for however many cases are being mapped.</summary>
public sealed record LitigationCaseMapContext(
    IReadOnlyDictionary<long, LitigationOrderDocument> OrderDocByOrderId,
    IReadOnlyDictionary<long, List<LitigationPropertyMatchResult>> PropertyMatchesByOrderId,
    IReadOnlyDictionary<long, OrderOutcomeMatch> OutcomeByOrderId,
    OrderOutcomeLookup OutcomeLookup,
    IReadOnlyDictionary<long, LitigationCaseAiAnalysis> CaseAiByCaseId,
    int LatestAnalysisRunNumber,
    DateOnly AsOf,
    IReadOnlyCollection<string> CompanyNames);

/// <summary>Builds the render-ready card of one litigation case (identity, dates, age, parties, orders with outcomes and property
/// matches, any stored analysis) — shared by the Litigation tab's card list and the case's own page, so the two can never disagree.</summary>
public static class LitigationCaseCardMapper
{
    public static List<string> ParseNames(string? json) => LitigationPartyNames.Parse(json);

    public static LitigationCaseCardViewModel Map(LitigationCase c, LitigationCaseMapContext ctx)
    {
        var status = LitigationCaseStatusClassifier.Classify(c.CaseStatus, c.CaseStage);
        var card = new LitigationCaseCardViewModel
        {
            LitigationCaseId = c.LitigationCaseId,
            CaseNumber = c.CaseNumber,
            Cnr = c.Cnr,
            CspId = c.CspId,
            ProviderCaseId = c.ProviderCaseId,
            Court = c.Court,
            Bench = c.Bench,
            CourtCategory = c.CourtCategory,
            State = c.State,
            District = c.District,
            CaseType = c.CaseType,
            CaseYear = c.CaseYear,
            CaseStage = c.CaseStage,
            CaseStatus = c.CaseStatus,
            StatusBucket = status,
            Act = c.Act,
            ProceedingType = c.ProceedingType,
            Direction = c.Direction,
            FilingDate = c.FilingDate,
            Age = LitigationCaseAges.Compute(c.FilingDate, c.DecisionDate, c.CaseYear, status, c.Orders.Select(o => o.OrderDate), ctx.AsOf, c.Cnr, c.CaseNumber),
            LastHearingDate = c.LastHearingDate,
            NextHearingDate = c.NextHearingDate,
            DecisionDate = c.DecisionDate,
            Petitioners = ParseNames(c.PetitionersJson),
            Respondents = ParseNames(c.RespondentsJson),
            PetitionerAdvocates = ParseNames(c.PetitionerAdvocatesJson),
            RespondentAdvocates = ParseNames(c.RespondentAdvocatesJson),
            FirstSeenUtc = c.FirstSeenUtc,
            LastSeenUtc = c.LastSeenUtc
        };
        card.CompanySide = LitigationCompanySides.Determine(card.Petitioners, card.Respondents, ctx.CompanyNames, c.Direction);
        card.Risk = LitigationBaselineRisk.Assess(new LitigationRiskInput(
            c.Type, c.Court, c.CourtCategory, c.CaseType, c.Act, c.CaseStage, c.CaseClassification, c.ProceedingType, status, card.CompanySide, card.Petitioners));

        foreach (var o in c.Orders.OrderByDescending(o => LitigationCaseAges.ParseDate(o.OrderDate) ?? DateOnly.MinValue))
        {
            ctx.OrderDocByOrderId.TryGetValue(o.LitigationCaseOrderId, out var od);
            var orderMatches = ctx.PropertyMatchesByOrderId.TryGetValue(o.LitigationCaseOrderId, out var omList) ? omList : [];
            ctx.OutcomeByOrderId.TryGetValue(o.LitigationCaseOrderId, out var outcomeMatch);

            card.Orders.Add(new LitigationOrderRowViewModel
            {
                LitigationCaseOrderId = o.LitigationCaseOrderId,
                LitigationOrderDocumentId = od?.LitigationOrderDocumentId,
                OrderDate = o.OrderDate,
                OrderType = o.OrderType,
                DocumentStatus = od?.Status,
                FailureReason = od?.FailureReason,
                RefreshCount = od?.RefreshCount ?? 0,
                Outcomes = outcomeMatch?.Outcomes.ToList() ?? [],
                FineAmount = outcomeMatch?.FineAmount,
                Confidence = outcomeMatch?.Confidence,
                EvidenceTruncated = outcomeMatch?.EvidenceTruncated ?? false,
                // Matches only carry orders WITH an outcome; an InsufficientEvidence classification is still current.
                ClassificationStatus = ctx.OutcomeLookup.CurrentStatusByOrderId?.TryGetValue(o.LitigationCaseOrderId, out var classified) == true
                    ? classified : null,
                PropertyMatches = orderMatches.Select(m => new LitigationPropertyMatchViewModel
                {
                    LitigationCaseOrderId = m.LitigationCaseOrderId,
                    OrderDate = m.OrderDate,
                    OrderType = m.OrderType,
                    PageNumber = m.PageNumber,
                    SourceLabel = m.SourceLabel,
                    AddressText = m.AddressText,
                    Strength = m.Strength,
                    MatchedPinCode = m.MatchedPinCode,
                    MatchedPlotNumbers = [.. m.MatchedPlotNumbers],
                    MatchedLocalities = [.. m.MatchedLocalities],
                    Excerpt = m.Excerpt,
                    IsCompanyPremises = m.IsCompanyPremises,
                    RocChargeId = m.RocChargeId,
                    RocChargeNumber = m.RocChargeNumber,
                    ChargeHolder = m.ChargeHolder
                }).ToList()
            });
        }
        card.PropertyMatches = card.Orders.SelectMany(o => o.PropertyMatches).ToList();

        if (ctx.CaseAiByCaseId.TryGetValue(c.LitigationCaseId, out var ca))
        {
            card.Analysis = MapAnalysis(ca, ctx.LatestAnalysisRunNumber);
            if (ca.CompletedUtc.HasValue && c.LastSeenUtc > ca.CompletedUtc.Value) card.IsAnalysisStaleComparedToCase = true;
        }
        return card;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()) ? p.GetString() : null;

    private static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    private static List<string> TextList(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => !string.IsNullOrWhiteSpace(x)).ToList() : [];

    /// <summary>Reads the v2.0 case analysis (every key is optional: a missing one leaves its field empty).</summary>
    private static void ReadDetailed(JsonElement root, LitigationCaseAiAnalysisViewModel vm)
    {
        vm.IsDetailed = true;
        vm.RiskLevel = Text(root, "risk");
        vm.BaselineRisk = Text(root, "baseline_risk");
        vm.RiskRaised = Flag(root, "risk_raised");
        vm.RiskTrigger = Text(root, "risk_trigger");
        vm.RiskReason = Text(root, "risk_reason");
        vm.Relevance = Text(root, "relevance");
        vm.CaseNature = Text(root, "case_nature");
        vm.TargetRole = Text(root, "target_party_role");
        vm.ExposureDirection = Text(root, "legal_exposure_direction");
        vm.Subject = Text(root, "subject");
        vm.Amount = Text(root, "amount");
        vm.ActiveRestraint = Flag(root, "active_restraint");
        vm.RestraintType = Text(root, "restraint_type");
        vm.LisPendens = Flag(root, "lis_pendens");
        vm.FactualBackground = Text(root, "factual_background");
        vm.ReliefSought = Text(root, "relief_sought");
        vm.CoreIssue = Text(root, "core_issue");
        vm.ConsequenceNote = Text(root, "consequence_note");
        vm.JudicialReasoning = TextList(root, "judicial_reasoning");
        vm.Provisions = TextList(root, "provisions");
        vm.Precedents = TextList(root, "precedents");
        vm.ConnectedMatters = TextList(root, "connected_matters");
        if (root.TryGetProperty("outcome", out var outcome) && outcome.ValueKind == JsonValueKind.Object)
        {
            vm.PetitionerResult = Text(outcome, "petitioner_result");
            vm.RespondentResult = Text(outcome, "respondent_result");
            vm.FinalOrderSummary = Text(outcome, "final_order_summary");
        }
        if (root.TryGetProperty("order_analysis", out var orders) && orders.ValueKind == JsonValueKind.Array)
            foreach (var o in orders.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object))
                vm.OrderFindings.Add(new LitigationAnalysisOrderFinding
                {
                    OrderId = o.TryGetProperty("order_id", out var oid) && oid.TryGetInt64(out var id) ? id : 0,
                    DocumentId = o.TryGetProperty("document_id", out var did) && did.TryGetInt64(out var d) ? d : null,
                    Date = Text(o, "date"), Type = Text(o, "type"), Summary = Text(o, "summary"),
                    KeyFindings = TextList(o, "key_findings"), OperativeOrder = Text(o, "operative_order")
                });
        if (root.TryGetProperty("evidence", out var evidence) && evidence.ValueKind == JsonValueKind.Array)
            foreach (var q in evidence.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object))
                vm.Quotes.Add(new LitigationAnalysisQuote
                {
                    Claim = Text(q, "claim") ?? "", Quote = Text(q, "quote") ?? "",
                    OrderId = q.TryGetProperty("order_id", out var qo) && qo.TryGetInt64(out var qid) ? qid : 0,
                    DocumentId = q.TryGetProperty("document_id", out var qd) && qd.TryGetInt64(out var qdid) ? qdid : null,
                    Page = q.TryGetProperty("page", out var qp) && qp.TryGetInt32(out var page) ? page : 0
                });
        vm.Unknowns = TextList(root, "unknowns");
    }

    public static LitigationCaseAiAnalysisViewModel MapAnalysis(LitigationCaseAiAnalysis ca, int runNumber)
    {
        var vm = new LitigationCaseAiAnalysisViewModel
        {
            LitigationCaseAiAnalysisId = ca.LitigationCaseAiAnalysisId,
            Status = ca.Status,
            CompletedUtc = ca.CompletedUtc,
            FailureReason = ca.FailureReason,
            RunNumber = runNumber
        };
        if (string.IsNullOrWhiteSpace(ca.AnalysisJson)) return vm;
        try
        {
            using var doc = JsonDocument.Parse(ca.AnalysisJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("summary", out var sProp)) vm.Summary = sProp.GetString();
            if (root.TryGetProperty("prompt_version", out _)) ReadDetailed(root, vm);
            if (root.TryGetProperty("unknowns", out var uProp) && uProp.ValueKind == JsonValueKind.Array)
                vm.Unknowns = uProp.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => !string.IsNullOrEmpty(x)).ToList();
            if (root.TryGetProperty("evidenceReferences", out var refProp) && refProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in refProp.EnumerateArray())
                {
                    var orderId = r.TryGetProperty("litigationCaseOrderId", out var oProp) ? oProp.GetInt64() : 0;
                    var pageNum = r.TryGetProperty("pageNumber", out var pProp) ? pProp.GetInt32() : 0;
                    vm.Citations.Add($"Order #{orderId} (p. {pageNum})");
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { /* keep what was read */ }
        return vm;
    }
}
