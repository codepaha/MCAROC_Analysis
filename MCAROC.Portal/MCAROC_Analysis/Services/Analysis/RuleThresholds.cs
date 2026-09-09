namespace MCAROC_Analysis.Services.Analysis;

/// <summary>Named, documented rule constants (mirrors ArchiveSafetyLimits' record-with-defaults pattern).
/// Percent fields are whole numbers (15m = 15%), not fractions. Boundary behavior is inclusive on the
/// stated comparison operator only — e.g. "> CriticalRevenueDeclinePercent" means exactly the threshold
/// value does NOT trigger, one basis point past it does; boundary tests cover this per constant.</summary>
public record RuleThresholds(
    decimal MaterialRevenueDeclinePercent = 15m,
    decimal CriticalRevenueDeclinePercent = 25m,
    decimal HighLeverageRatio = 3m,
    decimal MinCurrentRatio = 1.0m,
    decimal WatchCurrentRatioCeiling = 1.2m,
    decimal StrongCurrentRatioFloor = 1.5m,
    decimal MaterialDebtorDaysIncreasePercent = 20m,
    decimal MaterialPayableDaysIncreasePercent = 20m,
    decimal MaterialPromoterHoldingDeclinePoints = 10m,
    decimal PromoterHoldingToleranceOverHundredPercent = 2m,
    int StaleFinancialDataMonths = 18,
    int DirectorFanOutThreshold = 5,
    int DirectorCessationLookbackMonths = 24,
    decimal MaterialMsmeToPayablesRatioPercent = 10m,
    decimal HighRegisteredChargeExposureRatio = 1.0m,
    decimal MaterialChargeEnhancementPercent = 25m,
    decimal HighLenderConcentrationPercent = 50m,
    decimal WorkforceDeclineWatchPercent = 5m,
    decimal WorkforceDeclineReviewPercent = 10m,
    decimal WorkforceDeclineHighPercent = 20m,
    int IsolatedEpfoDelayMaxDays = 7,
    int RepeatedEpfoDelayLookbackMonths = 12,
    int RepeatedEpfoDelayMinOccurrences = 2)
{
    public static readonly RuleThresholds Default = new();
}
