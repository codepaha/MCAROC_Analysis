namespace MCAROC_Analysis.Models.Viz;

/// <summary>A period a chart point covers — carries everything either domain needs, forces neither to
/// fake the other's shape. <see cref="SortKey"/> is what ordering/uniqueness is enforced on;
/// <see cref="ActualDate"/> is populated only when a real calendar date exists (Dashboard's
/// weekly/monthly buckets), left <c>null</c> for a financial year (which names a 12-month span, not
/// one date).
///
/// Construction is factory-only: a private constructor and no positional/record syntax that would
/// expose a public one — <see cref="ForFinancialYear"/> and <see cref="ForDate"/> are the *only* ways
/// to get an instance, so a blank label or a <see cref="SortKey"/> that disagrees with
/// <see cref="ActualDate"/> is not constructible at all, not just discouraged.</summary>
public readonly struct ChartPeriod : IEquatable<ChartPeriod>
{
    public string Label { get; }
    public long SortKey { get; }
    public DateOnly? ActualDate { get; }

    private ChartPeriod(string label, long sortKey, DateOnly? actualDate)
    {
        Label = label;
        SortKey = sortKey;
        ActualDate = actualDate;
    }

    /// <summary>FY2025 -&gt; Label "FY2025", SortKey 2025, ActualDate null. Label is always non-blank
    /// by construction (interpolated from a real int), nothing to validate.</summary>
    public static ChartPeriod ForFinancialYear(int year) => new($"FY{year}", year, null);

    /// <summary>A real calendar bucket (Dashboard). SortKey is *always* <c>date.DayNumber</c> — never
    /// caller-supplied, so it can never disagree with <see cref="ActualDate"/>. The label is the
    /// caller's own display format and must be non-blank.</summary>
    public static ChartPeriod ForDate(DateOnly date, string label)
    {
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("A date-based chart period needs a non-blank display label.", nameof(label));
        return new ChartPeriod(label, date.DayNumber, date);
    }

    public bool Equals(ChartPeriod other) =>
        SortKey == other.SortKey && Label == other.Label && ActualDate == other.ActualDate;

    public override bool Equals(object? obj) => obj is ChartPeriod p && Equals(p);
    public override int GetHashCode() => HashCode.Combine(Label, SortKey, ActualDate);
    public static bool operator ==(ChartPeriod left, ChartPeriod right) => left.Equals(right);
    public static bool operator !=(ChartPeriod left, ChartPeriod right) => !left.Equals(right);
}
