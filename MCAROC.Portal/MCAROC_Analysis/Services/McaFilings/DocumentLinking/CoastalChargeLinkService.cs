using System.Text.Json;
using System.Text.Json.Serialization;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.McaFilings.DocumentLinking;

/// <summary>
/// Deterministic, stream-only linking pass connecting Coastal corpus PDF manifest entries
/// to ingested ROC charge events.
/// Pure in-memory/stream operations: no database writes, no filesystem writes.
/// </summary>
public static class CoastalChargeLinkService
{
    private static readonly JsonSerializerOptions EvidenceSerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static CoastalChargeLinkResult Execute(
        Stream outerZipStream,
        IReadOnlyList<RocCharge> charges,
        IReadOnlyList<RocChargeEvent> events,
        CoastalInventoryResult? prebuiltInventory = null)
    {
        ArgumentNullException.ThrowIfNull(outerZipStream);
        ArgumentNullException.ThrowIfNull(charges);
        ArgumentNullException.ThrowIfNull(events);

        var inventory = prebuiltInventory ?? CoastalCorpusInventoryService.BuildManifest(outerZipStream);
        var matcher = new ChargeCompositeKeyMatcher();

        // Ensure unpersisted in-memory entities have consistent positive identifiers
        long chargeIdSeq = 1;
        long eventIdSeq = 1;
        foreach (var c in charges)
        {
            if (c.ChargeId == 0)
            {
                c.ChargeId = chargeIdSeq++;
            }
            foreach (var ev in c.Events)
            {
                if (ev.RocChargeId == 0)
                {
                    ev.RocChargeId = c.ChargeId;
                    ev.RocCharge = c;
                }
                if (ev.ChargeEventId == 0)
                {
                    ev.ChargeEventId = eventIdSeq++;
                }
            }
        }
        foreach (var ev in events)
        {
            if (ev.ChargeEventId == 0)
            {
                ev.ChargeEventId = eventIdSeq++;
            }
        }

        var resultEntries = new List<CoastalChargeLinkResultEntry>(inventory.PdfManifestEntries.Count);

        foreach (var entry in inventory.PdfManifestEntries)
        {
            // Rule 1: Manifest duplicates bypass matching hierarchy and resolve to canonical coordinates
            if (!entry.IsCanonical)
            {
                var duplicateEvidence = new EvidencePayload
                {
                    CandidateChargeId = null,
                    InferredEventType = null,
                    FilingDate = null,
                    EventDate = null,
                    HasMalformedDate = false,
                    MalformedDateRawToken = null,
                    ClassificationCategory = null,
                    ClassificationMethod = null,
                    ClassificationRule = null,
                    ClassificationConfidence = null,
                    DateMatchMode = ChargeDateMatchMode.None.ToString(),
                    MatchFailureReason = null,
                    FailureReasonText = "Manifest duplicate bypassed to canonical entry."
                };

                resultEntries.Add(new CoastalChargeLinkResultEntry
                {
                    OuterEntryFullPath = entry.OuterEntryFullPath,
                    NestedEntryRelativePath = entry.NestedEntryRelativePath,
                    Sha256Hex = entry.Sha256Hex,
                    Outcome = PilotLinkOutcome.ManifestDuplicateBypassed,
                    Reason = PilotLinkReason.ManifestDuplicate,
                    MatchedRocChargeId = null,
                    MatchedRocChargeEventId = null,
                    DateMatchMode = ChargeDateMatchMode.None,
                    MatchFailureReason = null,
                    IsCanonical = false,
                    CanonicalOuterEntryFullPath = entry.CanonicalOuterEntryFullPath,
                    CanonicalNestedEntryRelativePath = entry.CanonicalNestedEntryRelativePath,
                    EvidenceJson = JsonSerializer.Serialize(duplicateEvidence, EvidenceSerializerOptions)
                });
                continue;
            }

            // Rule 2: Evaluate canonical entry via ChargeCandidateExtractor
            var candidate = ChargeCandidateExtractor.Extract(entry);

            // Out-of-scope non-charge documents
            if (candidate.Category != FilingCategory.Charge)
            {
                var outOfScopeEvidence = new EvidencePayload
                {
                    CandidateChargeId = candidate.ExtractedChargeId,
                    InferredEventType = candidate.InferredEventType?.ToString(),
                    FilingDate = candidate.FilingDate?.ToString("yyyy-MM-dd"),
                    EventDate = candidate.EventDate?.ToString("yyyy-MM-dd"),
                    HasMalformedDate = candidate.HasMalformedDate,
                    MalformedDateRawToken = candidate.MalformedDateRawToken,
                    ClassificationCategory = candidate.Category.ToString(),
                    ClassificationMethod = candidate.ClassificationMethod,
                    ClassificationRule = candidate.ClassificationRule,
                    ClassificationConfidence = candidate.Confidence.ToString(),
                    DateMatchMode = ChargeDateMatchMode.None.ToString(),
                    MatchFailureReason = null,
                    FailureReasonText = "Document classified as out-of-scope non-charge filing."
                };

                resultEntries.Add(new CoastalChargeLinkResultEntry
                {
                    OuterEntryFullPath = entry.OuterEntryFullPath,
                    NestedEntryRelativePath = entry.NestedEntryRelativePath,
                    Sha256Hex = entry.Sha256Hex,
                    Outcome = PilotLinkOutcome.UnlinkedOutOfScope,
                    Reason = PilotLinkReason.NonChargeDocument,
                    MatchedRocChargeId = null,
                    MatchedRocChargeEventId = null,
                    DateMatchMode = ChargeDateMatchMode.None,
                    MatchFailureReason = null,
                    IsCanonical = true,
                    CanonicalOuterEntryFullPath = entry.CanonicalOuterEntryFullPath,
                    CanonicalNestedEntryRelativePath = entry.CanonicalNestedEntryRelativePath,
                    EvidenceJson = JsonSerializer.Serialize(outOfScopeEvidence, EvidenceSerializerOptions)
                });
                continue;
            }

            // Charge document without extracted ChargeId in filename
            if (string.IsNullOrEmpty(candidate.ExtractedChargeId))
            {
                var noChargeIdEvidence = new EvidencePayload
                {
                    CandidateChargeId = null,
                    InferredEventType = candidate.InferredEventType?.ToString(),
                    FilingDate = candidate.FilingDate?.ToString("yyyy-MM-dd"),
                    EventDate = candidate.EventDate?.ToString("yyyy-MM-dd"),
                    HasMalformedDate = candidate.HasMalformedDate,
                    MalformedDateRawToken = candidate.MalformedDateRawToken,
                    ClassificationCategory = candidate.Category.ToString(),
                    ClassificationMethod = candidate.ClassificationMethod,
                    ClassificationRule = candidate.ClassificationRule,
                    ClassificationConfidence = candidate.Confidence.ToString(),
                    DateMatchMode = ChargeDateMatchMode.None.ToString(),
                    MatchFailureReason = ChargeMatchFailureReason.MissingChargeId.ToString(),
                    FailureReasonText = "Charge document lacks explicit ChargeId in filename."
                };

                resultEntries.Add(new CoastalChargeLinkResultEntry
                {
                    OuterEntryFullPath = entry.OuterEntryFullPath,
                    NestedEntryRelativePath = entry.NestedEntryRelativePath,
                    Sha256Hex = entry.Sha256Hex,
                    Outcome = PilotLinkOutcome.UnlinkedNoCandidate,
                    Reason = PilotLinkReason.MissingChargeId,
                    MatchedRocChargeId = null,
                    MatchedRocChargeEventId = null,
                    DateMatchMode = ChargeDateMatchMode.None,
                    MatchFailureReason = ChargeMatchFailureReason.MissingChargeId,
                    IsCanonical = true,
                    CanonicalOuterEntryFullPath = entry.CanonicalOuterEntryFullPath,
                    CanonicalNestedEntryRelativePath = entry.CanonicalNestedEntryRelativePath,
                    EvidenceJson = JsonSerializer.Serialize(noChargeIdEvidence, EvidenceSerializerOptions)
                });
                continue;
            }

            // Charge document with malformed date token
            if (candidate.HasMalformedDate)
            {
                var malformedDateEvidence = new EvidencePayload
                {
                    CandidateChargeId = candidate.ExtractedChargeId,
                    InferredEventType = candidate.InferredEventType?.ToString(),
                    FilingDate = null,
                    EventDate = null,
                    HasMalformedDate = true,
                    MalformedDateRawToken = candidate.MalformedDateRawToken,
                    ClassificationCategory = candidate.Category.ToString(),
                    ClassificationMethod = candidate.ClassificationMethod,
                    ClassificationRule = candidate.ClassificationRule,
                    ClassificationConfidence = candidate.Confidence.ToString(),
                    DateMatchMode = ChargeDateMatchMode.None.ToString(),
                    MatchFailureReason = null,
                    FailureReasonText = $"Candidate contains malformed date token '{candidate.MalformedDateRawToken}'."
                };

                resultEntries.Add(new CoastalChargeLinkResultEntry
                {
                    OuterEntryFullPath = entry.OuterEntryFullPath,
                    NestedEntryRelativePath = entry.NestedEntryRelativePath,
                    Sha256Hex = entry.Sha256Hex,
                    Outcome = PilotLinkOutcome.PendingReview,
                    Reason = PilotLinkReason.InvalidDateEvidence,
                    MatchedRocChargeId = null,
                    MatchedRocChargeEventId = null,
                    DateMatchMode = ChargeDateMatchMode.None,
                    MatchFailureReason = null,
                    IsCanonical = true,
                    CanonicalOuterEntryFullPath = entry.CanonicalOuterEntryFullPath,
                    CanonicalNestedEntryRelativePath = entry.CanonicalNestedEntryRelativePath,
                    EvidenceJson = JsonSerializer.Serialize(malformedDateEvidence, EvidenceSerializerOptions)
                });
                continue;
            }

            // Evaluate through composite key matcher
            var matchCandidate = new ChargeDocumentCandidate
            {
                ChargeId = candidate.ExtractedChargeId,
                EventType = candidate.InferredEventType,
                FilingDate = candidate.FilingDate,
                EventDate = candidate.EventDate,
                Amount = null,
                HolderName = null
            };

            var matchResult = matcher.Match(matchCandidate, charges, events);

            if (matchResult.IsMatched)
            {
                var acceptedReason = matchResult.DateMatchMode switch
                {
                    ChargeDateMatchMode.BothDatesMatched => PilotLinkReason.ExactMatchBothDates,
                    ChargeDateMatchMode.EventDateMatched => PilotLinkReason.ExactMatchEventDate,
                    ChargeDateMatchMode.FilingDateMatched => PilotLinkReason.ExactMatchFilingDate,
                    _ => PilotLinkReason.ExactMatchBothDates
                };

                var acceptedEvidence = new EvidencePayload
                {
                    CandidateChargeId = candidate.ExtractedChargeId,
                    InferredEventType = candidate.InferredEventType?.ToString(),
                    FilingDate = candidate.FilingDate?.ToString("yyyy-MM-dd"),
                    EventDate = candidate.EventDate?.ToString("yyyy-MM-dd"),
                    HasMalformedDate = false,
                    MalformedDateRawToken = null,
                    ClassificationCategory = candidate.Category.ToString(),
                    ClassificationMethod = candidate.ClassificationMethod,
                    ClassificationRule = candidate.ClassificationRule,
                    ClassificationConfidence = candidate.Confidence.ToString(),
                    DateMatchMode = matchResult.DateMatchMode.ToString(),
                    MatchFailureReason = null,
                    FailureReasonText = null
                };

                resultEntries.Add(new CoastalChargeLinkResultEntry
                {
                    OuterEntryFullPath = entry.OuterEntryFullPath,
                    NestedEntryRelativePath = entry.NestedEntryRelativePath,
                    Sha256Hex = entry.Sha256Hex,
                    Outcome = PilotLinkOutcome.AutoAccepted,
                    Reason = acceptedReason,
                    MatchedRocChargeId = matchResult.TargetChargeId,
                    MatchedRocChargeEventId = matchResult.TargetChargeEventId,
                    DateMatchMode = matchResult.DateMatchMode,
                    MatchFailureReason = null,
                    IsCanonical = true,
                    CanonicalOuterEntryFullPath = entry.CanonicalOuterEntryFullPath,
                    CanonicalNestedEntryRelativePath = entry.CanonicalNestedEntryRelativePath,
                    EvidenceJson = JsonSerializer.Serialize(acceptedEvidence, EvidenceSerializerOptions)
                });
            }
            else
            {
                PilotLinkOutcome outcome;
                PilotLinkReason reason;

                if (matchResult.FailureReasonCode == ChargeMatchFailureReason.ChargeNotFound)
                {
                    outcome = PilotLinkOutcome.UnlinkedNoCandidate;
                    reason = PilotLinkReason.ChargeNotFoundInWorkbook;
                }
                else if (matchResult.FailureReasonCode == ChargeMatchFailureReason.MissingChargeId)
                {
                    outcome = PilotLinkOutcome.UnlinkedNoCandidate;
                    reason = PilotLinkReason.MissingChargeId;
                }
                else
                {
                    outcome = PilotLinkOutcome.PendingReview;
                    reason = matchResult.FailureReasonCode switch
                    {
                        ChargeMatchFailureReason.DateMismatch => PilotLinkReason.DateMismatch,
                        ChargeMatchFailureReason.DateContradiction => PilotLinkReason.DateContradiction,
                        ChargeMatchFailureReason.EventTypeMismatch => PilotLinkReason.EventTypeMismatch,
                        ChargeMatchFailureReason.AmbiguousMultipleEvents => PilotLinkReason.AmbiguousMultipleEvents,
                        ChargeMatchFailureReason.ConflictingCorroboration => PilotLinkReason.ConflictingCorroboration,
                        _ => PilotLinkReason.DateMismatch
                    };
                }

                var rejectedEvidence = new EvidencePayload
                {
                    CandidateChargeId = candidate.ExtractedChargeId,
                    InferredEventType = candidate.InferredEventType?.ToString(),
                    FilingDate = candidate.FilingDate?.ToString("yyyy-MM-dd"),
                    EventDate = candidate.EventDate?.ToString("yyyy-MM-dd"),
                    HasMalformedDate = false,
                    MalformedDateRawToken = null,
                    ClassificationCategory = candidate.Category.ToString(),
                    ClassificationMethod = candidate.ClassificationMethod,
                    ClassificationRule = candidate.ClassificationRule,
                    ClassificationConfidence = candidate.Confidence.ToString(),
                    DateMatchMode = matchResult.DateMatchMode.ToString(),
                    MatchFailureReason = matchResult.FailureReasonCode.ToString(),
                    FailureReasonText = matchResult.FailureReason
                };

                resultEntries.Add(new CoastalChargeLinkResultEntry
                {
                    OuterEntryFullPath = entry.OuterEntryFullPath,
                    NestedEntryRelativePath = entry.NestedEntryRelativePath,
                    Sha256Hex = entry.Sha256Hex,
                    Outcome = outcome,
                    Reason = reason,
                    MatchedRocChargeId = matchResult.TargetChargeId,
                    MatchedRocChargeEventId = matchResult.TargetChargeEventId,
                    DateMatchMode = matchResult.DateMatchMode,
                    MatchFailureReason = matchResult.FailureReasonCode,
                    IsCanonical = true,
                    CanonicalOuterEntryFullPath = entry.CanonicalOuterEntryFullPath,
                    CanonicalNestedEntryRelativePath = entry.CanonicalNestedEntryRelativePath,
                    EvidenceJson = JsonSerializer.Serialize(rejectedEvidence, EvidenceSerializerOptions)
                });
            }
        }

        return new CoastalChargeLinkResult(resultEntries.AsReadOnly());
    }

    private sealed record EvidencePayload
    {
        public string? CandidateChargeId { get; init; }
        public string? InferredEventType { get; init; }
        public string? FilingDate { get; init; }
        public string? EventDate { get; init; }
        public bool HasMalformedDate { get; init; }
        public string? MalformedDateRawToken { get; init; }
        public string? ClassificationCategory { get; init; }
        public string? ClassificationMethod { get; init; }
        public string? ClassificationRule { get; init; }
        public string? ClassificationConfidence { get; init; }
        public string DateMatchMode { get; init; } = "None";
        public string? MatchFailureReason { get; init; }
        public string? FailureReasonText { get; init; }
    }
}
