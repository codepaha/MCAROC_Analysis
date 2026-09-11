using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.PreLoginReports;

public sealed class InstaFinancialsOptions
{
    public const string SectionName = "InstaFinancials";
    public string BaseUrl { get; init; } = "https://api.instafinancials.com/InstaReports/v2/InstaBasic/CompanyCIN";
    public string ApiKey { get; init; } = string.Empty;
    /// <summary>InstaFinancials cache-freshness control. 888 = fetch fresh from MCA, falling back to the latest
    /// cached record if the MCA refresh fails — the vendor's recommended setting for production availability.</summary>
    public int DaysToIgnore { get; init; } = 888;
}

public sealed class PreLoginReportException(string message, bool retryable = false) : Exception(message)
{
    public bool Retryable { get; } = retryable;
}

public sealed record InstaCompany(
    string Name, string RocName, string RegistrationNumber, string Category, string Subcategory,
    string Class, string AuthorisedCapital, string PaidUpCapital, string Members, string Incorporated,
    string Address, string Email, string Listed, string LastAgm, string BalanceSheetDate, string Status,
    // SBI-only fields — InstaBasic has no corresponding data, so these are always manually entered on the Review page.
    string ActiveCompliance = "-", string BooksOfAccountAddress = "-");

public sealed record InstaCharge(string Id, string Holder, string Created, string Modified, string Satisfied, string Amount, bool IsOpen, string Srn = "-");
public sealed record InstaDirector(string Name, string DinOrPan, string Designation, string Appointed);
public sealed record InstaReportData(InstaCompany Company, IReadOnlyList<InstaCharge> Charges, IReadOnlyList<InstaDirector> Directors);

public sealed class InstaFinancialsClient(HttpClient http, IOptions<InstaFinancialsOptions> options)
{
    public async Task<InstaReportData> GetCompanyAsync(string cin, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            throw new PreLoginReportException("Report generation is not configured. Set InstaFinancials:ApiKey in user secrets or the production secret store.");

        var baseUrl = settings.BaseUrl.TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/{Uri.EscapeDataString(cin)}?daysToIgnore={settings.DaysToIgnore}");
        request.Headers.Add("user-key", settings.ApiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new PreLoginReportException("No company record was returned for this CIN. LLPINs and some struck-off companies are not supported by this endpoint.");
        if (!response.IsSuccessStatusCode)
            throw new PreLoginReportException($"The report data service returned HTTP {(int)response.StatusCode}. Please try again later.",
                response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!json.RootElement.TryGetProperty("ReportData", out var reportData))
            throw new PreLoginReportException("The report data service returned an unexpected response.");

        var companyElement = Property(reportData, "companyData");
        if (companyElement.ValueKind != JsonValueKind.Object)
            throw new PreLoginReportException("The report data service returned no company data for this CIN.");

        var company = new InstaCompany(
            Text(companyElement, "company"), Text(companyElement, "rocName"), Text(companyElement, "registrationNumber"),
            Text(companyElement, "companyCategory"), Text(companyElement, "companySubcategory"), Text(companyElement, "classOfCompany"),
            Money(companyElement, "authorisedCapital"), Money(companyElement, "paidUpCapital"), Text(companyElement, "numberOfMembers", "0"),
            Date(companyElement, "dateOfIncorporation"), Address(companyElement), Text(companyElement, "emailAddress"),
            Listed(Text(companyElement, "whetherListedOrNot", "")), Date(companyElement, "dateOfLastAGM"),
            Date(companyElement, "balanceSheetDate"), Text(companyElement, "llpStatus", Text(companyElement, "statusUnderCIRP")));

        var charges = Array(Property(reportData, "indexChargesData")).Select(c => new InstaCharge(
            Text(c, "chargeId"), Text(c, "chName", Text(c, "chargeHolderName")), Date(c, "dateOfCreation"),
            Date(c, "dateOfModification"), Date(c, "dateOfSatisfaction"), Money(c, "amount"),
            string.Equals(Text(c, "chargeStatus", ""), "open", StringComparison.OrdinalIgnoreCase),
            Text(c, "SRN"))).ToList();

        var directors = Array(Property(reportData, "directorData"))
            .Select(d =>
            {
                var name = string.Join(' ', new[] { Text(d, "FirstName", ""), Text(d, "MiddleName", ""), Text(d, "LastName", "") }.Where(x => !string.IsNullOrWhiteSpace(x) && x != "-"));
                // All 3 name parts can be individually filtered out (e.g. all "." placeholders), leaving an
                // empty join that the "-" checks below wouldn't catch — normalize it to "-" so this entry is
                // excluded like any other nameless record, instead of appearing with a blank name.
                if (string.IsNullOrWhiteSpace(name)) name = "-";
                return new { Raw = d, Name = name, Din = Text(d, "DIN"), Pan = Text(d, "PAN"), Appointed = Date(d, "dateOfAppointment") };
            })
            .Where(x => x.Name != "-")
            // InstaBasic's directorData is one row per historical role event against this company, not one
            // row per person — the same DIN/PAN can appear more than once (e.g. re-appointed under a
            // different designation years later). Collapse to one row per person, keeping whichever
            // appointment is most recent so the report reflects current status, not a stale earlier role.
            .GroupBy(x => x.Din != "-" ? $"DIN:{x.Din}" : x.Pan != "-" ? $"PAN:{x.Pan}" : $"NAME:{x.Name}")
            .Select(g => g.OrderByDescending(x => AppointmentSortKey(x.Appointed)).First())
            .Select(x => new InstaDirector(
                CultureInfo.InvariantCulture.TextInfo.ToTitleCase(x.Name.ToLowerInvariant()),
                x.Din != "-" ? x.Din : x.Pan != "-" ? $"PAN {x.Pan}" : "-", Designation(x.Raw, cin), x.Appointed))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();

        return new InstaReportData(company, charges, directors);
    }

    private static JsonElement Property(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var result) ? result : default;
    private static IEnumerable<JsonElement> Array(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];
    private static string Text(JsonElement value, string property, string fallback = "-")
    {
        var item = Property(value, property);
        var text = item.ValueKind == JsonValueKind.String ? item.GetString() : item.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? item.ToString() : null;
        text = text?.Trim();
        // InstaBasic uses "." as a placeholder for a genuinely missing value (seen on director
        // FirstName/MiddleName/LastName) rather than omitting the field or using "-"/"NA" like elsewhere.
        return string.IsNullOrWhiteSpace(text) || string.Equals(text, "NA", StringComparison.OrdinalIgnoreCase) || text == "." ? fallback : text;
    }
    private static string Money(JsonElement value, string property)
    {
        var raw = Text(value, property);
        return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount) ? amount.ToString("N0", CultureInfo.InvariantCulture) : raw;
    }
    private static string Date(JsonElement value, string property)
    {
        var raw = Text(value, property);
        if (raw == "-" || !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) return raw;
        // InstaBasic uses 01-01-1900 as a sentinel for "no real date" rather than omitting the field —
        // no genuine MCA filing date predates 1900, so any parsed year that low is the sentinel, not data.
        return date.Year <= 1900 ? "-" : date.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
    }
    private static string Listed(string raw) => raw.ToUpperInvariant() switch { "Y" => "Listed", "N" => "Unlisted", _ => raw };
    private static string Address(JsonElement company)
    {
        var addresses = Property(company, "MCAMDSCompanyAddress");
        if (addresses.ValueKind != JsonValueKind.Array) return "-";
        var candidate = addresses.EnumerateArray().FirstOrDefault(a => Text(a, "addressType", "").Contains("regist", StringComparison.OrdinalIgnoreCase));
        if (candidate.ValueKind != JsonValueKind.Object) candidate = addresses.EnumerateArray().FirstOrDefault();
        if (candidate.ValueKind != JsonValueKind.Object) return "-";
        var pieces = new[] { "streetAddress", "streetAddress2", "streetAddress3", "streetAddress4", "locality", "district", "city", "state", "country", "postalCode" }
            .Select(x => Text(candidate, x, "")).Where(x => !string.IsNullOrWhiteSpace(x) && x != "-").Distinct(StringComparer.OrdinalIgnoreCase);
        return string.Join(", ", pieces).Trim() is { Length: > 0 } address ? address : "-";
    }
    private static string Designation(JsonElement director, string cin)
    {
        var roles = Array(Property(director, "MCAUserRole")).Where(r => Text(r, "cin", "") == cin || Text(r, "ucin", "") == cin).ToList();
        // Text()'s default fallback is "-", which is required here: passing "" as the fallback would make
        // a genuinely blank "designation" field return "" instead of "-", which already satisfies the
        // `!= "-"` check below and short-circuits before ever trying the roleLICValue fallback.
        return roles.Select(r => Text(r, "designation")).FirstOrDefault(x => x != "-")
            ?? roles.Select(r => Text(r, "roleLICValue")).FirstOrDefault(x => x != "-") ?? "Director";
    }
    private static DateTime AppointmentSortKey(string formattedDate) =>
        DateTime.TryParseExact(formattedDate, "dd-MM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : DateTime.MinValue;
}
