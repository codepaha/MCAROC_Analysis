namespace MCAROC_Analysis.Services.LitigationData;

public sealed class LitigationAiAnalysisOptions
{
    public const string SectionName = "LitigationAiAnalysis";
    public int MaxAttempts { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 60;
}
