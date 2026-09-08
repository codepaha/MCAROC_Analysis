using MCAROC_Analysis.Services.Excel;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class DateNormalizerTests
{
    [Fact]
    public void ParsesSourceDateFormat()
    {
        Assert.True(DateNormalizer.TryParse("10 Mar, 2026", out var result));
        Assert.Equal(new DateOnly(2026, 3, 10), result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("-")]
    [InlineData("")]
    public void TreatsBlankAndDashAsNull(string? input)
    {
        Assert.True(DateNormalizer.TryParse(input, out var result));
        Assert.Null(result);
    }

    [Fact]
    public void PassesThroughNativeDateTimeCells()
    {
        Assert.True(DateNormalizer.TryParse(new DateTime(2025, 12, 31), out var result));
        Assert.Equal(new DateOnly(2025, 12, 31), result);
    }

    [Fact]
    public void ReturnsFalseForUnparsableText()
    {
        Assert.False(DateNormalizer.TryParse("not a date", out _));
    }
}

public class AmountNormalizerTests
{
    [Fact]
    public void ParsesNativeNumericCell()
    {
        Assert.True(AmountNormalizer.TryParse(160.0, out var value, out var raw));
        Assert.Equal(160.0m, value);
        Assert.Equal("160", raw);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("-")]
    [InlineData("")]
    public void TreatsBlankAndDashAsNull(string? input)
    {
        Assert.True(AmountNormalizer.TryParse(input, out var value, out _));
        Assert.Null(value);
    }

    [Fact]
    public void StripsCommasFromTextAmounts()
    {
        Assert.True(AmountNormalizer.TryParse("1,234.5", out var value, out _));
        Assert.Equal(1234.5m, value);
    }
}

public class DinNormalizerTests
{
    [Fact]
    public void ZeroPadsFloatDin()
    {
        Assert.True(DinNormalizer.TryParse(415231.0, out var din));
        Assert.Equal("00415231", din);
    }

    [Fact]
    public void HandlesEightDigitDinWithoutPadding()
    {
        Assert.True(DinNormalizer.TryParse(12345678.0, out var din));
        Assert.Equal("12345678", din);
    }

    [Fact]
    public void RejectsValuesLongerThanEightDigits()
    {
        Assert.False(DinNormalizer.TryParse(123456789.0, out _));
    }
}

public class NameNormalizerTests
{
    [Fact]
    public void SplitsTrailingDinSuffix()
    {
        var (name, din) = NameNormalizer.SplitDin("AMITABH SARAN (DIN : 00415231)");
        Assert.Equal("AMITABH SARAN", name);
        Assert.Equal("00415231", din);
    }

    [Fact]
    public void ReturnsNullDinWhenSuffixAbsent()
    {
        var (name, din) = NameNormalizer.SplitDin("JAKSON GREEN VEHICLES PRIVATE LIMITED");
        Assert.Equal("JAKSON GREEN VEHICLES PRIVATE LIMITED", name);
        Assert.Null(din);
    }

    [Fact]
    public void CollapsesWhitespaceAndUppercases()
    {
        Assert.Equal("HDFC BANK LIMITED", NameNormalizer.Normalize("  hdfc   bank limited "));
    }
}
