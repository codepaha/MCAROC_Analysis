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
    public int CaseTimeoutSeconds { get; set; } = 600;
    /// <summary>A case whose order text sent to the model is longer than this is analysed in groups of orders (newest first), each in its own
    /// call, and the group results are merged: one call over a very long case times out.</summary>
    public int SplitAboveChars { get; set; } = 100_000;
    /// <summary>The most order text sent in one group call.</summary>
    public int GroupChars { get; set; } = 80_000;
    /// <summary>Case analyses in flight at once. A run used to analyse its cases one after another, so a few hundred cases took many hours; each
    /// result is also saved the moment it completes, so a restart or a lost lease never discards paid work.</summary>
    public int CaseConcurrency { get; set; } = 4;
    /// <summary>Order classifications in flight at once (each is a short call).</summary>
    public int OrderConcurrency { get; set; } = 6;
}
