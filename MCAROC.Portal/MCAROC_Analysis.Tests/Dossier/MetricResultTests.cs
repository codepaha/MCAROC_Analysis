using MCAROC_Analysis.Models.Dossier;

namespace MCAROC_Analysis.Tests.Dossier;

/// <summary>The <see cref="MetricResult"/> guardrail (Wave 4 D0): a metric always renders a value or
/// an explicit insufficiency reason, never a bare 0 or a blank; the display format is unit-aware.</summary>
public class MetricResultTests
{
    [Fact]
    public void Ok_carries_value_period_and_inputs_and_HasValue_is_true()
    {
        var m = MetricResult.Ok("Net debt / EBITDA", 1.8m, MetricUnit.Times, "FY2017",
            "FinancialYearData.TotalDebt", "FinancialYearData.CashAndBank", "FinancialYearData.Ebitda");

        Assert.True(m.HasValue);
        Assert.Equal("1.80x", m.DisplayValue());
        Assert.Equal("FY2017", m.Period);
        Assert.Equal(3, m.Inputs.Count);
    }

    [Fact]
    public void Insufficient_has_no_value_and_DisplayValue_is_the_reason()
    {
        var m = MetricResult.Insufficient("Revenue CAGR", MetricUnit.Percent,
            "Only 1 financial year on record", "FinancialYearData.Revenue");

        Assert.False(m.HasValue);
        Assert.Null(m.Value);
        Assert.Equal("Only 1 financial year on record", m.DisplayValue());
        Assert.Equal("—", m.Period);
    }

    [Theory]
    [InlineData(MetricUnit.Percent, 12.4, "12.40%")]
    [InlineData(MetricUnit.Percent, 100, "100%")]
    [InlineData(MetricUnit.Times, 2, "2x")]
    [InlineData(MetricUnit.Crore, 1240, "₹1,240 Cr")]
    [InlineData(MetricUnit.Days, 412, "412 days")]
    [InlineData(MetricUnit.Years, 6.2, "6.20 yrs")]
    [InlineData(MetricUnit.Count, 37, "37")]
    [InlineData(MetricUnit.Ratio, 1.85, "1.85")]
    public void DisplayValue_is_unit_aware(MetricUnit unit, double value, string expected)
    {
        Assert.Equal(expected, MetricResult.Ok("m", (decimal)value, unit, "FY2017", "x").DisplayValue());
    }

    [Fact]
    public void MetricGroup_HasAny_reflects_its_metric_count()
    {
        Assert.False(new MetricGroup("Charges", []).HasAny);
        Assert.True(new MetricGroup("Charges",
            [MetricResult.Ok("Open charge amount", 1240m, MetricUnit.Crore, "as at 10 Sep 2026", "RocCharge.CurrentAmount")]).HasAny);
    }
}
