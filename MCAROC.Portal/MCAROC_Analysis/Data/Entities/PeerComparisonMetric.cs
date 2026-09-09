namespace MCAROC_Analysis.Data.Entities;

/// <summary>Sourced from the "Peer Comparison" sheet — one row per (metric, financial year) with the
/// company's actual value and the peer-group median. Position is a purely mathematical comparison;
/// whether "Above"/"Below" is favourable is decided per-metric by PeerComparisonDisplayRules.</summary>
public class PeerComparisonMetric : ExtractedEntityBase
{
    public long PeerComparisonMetricId { get; set; }

    public string MetricName { get; set; } = string.Empty;
    public int FinancialYear { get; set; }

    public decimal? CompanyValue { get; set; }
    public decimal? PeerMedianValue { get; set; }
    public int? PeerCount { get; set; }

    public PeerPosition Position { get; set; } = PeerPosition.NotComparable;

    public string? Industry { get; set; }
    public string? Segment { get; set; }
}
