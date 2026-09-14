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
        var basis = candidate.Basis;
        var targets = targetCatalog.GetTargets(year, basis);

        // If not found in primary basis, check if Standalone targets exist for this year as fallback
        if (targets.Count == 0 && basis == FinancialBasis.Consolidated)
        {
            targets = targetCatalog.GetTargets(year, FinancialBasis.Standalone);
            if (targets.Count > 0)
            {
                basis = FinancialBasis.Standalone;
            }
        }

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

        // Check value corroboration
        if (candidate.StatementPageNumber.HasValue && !string.IsNullOrEmpty(candidate.CorroboratedField))
        {
            var target = targetCatalog.GetTarget(year, basis, candidate.CorroboratedField);
            if (target is not null)
            {
                long targetEntityId = target.TargetKind switch
                {
                    FinancialTargetKind.FinancialYearData => finMap.TryGetValue((FinancialYearData)target.TargetEntity, out var id) ? id : 0,
                    FinancialTargetKind.FinancialFact => factMap.TryGetValue((FinancialFact)target.TargetEntity, out var id) ? id : 0,
                    _ => 0
                };

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
        }

        // If year matches workbook but statement line item could not corroborate, route to PendingReview
        return new FinancialMatchResult
        {
            Outcome = PilotFinancialLinkOutcome.PendingReview,
            Reason = PilotFinancialLinkReason.ExactMatchReportingPeriod,
            MatchedFinancialYear = year,
            MatchedBasis = basis
        };
    }
}
