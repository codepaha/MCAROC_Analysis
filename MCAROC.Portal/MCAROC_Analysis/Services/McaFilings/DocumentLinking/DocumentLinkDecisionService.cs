using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.McaFilings.DocumentLinking;

public class CandidateEvaluationInput
{
    public long CanonicalFilingDocumentId { get; set; }
    public long IngestionRunId { get; set; }
    public string TargetEntityType { get; set; } = string.Empty;
    public long TargetEntityId { get; set; }
    public string? TargetField { get; set; }
    public DocumentLinkKind LinkKind { get; set; } = DocumentLinkKind.Supports;
    public string RuleVersion { get; set; } = "1.0.0";
    public string InputHash { get; set; } = string.Empty;
}

public class CandidateDecision
{
    public bool ShouldGenerate { get; set; }
    public DocumentDataLinkStatus RecommendedStatus { get; set; }
    public string DecisionReason { get; set; } = string.Empty;
}

/// <summary>
/// Service responsible for deciding whether to generate or suppress candidates based on
/// prior reviewer rejections and rule versions.
/// </summary>
public class DocumentLinkDecisionService
{
    /// <summary>
    /// Evaluates whether a proposed candidate link should be generated or suppressed
    /// by inspecting existing reviewer rejections.
    /// </summary>
    public CandidateDecision EvaluateCandidate(
        CandidateEvaluationInput candidate,
        IEnumerable<DocumentDataLink> existingLinks)
    {
        // Query prior rejected decisions matching canonical document, run, target entity, field, and link kind
        var priorRejections = existingLinks
            .Where(l => l.Status == DocumentDataLinkStatus.Rejected &&
                        l.CanonicalFilingDocumentId == candidate.CanonicalFilingDocumentId &&
                        l.IngestionRunId == candidate.IngestionRunId &&
                        string.Equals(l.TargetEntityType, candidate.TargetEntityType, StringComparison.OrdinalIgnoreCase) &&
                        l.TargetEntityId == candidate.TargetEntityId &&
                        string.Equals(l.TargetField, candidate.TargetField, StringComparison.OrdinalIgnoreCase) &&
                        l.LinkKind == candidate.LinkKind)
            .ToList();

        if (priorRejections.Count == 0)
        {
            return new CandidateDecision
            {
                ShouldGenerate = true,
                RecommendedStatus = DocumentDataLinkStatus.PendingReview,
                DecisionReason = "No prior reviewer rejections found."
            };
        }

        // Check if there is an exact match on RuleVersion and InputHash
        var exactSameRejection = priorRejections
            .FirstOrDefault(r => string.Equals(r.RuleVersion, candidate.RuleVersion, StringComparison.OrdinalIgnoreCase) &&
                                 string.Equals(r.InputHash, candidate.InputHash, StringComparison.OrdinalIgnoreCase));

        if (exactSameRejection != null)
        {
            return new CandidateDecision
            {
                ShouldGenerate = false,
                RecommendedStatus = DocumentDataLinkStatus.Rejected,
                DecisionReason = $"Candidate suppressed: previously rejected under rule version '{exactSameRejection.RuleVersion}' with reason '{exactSameRejection.ReviewReason}' and identical input hash."
            };
        }

        // If rule version is different (e.g. newer) or input hash has changed, allow recreation for independent review
        var versionOrHashDifference = priorRejections.First();
        return new CandidateDecision
        {
            ShouldGenerate = true,
            RecommendedStatus = DocumentDataLinkStatus.PendingReview,
            DecisionReason = $"Candidate permitted: rule version or input hash updated (Prior: v'{versionOrHashDifference.RuleVersion}', Proposed: v'{candidate.RuleVersion}'). Independent review required."
        };
    }
}
