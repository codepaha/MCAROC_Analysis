using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.McaFilings.DocumentLinking;

public class FinancialMatchResult
{
    public PilotFinancialLinkOutcome Outcome { get; init; }
    public PilotFinancialLinkReason Reason { get; init; }

    public int? MatchedFinancialYear { get; init; }
    public FinancialBasis? MatchedBasis { get; init; }

    public FinancialTargetKind? TargetKind { get; init; }
    public long? TargetEntityId { get; init; }
    public TargetSourceCoordinates? TargetCoordinates { get; init; }

    public string? TargetLineItem { get; init; }
    public decimal? MatchedValue { get; init; }
    public int? EvidencePageNumber { get; init; }
    public string? EvidenceTextQuote { get; init; }
}

public static class FinancialPeriodMatcher
{
    public static FinancialMatchResult Match(
        ExtractedFinancialCandidate candidate,
        FinancialLinkTargetCatalog targetCatalog,
        IDictionary<FinancialYearData, long> finMap,
        IDictionary<FinancialFact, long> factMap)
    {
        if (!candidate.IsFinancial)
        {
            return new FinancialMatchResult
            {
                Outcome = PilotFinancialLinkOutcome.UnlinkedOutOfScope,
                Reason = PilotFinancialLinkReason.NonFinancialDocument
            };
        }

        if (!candidate.FinancialYear.HasValue)
        {
            return new FinancialMatchResult
            {
                Outcome = PilotFinancialLinkOutcome.UnlinkedNoCandidate,
                Reason = PilotFinancialLinkReason.MissingReportingPeriod
            };
        }

        var year = candidate.FinancialYear.Value;

        if (candidate.HasConflictingBasis)
        {
            return new FinancialMatchResult
            {
                Outcome = PilotFinancialLinkOutcome.PendingReview,
                Reason = PilotFinancialLinkReason.ConflictingBasisEvidence,
                MatchedFinancialYear = year,
                MatchedBasis = candidate.Basis
            };
        }

        if (!candidate.Basis.HasValue)
        {
            return new FinancialMatchResult
            {
                Outcome = PilotFinancialLinkOutcome.PendingReview,
                Reason = PilotFinancialLinkReason.MissingBasisEvidence,
                MatchedFinancialYear = year
            };
        }

        var basis = candidate.Basis.Value;
        var targets = targetCatalog.GetTargets(year, basis);

        // Strict basis: zero cross-basis fallback between Consolidated and Standalone
        if (targets.Count == 0)
        {
            return new FinancialMatchResult
            {
                Outcome = PilotFinancialLinkOutcome.UnlinkedNoCandidate,
                Reason = PilotFinancialLinkReason.PeriodNotFoundInWorkbook,
                MatchedFinancialYear = year,
                MatchedBasis = basis
            };
        }

        if (candidate.IsXfaPlaceholder)
        {
            return new FinancialMatchResult
            {
                Outcome = PilotFinancialLinkOutcome.PendingReview,
                Reason = PilotFinancialLinkReason.ExactMatchReportingPeriod,
                MatchedFinancialYear = year,
                MatchedBasis = basis
            };
        }

        // Value corroboration: require exact declared comparison of normalized amount
        if (candidate.StatementPageNumber.HasValue && !string.IsNullOrEmpty(candidate.CorroboratedField))
        {
            if (!candidate.CorroboratedAmount.HasValue)
            {
                return new FinancialMatchResult
                {
                    Outcome = PilotFinancialLinkOutcome.PendingReview,
                    Reason = PilotFinancialLinkReason.ExactMatchReportingPeriod,
                    MatchedFinancialYear = year,
                    MatchedBasis = basis
                };
            }

            var target = targetCatalog.GetTarget(year, basis, candidate.CorroboratedField);
            if (target is not null && target.NumericValue.HasValue)
            {
                long targetEntityId = target.TargetKind switch
                {
                    FinancialTargetKind.FinancialYearData => finMap.TryGetValue((FinancialYearData)target.TargetEntity, out var id) ? id : 0,
                    FinancialTargetKind.FinancialFact => factMap.TryGetValue((FinancialFact)target.TargetEntity, out var id) ? id : 0,
                    _ => 0
                };

                var targetVal = Math.Round(target.NumericValue.Value, 2);
                var pdfVal = Math.Round(candidate.CorroboratedAmount.Value, 2);

                if (targetVal == pdfVal)
                {
                    // Cash-flow inferred year guard: never auto-accept inferred periods
                    if (target.YearInferred)
                    {
                        return new FinancialMatchResult
                        {
                            Outcome = PilotFinancialLinkOutcome.PendingReview,
                            Reason = PilotFinancialLinkReason.InferredCashFlowPeriod,
                            MatchedFinancialYear = year,
                            MatchedBasis = basis,
                            TargetKind = target.TargetKind,
                            TargetEntityId = targetEntityId,
                            TargetCoordinates = target.Coordinates,
                            TargetLineItem = target.TargetField,
                            MatchedValue = target.NumericValue,
                            EvidencePageNumber = candidate.StatementPageNumber,
                            EvidenceTextQuote = candidate.StatementTextQuote
                        };
                    }

                    return new FinancialMatchResult
                    {
                        Outcome = PilotFinancialLinkOutcome.AutoAccepted,
                        Reason = PilotFinancialLinkReason.ExactMatchReportingPeriodAndStatementValue,
                        MatchedFinancialYear = year,
                        MatchedBasis = basis,
                        TargetKind = target.TargetKind,
                        TargetEntityId = targetEntityId,
                        TargetCoordinates = target.Coordinates,
                        TargetLineItem = target.TargetField,
                        MatchedValue = target.NumericValue,
                        EvidencePageNumber = candidate.StatementPageNumber,
                        EvidenceTextQuote = candidate.StatementTextQuote
                    };
                }
                else
                {
                    // Value mismatch: route to review
                    return new FinancialMatchResult
                    {
                        Outcome = PilotFinancialLinkOutcome.PendingReview,
                        Reason = PilotFinancialLinkReason.StatementValueMismatch,
                        MatchedFinancialYear = year,
                        MatchedBasis = basis,
                        TargetKind = target.TargetKind,
                        TargetEntityId = targetEntityId,
                        TargetCoordinates = target.Coordinates,
                        TargetLineItem = target.TargetField,
                        MatchedValue = target.NumericValue,
                        EvidencePageNumber = candidate.StatementPageNumber,
                        EvidenceTextQuote = candidate.StatementTextQuote
                    };
                }
            }
        }

        // If year matches workbook but statement value could not be corroborated, route to PendingReview
        return new FinancialMatchResult
        {
            Outcome = PilotFinancialLinkOutcome.PendingReview,
            Reason = PilotFinancialLinkReason.ExactMatchReportingPeriod,
            MatchedFinancialYear = year,
            MatchedBasis = basis
        };
    }
}
