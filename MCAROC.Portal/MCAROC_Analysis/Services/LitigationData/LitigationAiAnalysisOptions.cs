namespace MCAROC_Analysis.Services.LitigationData;

public sealed class LitigationAiAnalysisOptions
{
    public const string SectionName = "LitigationAiAnalysis";
    public int MaxAttempts { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 60;
    /// <summary>The model for the case analysis (the one call per case that reads every order). The other litigation calls — the
    /// per-order outcome classification and the portfolio synthesis — keep the client's default model.</summary>
    public string CaseModelId { get; set; } = "gemini-2.5-flash";
    /// <summary>A case call reads every order of a case and returns a large JSON object; it takes minutes, not seconds.</summary>
    public int CaseTimeoutSeconds { get; set; } = 240;
}
