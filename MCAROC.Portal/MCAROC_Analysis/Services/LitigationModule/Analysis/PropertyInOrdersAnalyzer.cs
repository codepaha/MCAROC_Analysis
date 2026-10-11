using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.LitigationModule.Analysis;

public enum PropertyMentionLevel
{
    /// <summary>No property or asset mentions detected (routine procedural listing, adjournment, or pure debt/cheque claim).</summary>
    None,

    /// <summary>Generic asset or immovable property restraint without specific cadastral, survey, or address details
    /// (e.g. "restrained from alienating moveable or immoveable property of the corporate debtor / promoters").</summary>
    GenericRestraint,

    /// <summary>Specific asset or premises identified (e.g. flat number, project name, commercial unit, plant & machinery).</summary>
    IdentifiableAsset,

    /// <summary>Cadastral land schedule or cadastral markers (survey no, khasra, khatauni, CTS, plot, boundaries, or schedule of property).</summary>
    SurveyOrLandSchedule
}

public enum ChargeMatchingFeasibility
{
    Impossible,
    Low,
    High
}

public sealed record PropertyMentionObservation(
    PropertyMentionLevel Level,
    ChargeMatchingFeasibility Feasibility,
    IReadOnlyList<string> MatchedKeywords,
    IReadOnlyList<string> SurveyMarkers,
    IReadOnlyList<string> Excerpts,
    string Summary);

public sealed record OrderPropertyAnalysisResult(
    string OrderIdentifier,
    string? CourtForum,
    int TextLength,
    bool IsProcedural,
    PropertyMentionObservation PropertyObservation);

public sealed record CorpusPropertyReport(
    int TotalOrdersAnalyzed,
    int ProceduralOrdersCount,
    int SubstantiveOrdersCount,
    int OrdersWithSurveyOrScheduleCount,
    int OrdersWithIdentifiableAssetCount,
    int OrdersWithGenericRestraintCount,
    int OrdersWithNoPropertyMentionCount,
    IReadOnlyDictionary<string, int> ForumBreakdown,
    IReadOnlyList<OrderPropertyAnalysisResult> Findings);

/// <summary>Analyzes court orders (DRT, Consumer, NCLT, High Court, District Court) to detect whether and how property
/// details appear in fetched texts, and assesses feasibility of cross-referencing against ROC charge particulars (plan section 4.5).</summary>
public static partial class PropertyInOrdersAnalyzer
{
    [GeneratedRegex(@"\b(schedule\s+of\s+prop(?:erty|erties)|description\s+of\s+(?:mortgaged\s+)?prop(?:erty|erties)|schedule\s+['""]?[a-z]['""]?|boundaries\s*:|bounded\s+on\s+the|on\s+the\s+north|on\s+the\s+south)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ScheduleMarkers();

    [GeneratedRegex(@"\b(khewat\s*(?:no\.?)?\s*\d+[\d/]*|khatauni\s*(?:no\.?)?\s*\d+[\d/]*|khasra\s*(?:no\.?)?\s*\d+[\d/]*|survey\s*(?:no\.?)?\s*\d+[\d/]*|s\.?\s*no\.?\s*\d+[\d/]*|cts\s*(?:no\.?)?\s*\d+[\d/]*|gat\s*(?:no\.?)?\s*\d+[\d/]*|dag\s*(?:no\.?)?\s*\d+[\d/]*|patta\s*(?:no\.?)?\s*\d+[\d/]*)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CadastralMarkers();

    [GeneratedRegex(@"\b(flat\s*(?:no\.?)?\s*[\w\d-]+|plot\s*(?:no\.?)?\s*[\w\d-]+|unit\s*(?:no\.?)?\s*[\w\d-]+|shop\s*(?:no\.?)?\s*[\w\d-]+|plant\s+(?:and|&)\s+machinery|factory\s+premises|mortgaged\s+(?:flat|house|land|property))\b", RegexOptions.IgnoreCase)]
    private static partial Regex AssetMarkers();

    [GeneratedRegex(@"\b(immov(?:eable|able|eble)\s+prop(?:erty|erties)|alienat(?:e|ing)\s+(?:any\s+)?(?:mov(?:eable|able)\s+or\s+)?immov(?:eable|able|eble)|restrain(?:ed)?\s+(?:from\s+)?(?:[a-z\s]+)?(?:alienating|dealing\s+with|transferring|disposing\s+of)\s+(?:[a-z\s]+)?(?:assets?|prop(?:erty|erties))|secured\s+assets?|mortgaged\s+assets?|interim\s+injunction\s+qua\s+the\s+(?:immov(?:eable|able)\s+)?prop(?:erty|erties))\b", RegexOptions.IgnoreCase)]
    private static partial Regex GenericRestraintMarkers();

    [GeneratedRegex(@"\b(adjourned|listed\s+on|posted\s+to|fixed\s+for|notice\s+issued|matter\s+be\s+listed|re-notified|appearance|vakalat\s+filed|summons\s+served|called\s+absent|set\s+exparte|call\s+on\s+\d{1,2}[./-]\d{1,2}[./-]\d{2,4})\b", RegexOptions.IgnoreCase)]
    private static partial Regex ProceduralMarkers();

    public static OrderPropertyAnalysisResult AnalyzeOrder(string orderText, string identifier, string? courtForum = null)
    {
        if (string.IsNullOrWhiteSpace(orderText))
        {
            return new OrderPropertyAnalysisResult(
                identifier,
                courtForum,
                0,
                true,
                new PropertyMentionObservation(
                    PropertyMentionLevel.None,
                    ChargeMatchingFeasibility.Impossible,
                    [],
                    [],
                    [],
                    "Order text is empty or blank."));
        }

        var clean = orderText.Trim();
        var length = clean.Length;
        var scheduleMatches = ScheduleMarkers().Matches(clean);
        var cadastralMatches = CadastralMarkers().Matches(clean);
        var assetMatches = AssetMarkers().Matches(clean);
        var genericMatches = GenericRestraintMarkers().Matches(clean);

        var isProcedural = length < 1000 && ProceduralMarkers().IsMatch(clean) && cadastralMatches.Count == 0 && assetMatches.Count == 0;

        var surveyMarkers = cadastralMatches.Select(m => m.Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        PropertyMentionLevel level;
        ChargeMatchingFeasibility feasibility;
        string summary;

        if (cadastralMatches.Count > 0)
        {
            level = PropertyMentionLevel.SurveyOrLandSchedule;
            feasibility = ChargeMatchingFeasibility.High;
            summary = $"Cadastral land or property schedule identified with {surveyMarkers.Count} cadastral markers ({string.Join(", ", surveyMarkers.Take(3))}). High feasibility for charge matching.";
            foreach (Match m in cadastralMatches) keywords.Add(m.Value.Trim());
            foreach (Match m in scheduleMatches) keywords.Add(m.Value.Trim());
            foreach (Match m in assetMatches) keywords.Add(m.Value.Trim());
        }
        else if (assetMatches.Count > 0)
        {
            level = PropertyMentionLevel.IdentifiableAsset;
            feasibility = ChargeMatchingFeasibility.Low;
            summary = scheduleMatches.Count > 0
                ? $"Property schedule references specific asset or premises ({string.Join(", ", assetMatches.Select(m => m.Value).Take(2))}) without cadastral survey numbers. Low feasibility for charge matching."
                : $"Specific asset or premises identified ({string.Join(", ", assetMatches.Select(m => m.Value).Take(2))}). Low-to-medium feasibility for charge matching.";
            foreach (Match m in assetMatches) keywords.Add(m.Value.Trim());
            foreach (Match m in scheduleMatches) keywords.Add(m.Value.Trim());
        }
        else if (genericMatches.Count > 0)
        {
            level = PropertyMentionLevel.GenericRestraint;
            feasibility = ChargeMatchingFeasibility.Impossible;
            summary = "Generic immovable property or asset restraint without specific land/address markers. Cannot be matched to individual ROC charges.";
            foreach (Match m in genericMatches) keywords.Add(m.Value.Trim());
        }
        else
        {
            level = PropertyMentionLevel.None;
            feasibility = ChargeMatchingFeasibility.Impossible;
            summary = scheduleMatches.Count > 0
                ? "Procedural or textual reference to property schedule/boundaries without extractable cadastral numbers or identifiable asset particulars."
                : (isProcedural
                    ? "Routine procedural order (adjournment, appearance, or hearing listing); no property mentioned."
                    : "Substantive order without property particulars (monetary award, contract dispute, or tax appeal).");
        }

        var excerpts = keywords.Count > 0 ? ExtractExcerpts(clean, keywords) : [];

        return new OrderPropertyAnalysisResult(
            identifier,
            courtForum ?? InferCourtForum(clean),
            length,
            isProcedural,
            new PropertyMentionObservation(
                level,
                feasibility,
                keywords.ToList(),
                surveyMarkers,
                excerpts,
                summary));
    }

    public static CorpusPropertyReport GenerateReport(IEnumerable<(string Identifier, string Text, string? Forum)> orders)
    {
        var results = orders.Select(o => AnalyzeOrder(o.Text, o.Identifier, o.Forum)).ToList();

        var forumCounts = results
            .GroupBy(r => r.CourtForum ?? "Unknown", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        return new CorpusPropertyReport(
            TotalOrdersAnalyzed: results.Count,
            ProceduralOrdersCount: results.Count(r => r.IsProcedural),
            SubstantiveOrdersCount: results.Count(r => !r.IsProcedural),
            OrdersWithSurveyOrScheduleCount: results.Count(r => r.PropertyObservation.Level == PropertyMentionLevel.SurveyOrLandSchedule),
            OrdersWithIdentifiableAssetCount: results.Count(r => r.PropertyObservation.Level == PropertyMentionLevel.IdentifiableAsset),
            OrdersWithGenericRestraintCount: results.Count(r => r.PropertyObservation.Level == PropertyMentionLevel.GenericRestraint),
            OrdersWithNoPropertyMentionCount: results.Count(r => r.PropertyObservation.Level == PropertyMentionLevel.None),
            ForumBreakdown: forumCounts,
            Findings: results);
    }

    private static string InferCourtForum(string text)
    {
        var sample = text.Length > 500 ? text[..500] : text;
        if (sample.Contains("DISTRICT CONSUMER", StringComparison.OrdinalIgnoreCase) ||
            sample.Contains("STATE CONSUMER", StringComparison.OrdinalIgnoreCase) ||
            sample.Contains("NATIONAL CONSUMER", StringComparison.OrdinalIgnoreCase) ||
            sample.Contains("CONSUMER FORUM", StringComparison.OrdinalIgnoreCase))
            return "Consumer";

        if (sample.Contains("DEBT RECOVERY", StringComparison.OrdinalIgnoreCase) ||
            sample.Contains("DRT", StringComparison.OrdinalIgnoreCase) ||
            sample.Contains("Presiding Officer", StringComparison.OrdinalIgnoreCase) ||
            sample.Contains("Honble P.O.", StringComparison.OrdinalIgnoreCase))
            return "DRT";

        if (sample.Contains("HIGH COURT", StringComparison.OrdinalIgnoreCase))
            return "High Court";

        if (sample.Contains("NCLAT", StringComparison.OrdinalIgnoreCase))
            return "NCLAT";

        if (sample.Contains("NCLT", StringComparison.OrdinalIgnoreCase))
            return "NCLT";

        if (sample.Contains("SUPREME COURT", StringComparison.OrdinalIgnoreCase))
            return "Supreme Court";

        if (sample.Contains("आयकर", StringComparison.OrdinalIgnoreCase) ||
            sample.Contains("ITAT", StringComparison.OrdinalIgnoreCase))
            return "ITAT";

        return "District Court / Other";
    }

    private static IReadOnlyList<string> ExtractExcerpts(string text, IEnumerable<string> keywords, int contextChars = 80)
    {
        var excerpts = new List<string>();
        foreach (var kw in keywords.Take(5))
        {
            var idx = text.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var start = Math.Max(0, idx - contextChars);
                var end = Math.Min(text.Length, idx + kw.Length + contextChars);
                var snippet = text[start..end].Replace('\r', ' ').Replace('\n', ' ').Trim();
                excerpts.Add(snippet);
            }
        }
        return excerpts;
    }
}

