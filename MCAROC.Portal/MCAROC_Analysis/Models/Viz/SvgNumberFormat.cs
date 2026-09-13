using System.Globalization;

namespace MCAROC_Analysis.Models.Viz;

/// <summary>The one place an SVG coordinate/dimension becomes a string, shared by every chart partial
/// (#120, C9) — InvariantCulture always, since SVG attribute syntax requires '.' as the decimal
/// separator regardless of the server's ambient culture (the same pitfall #123/C5b hit with
/// <c>DetailsFormat.Money()</c>'s un-pinned N2 format, and #116 caught before shipping
/// <c>_Sparkline.cshtml</c>'s own private copy of this exact logic).</summary>
public static class SvgNumberFormat
{
    public static string N(double n) => n.ToString("0.##", CultureInfo.InvariantCulture);
}
