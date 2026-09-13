namespace MCAROC_Analysis.Models.Viz;

/// <summary>A closed set of semantic chart colors (#120, C9). Deliberately a typed enum, not a raw CSS
/// string: a generic bar partial writes `fill="var(...)"` straight from this value, and a shared
/// rendering boundary must never accept caller-supplied text that lands directly in a CSS attribute —
/// only these 5 known values are ever resolvable.</summary>
public enum ChartAccent
{
    Brand,
    Danger,
    Warning,
    Success,
    Muted
}

/// <summary>The one place a <see cref="ChartAccent"/> becomes a CSS value — returns the full
/// <c>var(--x)</c> expression, never assembled by string concatenation at a call site.</summary>
public static class ChartAccentCss
{
    public static string CssVariable(ChartAccent accent) => accent switch
    {
        ChartAccent.Danger => "var(--danger)",
        ChartAccent.Warning => "var(--warning)",
        ChartAccent.Success => "var(--success)",
        ChartAccent.Muted => "var(--muted)",
        _ => "var(--brand)"
    };
}
