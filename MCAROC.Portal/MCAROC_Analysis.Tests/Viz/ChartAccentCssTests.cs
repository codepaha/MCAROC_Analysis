using MCAROC_Analysis.Models.Viz;

namespace MCAROC_Analysis.Tests.Viz;

/// <summary>#120 (C9): <see cref="ChartAccentCss.CssVariable"/> is the one place a <see
/// cref="ChartAccent"/> becomes a CSS value — a total function over the closed enum, never a raw
/// string a partial would have to trust.</summary>
public class ChartAccentCssTests
{
    [Theory]
    [InlineData(ChartAccent.Brand, "var(--brand)")]
    [InlineData(ChartAccent.Danger, "var(--danger)")]
    [InlineData(ChartAccent.Warning, "var(--warning)")]
    [InlineData(ChartAccent.Success, "var(--success)")]
    [InlineData(ChartAccent.Muted, "var(--muted)")]
    public void Every_known_accent_resolves_to_its_exact_css_variable(ChartAccent accent, string expected)
    {
        Assert.Equal(expected, ChartAccentCss.CssVariable(accent));
    }

    [Fact]
    public void An_undefined_enum_value_falls_back_to_brand_rather_than_throwing()
    {
        var invalid = (ChartAccent)999;
        Assert.Equal("var(--brand)", ChartAccentCss.CssVariable(invalid));
    }
}
