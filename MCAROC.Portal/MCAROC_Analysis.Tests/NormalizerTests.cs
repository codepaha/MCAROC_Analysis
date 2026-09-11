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

public class DateTimeNormalizerTests
{
    [Fact]
    public void ParsesSourceTimestampWithTrailingHours()
    {
        Assert.True(DateTimeNormalizer.TryParse("9 Sep, 2026 09:32 Hours", out var result));
        Assert.Equal(new DateTime(2026, 9, 9, 9, 32, 0), result);
    }

    [Theory]
    [InlineData("9 Sep, 2026 09:32 Hours")]
    [InlineData("9 Sep, 2026 09:32 hours")]
    [InlineData("9 Sep, 2026 09:32 hrs")]
    [InlineData("9 Sep, 2026 09:32 hr")]
    [InlineData("9 Sep, 2026 09:32")]
    public void StripsTerminalHourVariants(string input)
    {
        Assert.True(DateTimeNormalizer.TryParse(input, out var result));
        Assert.Equal(new DateTime(2026, 9, 9, 9, 32, 0), result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("-")]
    [InlineData("")]
    [InlineData("   ")]
    public void TreatsBlankAndDashAsNull(string? input)
    {
        Assert.True(DateTimeNormalizer.TryParse(input, out var result));
        Assert.Null(result);
    }

    [Fact]
    public void PassesThroughNativeDateTimeCells()
    {
        var dt = new DateTime(2026, 9, 9, 9, 32, 0);
        Assert.True(DateTimeNormalizer.TryParse(dt, out var result));
        Assert.Equal(dt, result);
    }

    [Theory]
    [InlineData("not a date")]
    [InlineData("99/99/9999 25:61")]
    [InlineData("2026-13-45")]
    public void ReturnsFalseForUnparsableText(string input)
    {
        Assert.False(DateTimeNormalizer.TryParse(input, out _));
    }
}

