using System.Text.Json;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.McaFilings.DocumentLinking;

public class LateDuplicateCanonicalizationResult
{
    public bool Succeeded { get; set; }
    public long CanonicalFilingDocumentId { get; set; }
    public long DuplicateFilingDocumentId { get; set; }
    public int UpdatedLinkCount { get; set; }
    public List<DocumentDataLinkSupersession> CreatedSupersessions { get; set; } = [];
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Service managing manifest-time duplicate bypassing and late duplicate canonicalization
/// with DocumentDataLinkSupersession audit preservation.
/// NOTE: In accordance with the Single Migration Lane rule, this contract PR establishes
/// the domain models, invariants, and evidence preservation semantics in memory.
/// The subsequent schema/persistence implementation will wrap these operations in a serializable
/// database transaction with row-level locks and concurrent collision tests.
/// </summary>
public class DocumentLinkCanonicalizationService
{
    /// <summary>
    /// Checks whether a document should be processed by the matching hierarchy at manifest time.
    /// Documents flagged as DuplicateOfDocumentId != null bypass the matching pipeline entirely.
    /// </summary>
    public bool ShouldProcessForLinking(McaFilingDocument document)
    {
        return !document.DuplicateOfDocumentId.HasValue;
    }

    /// <summary>
    /// Performs canonicalization when a document is identified as a late duplicate of a canonical document.
    /// If an active link on the duplicate collides with an active link on the canonical document,
    /// the duplicate's link is marked SupersededByDuplicate and a DocumentDataLinkSupersession record is created.
    /// </summary>
    public LateDuplicateCanonicalizationResult CanonicalizeLateDuplicate(
        long duplicateDocumentId,
        long canonicalDocumentId,
        long requestId,
        long ingestionRunId,
        string actor,
        string reason,
        List<DocumentDataLink> allLinks)
    {
        if (duplicateDocumentId == canonicalDocumentId)
        {
            return new LateDuplicateCanonicalizationResult
            {
                Succeeded = false,
                ErrorMessage = "Duplicate document ID cannot be identical to canonical document ID."
            };
        }

        var result = new LateDuplicateCanonicalizationResult
        {
            Succeeded = true,
            CanonicalFilingDocumentId = canonicalDocumentId,
            DuplicateFilingDocumentId = duplicateDocumentId
        };

        // Get active links for duplicate document
        var duplicateLinks = allLinks
            .Where(l => l.FilingDocumentId == duplicateDocumentId &&
                        l.RequestId == requestId &&
                        l.IngestionRunId == ingestionRunId &&
                        l.Status != DocumentDataLinkStatus.SupersededByDuplicate &&
                        l.Status != DocumentDataLinkStatus.Rejected)
            .ToList();

        // Get active links for canonical document
        var canonicalLinks = allLinks
            .Where(l => l.CanonicalFilingDocumentId == canonicalDocumentId &&
                        l.RequestId == requestId &&
                        l.IngestionRunId == ingestionRunId &&
                        l.Status != DocumentDataLinkStatus.SupersededByDuplicate &&
                        l.Status != DocumentDataLinkStatus.Rejected)
            .ToList();

        foreach (var dupLink in duplicateLinks)
        {
            // Check if there is an existing active canonical link with the same target and kind
            var matchingCanonicalLink = canonicalLinks.FirstOrDefault(c =>
                string.Equals(c.TargetEntityType, dupLink.TargetEntityType, StringComparison.OrdinalIgnoreCase) &&
                c.TargetEntityId == dupLink.TargetEntityId &&
                string.Equals(c.TargetField, dupLink.TargetField, StringComparison.OrdinalIgnoreCase) &&
                c.LinkKind == dupLink.LinkKind);

            if (matchingCanonicalLink != null)
            {
                // Capture immutable snapshot BEFORE mutating status or canonical document ID
                var snapshot = new
                {
                    dupLink.DocumentDataLinkId,
                    dupLink.RequestId,
                    dupLink.IngestionRunId,
                    dupLink.FilingDocumentId,
                    dupLink.CanonicalFilingDocumentId,
                    Status = dupLink.Status.ToString(),
                    dupLink.TargetEntityType,
                    dupLink.TargetEntityId,
                    dupLink.TargetField,
                    LinkKind = dupLink.LinkKind.ToString(),
                    MatchMethod = dupLink.MatchMethod.ToString(),
                    Confidence = dupLink.Confidence.ToString(),
                    dupLink.EvidenceJson,
                    dupLink.RuleVersion,
                    dupLink.InputHash,
                    dupLink.CreatedUtc,
                    dupLink.ReviewedUtc,
                    dupLink.ReviewedBy,
                    dupLink.ReviewReason
                };

                // Collision: duplicate link must be superseded by the surviving canonical link
                dupLink.Status = DocumentDataLinkStatus.SupersededByDuplicate;
                dupLink.CanonicalFilingDocumentId = canonicalDocumentId;

                var supersession = new DocumentDataLinkSupersession
                {
                    RequestId = requestId,
                    IngestionRunId = ingestionRunId,
                    SupersededLinkId = dupLink.DocumentDataLinkId,
                    SupersededLink = dupLink,
                    SurvivingLinkId = matchingCanonicalLink.DocumentDataLinkId,
                    SurvivingLink = matchingCanonicalLink,
                    SupersededUtc = DateTime.UtcNow,
                    Actor = actor,
                    Reason = reason,
                    EvidencePreservationJson = JsonSerializer.Serialize(snapshot)
                };

                dupLink.SupersededByLinks.Add(supersession);
                matchingCanonicalLink.SupersedesLinks.Add(supersession);

                result.CreatedSupersessions.Add(supersession);
                result.UpdatedLinkCount++;
            }
            else
            {
                // No collision: simply update canonical ID to canonical document
                dupLink.CanonicalFilingDocumentId = canonicalDocumentId;
                result.UpdatedLinkCount++;
            }
        }

        return result;
    }
}
