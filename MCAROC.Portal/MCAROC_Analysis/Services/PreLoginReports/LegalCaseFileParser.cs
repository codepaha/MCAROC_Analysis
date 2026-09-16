using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using ExcelDataReader;

namespace MCAROC_Analysis.Services.PreLoginReports;

/// <summary>One litigation case, normalized from whatever litigation-tracker export (multi-sheet .xlsx or
/// flat .csv) the user uploads for a request. <see cref="Category"/> is internal-only (drives the existing
/// 9-bucket <see cref="InstaLegalCases"/> counts) and is never rendered as its own report column — the
/// report shows <see cref="Court"/> (a real court/forum name) instead.</summary>
public sealed record LegalCaseRecord(
    string Category, string Court, string CnrNumber, string CaseNo, string CaseType, string CaseYear, string CaseStage,
    string Act, string DateOfFiling, string State, string District, string CaseDetails, string DateOfHearing, string Status);

/// <summary>Parses the two litigation-tracker export shapes seen from clients into <see cref="LegalCaseRecord"/>:
/// a multi-sheet workbook with one sheet per court level (District/High Court/Supreme/Other/Appellate Cases —
/// "Summary" and "Defaulter Cases" sheets are a different shape entirely and are skipped), or a single flat
/// CSV covering all levels with a per-row "Court" category column. Both shapes share the same core column
/// names (Court, Case_Number, Case_Type, Case_Status, Case_Stage, Case_Year, Court_Forum, State, District,
/// Petitioners, Respondents, LDOH_NDOH, Act, Cnr_Number, Filing_Date) — this reads only those, tolerating
/// sheet-specific extra columns (e.g. High Court's Judges/Case_History) by looking columns up by header
/// name rather than position.</summary>
public static class LegalCaseFileParser
{
    private static readonly string[] SkippedSheets = ["Summary", "Defaulter Cases"];

    // Fallback category when a sheet has no per-row "Court" value of its own (Appellate Cases never
    // populates it) — keyed by sheet title since that's the only signal available for those rows.
    private static readonly Dictionary<string, string> SheetFallbackCategory = new(StringComparer.OrdinalIgnoreCase)
    {
        ["District Cases"] = "district",
        ["High Court Cases"] = "highcourt",
        ["Supreme Court Cases"] = "supreme",
        ["Other Court Cases"] = "other",
        ["Appellate Cases"] = "appellate",
    };

    private static readonly string[] RequiredColumns = ["Court", "Case_Number", "Case_Type", "Case_Status", "Case_Stage", "Case_Year", "Court_Forum", "State", "District", "Petitioners", "Respondents", "LDOH_NDOH", "Act"];

    private static string NormalizeBench(string bench)
    {
        if (bench is "-" || string.Equals(bench, "Not Found", StringComparison.OrdinalIgnoreCase)) return "-";
        var spaced = Regex.Replace(bench.Replace('_', ' ').TrimEnd('.'), @"\s*,\s*", ", ");
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(spaced.ToLowerInvariant());
    }

    // Buckets each parsed case's Category into the report's existing 9-column summary (Supreme/High/
    // District/Consumer/ITAT-CESTAT/NCLT-NCLAT/DRT/RERA/NGT-Others) — the wireframe's own footnote ("Others
    // include cases from SEBI/SAT, APTEL, and IPAB") is exactly this catch-all bucket, so any category this
    // parser doesn't specifically recognize (appellate-tribunal rows with no clearer signal, etc.) lands
    // there rather than being silently dropped from the counts.
    private static readonly string[] KnownCategories = ["district", "highcourt", "supreme", "consumer", "itat", "cestat", "drt", "nclt", "nclat", "rera", "ngt"];

    public static InstaLegalCases ToInstaLegalCases(IReadOnlyList<LegalCaseRecord> cases)
    {
        int Count(params string[] categories) => cases.Count(c => categories.Contains(c.Category, StringComparer.OrdinalIgnoreCase));
        return new InstaLegalCases(
            SupremeCourt: Count("supreme"), HighCourt: Count("highcourt"), DistrictCourt: Count("district"),
            ConsumerForum: Count("consumer"), ItatTax: Count("itat", "cestat"), NcltNclat: Count("nclt", "nclat"),
            DrtDrat: Count("drt"), Rera: Count("rera"),
            NgtOthers: cases.Count(c => !KnownCategories.Contains(c.Category, StringComparer.OrdinalIgnoreCase)),
            Cases: cases);
    }

    public static IReadOnlyList<LegalCaseRecord> Parse(Stream stream, string fileName)
    {
        var isCsv = Path.GetExtension(fileName).Equals(".csv", StringComparison.OrdinalIgnoreCase);
        List<LegalCaseRecord> records;
        try
        {
            using var reader = isCsv ? ExcelReaderFactory.CreateCsvReader(stream) : ExcelReaderFactory.CreateReader(stream);
            var dataSet = reader.AsDataSet(new ExcelDataSetConfiguration { ConfigureDataTable = _ => new ExcelDataTableConfiguration { UseHeaderRow = false } });
            records = [];
            foreach (DataTable table in dataSet.Tables)
            {
                if (SkippedSheets.Contains(table.TableName, StringComparer.OrdinalIgnoreCase)) continue;
                SheetFallbackCategory.TryGetValue(table.TableName, out var fallbackCategory);
                ParseTable(table, fallbackCategory, records);
            }
        }
        catch (Exception ex) when (ex is not PreLoginReportException)
        {
            throw new PreLoginReportException("The litigation file could not be read. Upload the .xlsx export or the flat .csv export, unmodified.");
        }
        if (records.Count == 0)
            throw new PreLoginReportException("No litigation cases could be found in the uploaded file. Check it has the expected columns (Court, Case_Number, Court_Forum, ...) and try again.");
        return records;
    }

    private static void ParseTable(DataTable table, string? fallbackCategory, List<LegalCaseRecord> records)
    {
        var rows = table.Rows.Cast<DataRow>().ToList();
        var headerRowIndex = rows.FindIndex(r => ColumnIndex(r, table.Columns.Count, "Court_Forum") >= 0);
        if (headerRowIndex < 0) return; // not a case-shaped sheet (e.g. a stray/blank sheet) — nothing to extract

        var header = rows[headerRowIndex];
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < table.Columns.Count; i++)
        {
            var name = Cell(header, i);
            if (!string.IsNullOrWhiteSpace(name) && !columns.ContainsKey(name)) columns[name] = i;
        }
        if (RequiredColumns.Any(c => !columns.ContainsKey(c))) return; // not the litigation-case column shape

        for (var r = headerRowIndex + 1; r < rows.Count; r++)
        {
            var row = rows[r];
            var caseNo = Field(row, columns, "Case_Number");
            var courtForum = Field(row, columns, "Court_Forum");
            if (caseNo == "-" && courtForum == "-") continue; // blank spacer row

            var rowCategory = Field(row, columns, "Court").ToLowerInvariant();
            var category = rowCategory is "-" or "na" or "" ? fallbackCategory ?? "other" : rowCategory;

            // Appellate-tribunal rows never populate Court_Forum — "Bench" (e.g. "nclt,cuttack bench.") is
            // the only court-identifying text those rows carry, so fall back to it rather than show "-".
            var court = courtForum != "-" ? courtForum : NormalizeBench(Field(row, columns, "Bench"));

            records.Add(new LegalCaseRecord(
                Category: category,
                Court: court,
                CnrNumber: Field(row, columns, "Cnr_Number"),
                CaseNo: caseNo,
                CaseType: Field(row, columns, "Case_Type"),
                CaseYear: Field(row, columns, "Case_Year"),
                CaseStage: Field(row, columns, "Case_Stage"),
                Act: Field(row, columns, "Act"),
                DateOfFiling: Field(row, columns, "Filing_Date"),
                State: Field(row, columns, "State"),
                District: Field(row, columns, "District"),
                CaseDetails: BuildCaseDetails(Field(row, columns, "Petitioners"), Field(row, columns, "Respondents")),
                DateOfHearing: Field(row, columns, "LDOH_NDOH"),
                Status: Field(row, columns, "Case_Status")));
        }
    }

    // These exports list every party twice (or, on some High Court rows, three times) back-to-back: once
    // bare, once again with a "N) " serial prefix, and — on rows where the source jams the advocate list
    // into the same field — once more as "PARTY NAME ADVOCATE -Firm/Person". Split on the ", , " separator,
    // strip any serial prefix and any trailing " ADVOCATE -..." annotation, and de-duplicate
    // case-insensitively (keeping first-seen casing and order) so a 40-50-party case renders each party
    // once, not two or three times.
    private static readonly Regex SerialPrefix = new(@"^\d+\)\s*", RegexOptions.Compiled);
    private static readonly Regex AdvocateSuffix = new(@"\s+ADVOCATE\s*-.*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string BuildCaseDetails(string petitioners, string respondents)
    {
        var p = DedupeParties(petitioners);
        var r = DedupeParties(respondents);
        if (p == "-" && r == "-") return "-";
        return $"{(p == "-" ? "Unknown" : p)} VS {(r == "-" ? "Unknown" : r)}";
    }

    private static string DedupeParties(string raw)
    {
        if (raw == "-") return "-";
        var seen = new List<string>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split(", ,", StringSplitOptions.None))
        {
            var name = AdvocateSuffix.Replace(SerialPrefix.Replace(part.Trim(), "").Trim(), "").Trim();
            if (name.Length == 0) continue;
            if (seenKeys.Add(name)) seen.Add(name);
        }
        return seen.Count == 0 ? "-" : string.Join(", ", seen);
    }

    private static int ColumnIndex(DataRow row, int columnCount, string headerName)
    {
        for (var i = 0; i < columnCount; i++)
            if (string.Equals(Convert.ToString(row[i], CultureInfo.InvariantCulture)?.Trim(), headerName, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static string Cell(DataRow row, int index) => index >= 0 && index < row.ItemArray.Length
        ? Convert.ToString(row[index], CultureInfo.InvariantCulture)?.Trim() ?? "" : "";

    private static string Field(DataRow row, Dictionary<string, int> columns, string name)
    {
        if (!columns.TryGetValue(name, out var index)) return "-";
        var raw = row[index];
        if (raw is null or DBNull) return "-";
        var text = raw switch
        {
            double d => d == Math.Floor(d) && Math.Abs(d) < 1e15 ? d.ToString("F0", CultureInfo.InvariantCulture) : d.ToString(CultureInfo.InvariantCulture),
            _ => Convert.ToString(raw, CultureInfo.InvariantCulture)
        };
        text = text?.Trim();
        return string.IsNullOrWhiteSpace(text) || string.Equals(text, "NA", StringComparison.OrdinalIgnoreCase) ? "-" : text;
    }
}
