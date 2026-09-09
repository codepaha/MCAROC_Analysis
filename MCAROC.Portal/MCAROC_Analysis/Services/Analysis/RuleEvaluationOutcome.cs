namespace MCAROC_Analysis.Services.Analysis;

/// <summary>Internal to rule evaluation — never persisted as its own column. A rule reports NotEvaluated
/// (with a reason) whenever a required input is missing or ambiguous, so absence of data never becomes a
/// false adverse finding (a 0 or an implicit Watch/Review).</summary>
public enum RuleEvaluationStatus
{
    Triggered,
    NotTriggered,
    NotEvaluated
}

public record RuleEvaluationOutcome(RuleEvaluationStatus Status, FindingDraft? Finding, string? Code, string? NotEvaluatedReason)
{
    public static RuleEvaluationOutcome Triggered(FindingDraft finding) => new(RuleEvaluationStatus.Triggered, finding, finding.Code, null);
    public static RuleEvaluationOutcome NotTriggered() => new(RuleEvaluationStatus.NotTriggered, null, null, null);
    public static RuleEvaluationOutcome NotEvaluated(string code, string reason) => new(RuleEvaluationStatus.NotEvaluated, null, code, reason);
}
