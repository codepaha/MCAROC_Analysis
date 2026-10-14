using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.Chat;

/// <summary>What identifies the client in one request's questions: the company's current and former names and its people.</summary>
public sealed record MaskContext(IReadOnlyCollection<string> CompanyNames, IReadOnlyCollection<string> PersonNames);

/// <summary>
/// Removes what identifies a client from a chat question before it is compared with other clients' questions (#499): the company's names (current and
/// former, with and without the legal form), its people's names, CIN / LLPIN / PAN / GSTIN, case numbers, amounts, e-mail addresses, phone numbers and
/// any other long number. What stays is the shape of the question ("When did [company] change its name?"). Years and lender names stay: they say what
/// kind of question it is, not whose.
/// </summary>
public static partial class QuestionMasker
{
    public const string Company = "[company]";

    public static string Mask(string question, MaskContext context)
    {
        var t = question.Trim();

        // Names first, longest first, so "Coastal Projects Limited" goes before "Coastal Projects".
        foreach (var name in context.CompanyNames.SelectMany(NameForms).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length))
            t = Regex.Replace(t, $@"(?<![\w]){Regex.Escape(name)}(?![\w])", Company, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (var person in context.PersonNames.Where(p => p.Trim().Length >= 4).Select(p => p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n => n.Length))
            t = Regex.Replace(t, $@"(?<![\w]){Regex.Escape(person)}(?![\w])", "[person]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        t = Cin().Replace(t, "[cin]");
        t = Llpin().Replace(t, "[cin]");
        t = Gstin().Replace(t, "[gstin]");
        t = Pan().Replace(t, "[pan]");
        t = Email().Replace(t, "[contact]");
        t = CaseNumber().Replace(t, "[case-no]");
        t = Amount().Replace(t, "[amount]");
        t = Phone().Replace(t, "[contact]");
        t = LongNumber().Replace(t, "[number]");
        return Spaces().Replace(t, " ").Trim();
    }

    /// <summary>A name as written, and without its legal form ("Coastal Projects Limited" → "Coastal Projects"), when what remains is still distinctive.</summary>
    private static IEnumerable<string> NameForms(string name)
    {
        var n = Spaces().Replace(name.Trim(), " ");
        if (n.Length == 0) yield break;
        yield return n;
        var core = LegalForm().Replace(n, "").Trim();
        if (core.Length >= 5 && core.Contains(' ')) yield return core;
    }

    [GeneratedRegex(@"\s+(private\s+limited|pvt\.?\s+ltd\.?|limited|ltd\.?|llp)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegalForm();

    [GeneratedRegex(@"\b[UL]\d{5}[A-Z]{2}\d{4}[A-Z]{3}\d{6}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Cin();

    [GeneratedRegex(@"\b[A-Z]{3}-\d{4}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Llpin();

    [GeneratedRegex(@"\b\d{2}[A-Z]{5}\d{4}[A-Z][0-9A-Z]Z[0-9A-Z]\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Gstin();

    [GeneratedRegex(@"\b[A-Z]{5}\d{4}[A-Z]\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pan();

    [GeneratedRegex(@"\b[\w.+-]+@[\w-]+(\.[\w-]+)+\b", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    // "OA/77/2022", "O.A. No. 1234 of 2019", "CP(IB) 56/2021", "WP 1234/2020"
    [GeneratedRegex(@"\b[A-Z][A-Za-z.()]{0,14}(?:\s*No\.?)?[\s/.-]*\d{1,6}\s*(?:/\s*[A-Za-z]{1,6}\s*)?(?:/|\bof\b)\s*(?:19|20)\d{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex CaseNumber();

    // "Rs 2.5 crore", "₹1,20,000", "INR 50 lakh", "3.2 cr"
    [GeneratedRegex(@"(?:(?:rs\.?|inr|₹)\s*[\d,]+(?:\.\d+)?(?:\s*(?:crores?|cr|lakhs?|lacs?|million|mn|billion|bn|thousand|k))?|\b\d[\d,]*(?:\.\d+)?\s*(?:crores?|cr|lakhs?|lacs?)\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Amount();

    [GeneratedRegex(@"(?<!\w)(?:\+91[\s-]?)?[6-9]\d{9}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Phone();

    // A remaining run of 6+ digits (account, SRN, charge id...). Years (4 digits) stay.
    [GeneratedRegex(@"\b[A-Z]?\d{6,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LongNumber();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
