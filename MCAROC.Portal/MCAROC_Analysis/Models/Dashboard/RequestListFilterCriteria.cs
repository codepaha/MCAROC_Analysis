using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Models;

public enum RequestListSort
{
    CreatedDateDesc,
    CreatedDateAsc,
    PriorityDesc,
    CompanyNameAsc
}

/// <summary>Adds drill-down-only and list-only fields on top of the shared global filters. Inheriting
/// (rather than duplicating) DashboardFilterCriteria's fields is what makes drill-down links "just work" —
/// the same query-string keys bind into either page.</summary>
public class RequestListFilterCriteria : DashboardFilterCriteria
{
    public bool? AttentionRequired { get; set; }
    public FindingSection? Section { get; set; }

    /// <summary>Top Risk Indicator drill-down links always pass Code, never Title — the same Code can
    /// render different Titles depending on materiality (see FinancialRules.cs), so Title is not a stable
    /// filter key.</summary>
    public string? Code { get; set; }

    /// <summary>Free-text search against RequestNumber/CompanyName/Cin/Llpin/Pan. Trimmed before matching;
    /// SQL Server's default collation is case-insensitive, so no further normalization is needed.</summary>
    public string? SearchText { get; set; }

    public int Page { get; set; } = 1;
    public const int PageSize = 25;

    /// <summary>Always defaults to CreatedDateDesc regardless of which drill-down link was clicked — a
    /// consistent, predictable default beats a sort order that silently changes based on entry path.</summary>
    public RequestListSort Sort { get; set; } = RequestListSort.CreatedDateDesc;

    public new Dictionary<string, string> ToRouteValues(Dictionary<string, string>? overrides = null)
    {
        var values = base.ToRouteValues();
        if (AttentionRequired is { } attentionRequired) values["AttentionRequired"] = attentionRequired.ToString();
        if (Section is { } section) values["Section"] = section.ToString();
        if (Code is not null) values["Code"] = Code;
        if (!string.IsNullOrWhiteSpace(SearchText)) values["SearchText"] = SearchText;
        if (Page != 1) values["Page"] = Page.ToString();
        if (Sort != RequestListSort.CreatedDateDesc) values["Sort"] = Sort.ToString();

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
