using System.Text.Json;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.McaFilings.DocumentLinking;

public class ChargeDocumentCandidate
{
    public long FilingDocumentId { get; set; }
    public long? DuplicateOfDocumentId { get; set; }
    public string ChargeId { get; set; } = string.Empty;
    public ChargeEventType? EventType { get; set; }
    public DateOnly? EventDate { get; set; }
    public DateOnly? FilingDate { get; set; }
    public decimal? Amount { get; set; }
    public string? HolderName { get; set; }
    public string? DocumentSrn { get; set; } // May be present on filing, but strictly ignored for charge matching
    public int? PageNumber { get; set; }
    public string? TextQuote { get; set; }
}

public class ChargeMatchResult
{
    public bool IsMatched { get; set; }
    public long? TargetChargeId { get; set; }
    public long? TargetChargeEventId { get; set; }
    public ChargeDateMatchMode DateMatchMode { get; set; } = ChargeDateMatchMode.None;
    public DocumentLinkConfidence Confidence { get; set; } = DocumentLinkConfidence.Low;
    public string EvidenceJson { get; set; } = "{}";
    public string? FailureReason { get; set; }
}

public class ChargeCompositeKeyMatcher
{
    /// <summary>
    /// Matches a candidate extracted from a charge PDF document against ingested RocCharge and RocChargeEvent rows.
    /// Distinguishes between EventDate (instrument deed date) and FilingDate (ROC registration date).
    /// Strictly excludes SRN from matching.
    /// </summary>
    public ChargeMatchResult Match(
        ChargeDocumentCandidate candidate,
        IEnumerable<RocCharge> existingCharges,
        IEnumerable<RocChargeEvent> existingEvents)
    {
        if (string.IsNullOrWhiteSpace(candidate.ChargeId))
        {
            return new ChargeMatchResult
            {
                IsMatched = false,
                FailureReason = "Charge ID is missing or empty."
            };
        }

        var normalizedCandidateChargeId = candidate.ChargeId.Trim();

        // 1. Resolve candidate charge by RocChargeNumber
        var matchingCharges = existingCharges
            .Where(c => string.Equals(c.RocChargeNumber?.Trim(), normalizedCandidateChargeId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matchingCharges.Count == 0)
        {
            return new ChargeMatchResult
            {
                IsMatched = false,
                FailureReason = $"No RocCharge found with charge number '{normalizedCandidateChargeId}'."
            };
        }

        var chargeIds = matchingCharges.Select(c => c.ChargeId).ToHashSet();
        var relevantEvents = existingEvents.Where(e => chargeIds.Contains(e.RocChargeId)).ToList();

        // 2. Filter by EventType if specified
        if (candidate.EventType.HasValue)
        {
            relevantEvents = relevantEvents.Where(e => e.EventType == candidate.EventType.Value).ToList();
        }

        // 3. Match against EventDate or FilingDate
        var eventMatches = new List<(RocChargeEvent Event, ChargeDateMatchMode Mode)>();

        foreach (var ev in relevantEvents)
        {
            bool matchesEventDate = candidate.EventDate.HasValue && ev.EventDate.HasValue && candidate.EventDate.Value == ev.EventDate.Value;
            bool matchesFilingDate = candidate.FilingDate.HasValue && ev.FilingDate.HasValue && candidate.FilingDate.Value == ev.FilingDate.Value;

            if (matchesEventDate && matchesFilingDate)
            {
                eventMatches.Add((ev, ChargeDateMatchMode.BothDatesMatched));
            }
            else if (matchesEventDate)
            {
                eventMatches.Add((ev, ChargeDateMatchMode.EventDateMatched));
            }
            else if (matchesFilingDate)
            {
                eventMatches.Add((ev, ChargeDateMatchMode.FilingDateMatched));
            }
        }

        // If no date-specific event matched, but we have exactly 1 event and no dates were provided on candidate, check amount/holder corroboration
        if (eventMatches.Count == 0 && !candidate.EventDate.HasValue && !candidate.FilingDate.HasValue)
        {
            if (relevantEvents.Count == 1)
            {
                eventMatches.Add((relevantEvents[0], ChargeDateMatchMode.None));
            }
        }

        if (eventMatches.Count == 0)
        {
            return new ChargeMatchResult
            {
                IsMatched = false,
                FailureReason = "No matching RocChargeEvent found for the specified event type and date(s)."
            };
        }

        if (eventMatches.Count > 1)
        {
            // Ambiguous multiple matches
            return new ChargeMatchResult
            {
                IsMatched = false,
                FailureReason = $"Ambiguous: found {eventMatches.Count} matching charge events."
            };
        }

        var (matchedEvent, dateMode) = eventMatches[0];
        var parentCharge = matchingCharges.FirstOrDefault(c => c.ChargeId == matchedEvent.RocChargeId);

        // Corroborate amount and holder
        bool amountCorroborated = false;
        if (candidate.Amount.HasValue)
        {
            if (matchedEvent.ChargeAmount.HasValue && matchedEvent.ChargeAmount.Value == candidate.Amount.Value)
            {
                amountCorroborated = true;
            }
            else if (parentCharge?.CurrentAmount.HasValue == true && parentCharge.CurrentAmount.Value == candidate.Amount.Value)
            {
                amountCorroborated = true;
            }
        }

        bool holderCorroborated = false;
        if (!string.IsNullOrWhiteSpace(candidate.HolderName))
        {
            var candHolder = candidate.HolderName.Trim();
            if (!string.IsNullOrWhiteSpace(matchedEvent.HolderNameNormalized) &&
                matchedEvent.HolderNameNormalized.Contains(candHolder, StringComparison.OrdinalIgnoreCase))
            {
                holderCorroborated = true;
            }
            else if (!string.IsNullOrWhiteSpace(parentCharge?.LatestChargeHolderNormalized) &&
                     parentCharge.LatestChargeHolderNormalized.Contains(candHolder, StringComparison.OrdinalIgnoreCase))
            {
                holderCorroborated = true;
            }
        }

        // Confidence determination
        DocumentLinkConfidence confidence = DocumentLinkConfidence.Medium;
        if (dateMode != ChargeDateMatchMode.None && (amountCorroborated || holderCorroborated))
        {
            confidence = DocumentLinkConfidence.High;
        }
        else if (dateMode == ChargeDateMatchMode.BothDatesMatched)
        {
            confidence = DocumentLinkConfidence.High;
        }
        else if (dateMode == ChargeDateMatchMode.None && !amountCorroborated && !holderCorroborated)
        {
            confidence = DocumentLinkConfidence.Low;
        }

        var evidence = new
        {
            RocChargeNumber = normalizedCandidateChargeId,
            EventType = candidate.EventType?.ToString(),
            MatchedChargeEventId = matchedEvent.ChargeEventId,
            DateMatchMode = dateMode.ToString(),
            CandidateEventDate = candidate.EventDate?.ToString("yyyy-MM-dd"),
            CandidateFilingDate = candidate.FilingDate?.ToString("yyyy-MM-dd"),
            EventDateInDb = matchedEvent.EventDate?.ToString("yyyy-MM-dd"),
            FilingDateInDb = matchedEvent.FilingDate?.ToString("yyyy-MM-dd"),
            AmountCorroborated = amountCorroborated,
            HolderCorroborated = holderCorroborated,
            PageNumber = candidate.PageNumber,
            TextQuote = candidate.TextQuote
        };

        return new ChargeMatchResult
        {
            IsMatched = true,
            TargetChargeId = parentCharge?.ChargeId,
            TargetChargeEventId = matchedEvent.ChargeEventId,
            DateMatchMode = dateMode,
            Confidence = confidence,
            EvidenceJson = JsonSerializer.Serialize(evidence)
        };
    }
}
