namespace MCAROC_Analysis.Services.LitigationData;

public sealed class LitigationAiAnalysisOptions
{
    public const string SectionName = "LitigationAiAnalysis";
    public int MaxAttempts { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 60;
    /// <summary>The model for the case analysis (the one call per case that reads every order). The other litigation calls — the
    /// per-order outcome classification and the portfolio synthesis — keep the client's default model.</summary>
    public string CaseModelId { get; set; } = "gemini-3.1-flash-lite";
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
    /// <summary>A company with more cases than this has its cases ranked (<see cref="LitigationCasePriority"/>) and only the highest-value ones
    /// read in full; the rest keep their court-record tier, and their orders are still classified. At or below it, every case is read.</summary>
    public int TieringAboveCases { get; set; } = 1000;
    /// <summary>The most cases read in full for one company once tiering applies (the highest scores first, insolvency always first).</summary>
    public int MaxAiCases { get; set; } = 1500;
    /// <summary>Case analyses read together in one portfolio-synthesis step, the highest risk first. Small on purpose: the step also sees the verified
    /// order quotes behind each case, and a model reads a handful of cases more carefully than hundreds. More than this many analysed cases are
    /// summarised batch by batch and the batch summaries combined.</summary>
    public int PortfolioBatchSize { get; set; } = 6;
    /// <summary>The most batch summaries combined in one step; more are combined in rounds.</summary>
    public int PortfolioFanIn { get; set; } = 20;
    /// <summary>Model calls in a row that get no reply (timeout, quota, server error) before the run decides the service is down and pauses.</summary>
    public int OutageAfterConsecutiveFailures { get; set; } = 10;
    /// <summary>How long a paused run waits before trying again; it does not use up the ordinary retry attempts.</summary>
    public int OutageRetryMinutes { get; set; } = 10;
    /// <summary>How many times a run may pause for an outage before it is marked failed.</summary>
    public int OutageMaxAttempts { get; set; } = 8;
    /// <summary>Order classifications in flight at once (each is a short call).</summary>
    public int OrderConcurrency { get; set; } = 6;
}
