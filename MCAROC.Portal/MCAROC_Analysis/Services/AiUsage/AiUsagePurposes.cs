namespace MCAROC_Analysis.Services.AiUsage;

/// <summary>What a paid call was for. The cost report maps each purpose to the thing that drives its spend (filings, orders, cases, or the assignment itself).</summary>
public static class AiUsagePurposes
{
    public const string FilingExtraction = "FilingExtraction";
    public const string EmbeddingFilings = "EmbeddingFilings";
    public const string EmbeddingOrders = "EmbeddingOrders";
    public const string Embedding = "Embedding";
    public const string OrderClassification = "LitigationOrderClassification";
    public const string CaseAnalysis = "LitigationCaseAnalysis";
    public const string LitigationPortfolio = "LitigationPortfolio";
    public const string Copilot = "Copilot";
    public const string PropertyParticulars = "PropertyParticulars";
    public const string ChargesNarrative = "ChargesNarrative";
    public const string CrossSection = "CrossSectionAnalysis";
    public const string CalculationAudit = "CalculationAudit";
    public const string BorrowerExtraction = "BorrowerExtraction";
    public const string GroundedSearch = "GroundedSearch";
    public const string KeywordSuggestion = "LitigationKeywordSuggestion";
    public const string LitigationAttribution = "LitigationAttribution";
}
