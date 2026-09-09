using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Models;

/// <summary>The global filter set shared by the Dashboard and Search History pages. Bound via [FromQuery]
/// on both, so the same query-string keys work on either page — the mechanism that makes drill-down links
/// "just carry the active filters forward" without any custom glue code.</summary>
public class DashboardFilterCriteria
{
    public DateOnly? DateFrom { get; set; }
    public DateOnly? DateTo { get; set; }
    public long? ClientId { get; set; }

    /// <summary>Bound to RequestStatus. UI label is "Analysis Status", not "Status" — the MCA filing PDF
    /// pipeline has its own independent status (McaFilingBatch.Status) not exposed as a top-level filter in
    /// v1, so an unqualified "Status" label would be ambiguous about which pipeline it filters.</summary>
    public RequestStatus? Status { get; set; }
    public ReviewPriority? Priority { get; set; }
    public EntityType? EntityType { get; set; }

    private const int DefaultWindowDays = 90;

    /// <summary>Resolves DateFrom/DateTo to a concrete window: defaults to the trailing 90 days when either
    /// is unset, so every KPI/trend calculation always has a well-defined window and a well-defined
    /// immediately-preceding comparison window (see PriorPeriod).</summary>
    public (DateOnly From, DateOnly To) ResolveWindow(DateOnly today)
    {
        var to = DateTo ?? today;
        var from = DateFrom ?? to.AddDays(-(DefaultWindowDays - 1));
        return (from, to);
    }

    /// <summary>The equal-length window immediately preceding (From, To) — e.g. a 30-day window compares
    /// against the 30 days immediately before it, not a generic "30 calendar days back" or "previous
    /// calendar month" (those are different concepts; this dashboard always uses equal-length adjacent
    /// windows for the trend-percent comparison).</summary>
    public static (DateOnly From, DateOnly To) PriorPeriod(DateOnly from, DateOnly to)
    {
        var lengthDays = to.DayNumber - from.DayNumber + 1;
        var priorTo = from.AddDays(-1);
        var priorFrom = priorTo.AddDays(-(lengthDays - 1));
        return (priorFrom, priorTo);
    }

    /// <summary>Builds a route-value dictionary for drill-down links, carrying every currently-active filter
    /// forward and letting the caller add or override one specific key (e.g. {"priority","High"}) — the
    /// concrete mechanism behind "every dashboard number is clickable and preserves the active filters."</summary>
    public Dictionary<string, string> ToRouteValues(Dictionary<string, string>? overrides = null)
    {
        var values = new Dictionary<string, string>();
        if (DateFrom is { } dateFrom) values["DateFrom"] = dateFrom.ToString("yyyy-MM-dd");
        if (DateTo is { } dateTo) values["DateTo"] = dateTo.ToString("yyyy-MM-dd");
        if (ClientId is { } clientId) values["ClientId"] = clientId.ToString();
        if (Status is { } status) values["Status"] = status.ToString();
        if (Priority is { } priority) values["Priority"] = priority.ToString();
        if (EntityType is { } entityType) values["EntityType"] = entityType.ToString();

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                if (string.IsNullOrEmpty(value)) values.Remove(key);
                else values[key] = value;
            }
        }

        return values;
    }
}
