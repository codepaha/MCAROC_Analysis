using System.Globalization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MCAROC_Analysis.Models;
using Microsoft.Extensions.Caching.Memory;

namespace MCAROC_Analysis.Services.PreLoginReports;

public sealed record GeneratedReport(byte[] Bytes, string FileName);

public sealed class PreLoginReportService(InstaFinancialsClient client, IWebHostEnvironment environment, IMemoryCache cache)
{
    private const string DraftCachePrefix = "pre-login-report:";

    public async Task<PreLoginReportDraftViewModel> PrepareDraftAsync(PreLoginReportViewModel request, CancellationToken cancellationToken)
    {
        var data = await FetchDataAsync(request.Cin, request.CompanyName, cancellationToken);
        var draftId = Guid.NewGuid().ToString("N");
        // The shared IMemoryCache is registered with a SizeLimit (DossierCache), so every entry must
        // declare a Size — one draft counts as one unit.
        cache.Set(DraftCachePrefix + draftId, new CachedDraft(request.Cin, request.Format, data),
            new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15), Size = 1 });
        return ToDraft(draftId, request.Cin, request.Format, data);
    }

    public async Task<InstaReportData> FetchDataAsync(string cin, string? submittedCompanyName, CancellationToken cancellationToken)
    {
        try
        {
            var data = await client.GetCompanyAsync(cin, cancellationToken);
            return data with { Company = data.Company with { Name = data.Company.Name == "-" ? submittedCompanyName?.Trim() ?? cin : data.Company.Name } };
        }
        catch (HttpRequestException) { throw new PreLoginReportException("The report data service could not be reached. Please try again later.", retryable: true); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new PreLoginReportException("The report data request timed out. It will be retried automatically.", retryable: true); }
    }

    public async Task<GeneratedReport> GenerateDraftAsync(PreLoginReportDraftViewModel draft, CancellationToken cancellationToken)
    {
        if (!cache.TryGetValue<CachedDraft>(DraftCachePrefix + draft.DraftId, out var cached) || cached is null)
            throw new PreLoginReportException("This review session expired. Fetch the CIN data again before generating the report.");
        if (!string.Equals(cached.Cin, draft.Cin, StringComparison.Ordinal) || cached.Format != draft.Format ||
            cached.Data.Charges.Count != draft.Charges.Count || cached.Data.Directors.Count != draft.Directors.Count)
            throw new PreLoginReportException("The report data changed unexpectedly. Fetch and review the CIN data again.");

        return await GenerateFromDataAsync(draft.Cin, draft.Format, ApplyEdits(cached.Data, draft), cancellationToken);
    }

    public async Task<GeneratedReport> GenerateFromDataAsync(string cin, PreLoginReportFormat format, InstaReportData data, CancellationToken cancellationToken)
    {
        var companyName = data.Company.Name;
        var template = Path.Combine(environment.ContentRootPath, "ReportTemplates", format == PreLoginReportFormat.Sbi ? "SBI" : "PRR",
            format == PreLoginReportFormat.Sbi ? "sbi-template.docx" : "prr-template.docx");
        if (!File.Exists(template)) throw new PreLoginReportException("The selected report template is unavailable on this server.");

        var temporaryPath = Path.Combine(Path.GetTempPath(), $"mcaroc-{Guid.NewGuid():N}.docx");
        try
        {
            File.Copy(template, temporaryPath, overwrite: true);
            using (var document = WordprocessingDocument.Open(temporaryPath, true))
            {
                var body = document.MainDocumentPart?.Document?.Body
                    ?? throw new PreLoginReportException("The selected report template is invalid.");
                var sectionProperties = body.Elements<SectionProperties>().LastOrDefault()?.CloneNode(true) as SectionProperties ?? new SectionProperties();
                body.RemoveAllChildren();
                if (format == PreLoginReportFormat.Sbi) BuildSbi(body, cin, companyName, data);
                else BuildPrr(body, cin, companyName, data);
                body.Append(sectionProperties);
                document.MainDocumentPart!.Document.Save();
            }
            var fileName = SafeFileName(companyName, cin) + $"_{format.ToString().ToUpperInvariant()}.docx";
            return new GeneratedReport(await File.ReadAllBytesAsync(temporaryPath, cancellationToken), fileName);
        }
        catch (PreLoginReportException) { throw; }
        catch (Exception ex) when (ex is IOException or OpenXmlPackageException)
        {
            throw new PreLoginReportException("The report could not be generated. Please try again or contact support.");
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    private static PreLoginReportDraftViewModel ToDraft(string draftId, string cin, PreLoginReportFormat format, InstaReportData data) => new()
    {
        DraftId = draftId, Cin = cin, Format = format, Company = ToEditable(data.Company),
        Charges = data.Charges.Select(c => new EditableChargeViewModel { Id = c.Id, Holder = c.Holder, Created = c.Created, Modified = c.Modified, Satisfied = c.Satisfied, Amount = c.Amount, IsOpen = c.IsOpen }).ToList(),
        Directors = data.Directors.Select(d => new EditableDirectorViewModel { Name = d.Name, DinOrPan = d.DinOrPan, Designation = d.Designation, Appointed = d.Appointed }).ToList()
    };

    private static InstaReportData ApplyEdits(InstaReportData original, PreLoginReportDraftViewModel draft) => new(
        new InstaCompany(draft.Company.Name, draft.Company.RocName, draft.Company.RegistrationNumber, draft.Company.Category, draft.Company.Subcategory,
            draft.Company.Class, draft.Company.AuthorisedCapital, draft.Company.PaidUpCapital, draft.Company.Members, draft.Company.Incorporated,
            draft.Company.Address, draft.Company.Email, draft.Company.Listed, draft.Company.LastAgm, draft.Company.BalanceSheetDate, draft.Company.Status),
        draft.Charges.Select(c => new InstaCharge(c.Id, c.Holder, c.Created, c.Modified, c.Satisfied, c.Amount, c.IsOpen)).ToList(),
        draft.Directors.Select(d => new InstaDirector(d.Name, d.DinOrPan, d.Designation, d.Appointed)).ToList());

    private static EditableCompanyViewModel ToEditable(InstaCompany c) => new()
    {
        Name = c.Name, RocName = c.RocName, RegistrationNumber = c.RegistrationNumber, Category = c.Category, Subcategory = c.Subcategory,
        Class = c.Class, AuthorisedCapital = c.AuthorisedCapital, PaidUpCapital = c.PaidUpCapital, Members = c.Members, Incorporated = c.Incorporated,
        Address = c.Address, Email = c.Email, Listed = c.Listed, LastAgm = c.LastAgm, BalanceSheetDate = c.BalanceSheetDate, Status = c.Status
    };

    private sealed record CachedDraft(string Cin, PreLoginReportFormat Format, InstaReportData Data);

    private static void BuildPrr(Body body, string cin, string name, InstaReportData data)
    {
        body.Append(Title("MCA-ROC Search Report", underline: true));
        body.Append(KeyValueTable(new[] { ("Assignment No", ""), ("SystemId", ""), ("CIN Number", cin), ("Name of the company", name),
            ("Generation date", DateTime.Now.ToString("dd/MM/yyyy h:mm:ss tt", CultureInfo.InvariantCulture)), ("Result", $"{data.Charges.Count} charge(s) found.") }, accentLabels: true));
        body.Append(Heading("Company Details"));
        body.Append(CompanyTable(cin, name, data.Company));
        body.Append(Heading("MCA-ROC Charges"));
        body.Append(KeyValueTable(new[] { ("ROC-MCA Total Charges (Loan)", data.Charges.Count.ToString(CultureInfo.InvariantCulture)),
            ("Total Open Charge Amount (in Rs.)", OpenAmount(data.Charges)) }, accentLabels: true));
        body.Append(ChargesTable(data.Charges, includeServiceRequestNumber: false));
        body.Append(Heading("Directors / Signatories"));
        body.Append(Paragraph($"Total Directors / Signatories: {data.Directors.Count}", 9));
        body.Append(DirectorsTable(data.Directors));
        body.Append(Paragraph($"Report generated on {DateTime.Now:dd-MM-yyyy}", 9, JustificationValues.Right));
    }

    private static void BuildSbi(Body body, string cin, string name, InstaReportData data)
    {
        body.Append(KeyValueTable(new[] { ("REQUEST NO:", ""), ("PREPARED ON:", DateTime.Now.ToString("dd MMMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant()) }, accentLabels: false));
        body.Append(Heading("Company Details", JustificationValues.Left));
        body.Append(CompanyTable(cin, name, data.Company, sbiLabels: true));
        body.Append(Heading("SUMMARY", JustificationValues.Left));
        body.Append(Paragraph($"CIN : {cin}", 10));
        body.Append(Paragraph($"Company Name: {name}", 10));
        body.Append(Heading("Legal Cases", JustificationValues.Left));
        body.Append(SimpleTable(new[] { "SC\nSupreme Court", "HC\nHigh Court", "DC\nDistrict Court", "CC\nConsumer Court", "ITAT/CESTAT", "*Others" },
            new[] { new[] { "0", "0", "0", "0", "0", "0" } }));
        body.Append(Paragraph("*Others include cases from NCLT, NCLAT and DRT", 8));
        body.Append(Heading("MCA-ROC Details", JustificationValues.Left));
        body.Append(KeyValueTable(new[] { ("ROC-MCA Total Charges (Loans)", data.Charges.Count.ToString(CultureInfo.InvariantCulture)) }, accentLabels: false));
        body.Append(Heading("Details of MCA-ROC CHARGES", JustificationValues.Left));
        body.Append(ChargesTable(data.Charges, includeServiceRequestNumber: true));
        body.Append(Heading("Details of legal cases", JustificationValues.Left));
        body.Append(Paragraph("No records found", 10));
        body.Append(Heading("Disclaimer", JustificationValues.Left));
        body.Append(Paragraph($"This report contains information about {name} which has been compiled using data available online in public domain. To that effect, the correctness, accuracy and completeness of this report are directly related to the data available online in public domain. This report is not to be treated as advice in any form and users are advised to carry out necessary due diligence, verification or seek proper professional advice before taking any decision.", 9));
        body.Append(Paragraph("PS: This report is computer generated and hence authorized signature not required", 9));
    }

    private static Table CompanyTable(string cin, string name, InstaCompany c, bool sbiLabels = false) => KeyValueTable(new[]
    {
        ("CIN" + (sbiLabels ? string.Empty : " Number"), cin), ("Company Name" + (sbiLabels ? string.Empty : " of the Company"), name),
        (sbiLabels ? "ROC Name" : "ROC Code", c.RocName), ("Registration Number", c.RegistrationNumber),
        (sbiLabels ? "Date of Incorporation" : "Company Category", sbiLabels ? c.Incorporated : c.Category),
        (sbiLabels ? "Email Id" : "Company Subcategory", sbiLabels ? c.Email : c.Subcategory),
        (sbiLabels ? "Registered Address" : "Class of Company", sbiLabels ? c.Address : c.Class),
        (sbiLabels ? "Listed in Stock Exchange(s) (Y/N)" : "Authorised Capital (in Rs.)", sbiLabels ? c.Listed : c.AuthorisedCapital),
        (sbiLabels ? "Category of Company" : "Paid up Capital (in Rs.)", sbiLabels ? c.Category : c.PaidUpCapital),
        (sbiLabels ? "Subcategory of the Company" : "Number of Members", sbiLabels ? c.Subcategory : c.Members),
        (sbiLabels ? "Class of Company" : "Date of Incorporation", sbiLabels ? c.Class : c.Incorporated),
        (sbiLabels ? "Authorised Capital (Rs)" : "Registered Address", sbiLabels ? c.AuthorisedCapital : c.Address),
        (sbiLabels ? "Paid up Capital (Rs)" : "Email Id", sbiLabels ? c.PaidUpCapital : c.Email),
        (sbiLabels ? "Date of last AGM" : "Whether Listed or Not", sbiLabels ? c.LastAgm : c.Listed),
        (sbiLabels ? "Date of Balance Sheet" : "Date of Last AGM", sbiLabels ? c.BalanceSheetDate : c.LastAgm),
        ("Company Status", sbiLabels ? c.Status : c.BalanceSheetDate),
        (sbiLabels ? "" : "Company Status", sbiLabels ? "" : c.Status)
    }.Where(x => !string.IsNullOrEmpty(x.Item1)), accentLabels: false);

    private static Table ChargesTable(IReadOnlyList<InstaCharge> charges, bool includeServiceRequestNumber)
    {
        var headers = includeServiceRequestNumber
            ? new[] { "Sr. No", "Service Request Number (SRN)", "Charge Id", "Charge Holder Name", "Date of Charge Creation", "Date of Modification", "Date of Satisfaction", "Amount" }
            : new[] { "Sr. No.", "Charge Id", "Charge Holder Name", "Date of Charge Creation", "Date of Modification", "Date of Satisfaction", "Amount (Rs.)" };
        var rows = charges.Select((charge, index) => includeServiceRequestNumber
            ? new[] { (index + 1).ToString(), "", charge.Id, charge.Holder, charge.Created, charge.Modified, charge.Satisfied, charge.Amount }
            : new[] { (index + 1).ToString(), charge.Id, charge.Holder, charge.Created, charge.Modified, charge.Satisfied, charge.Amount }).ToList();
        if (rows.Count == 0) rows.Add(Enumerable.Repeat("No charges found.", headers.Length).ToArray());
        return SimpleTable(headers, rows);
    }

    private static Table DirectorsTable(IReadOnlyList<InstaDirector> directors) => SimpleTable(
        new[] { "Sr. No.", "Name", "DIN / PAN", "Designation", "Date of Appointment" },
        directors.Count == 0 ? new[] { new[] { "", "No director records found.", "", "", "" } } : directors.Select((d, i) => new[] { (i + 1).ToString(), d.Name, d.DinOrPan, d.Designation, d.Appointed }));
    private static string OpenAmount(IEnumerable<InstaCharge> charges) => charges.Where(c => c.IsOpen).Sum(c => decimal.TryParse(c.Amount, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount) ? amount : 0).ToString("N0", CultureInfo.InvariantCulture);
    private static Paragraph Title(string text, bool underline = false) => Paragraph(text, 13, JustificationValues.Center, bold: true, underline: underline);
    private static Paragraph Heading(string text, JustificationValues? alignment = null) => Paragraph(text, 12, alignment ?? JustificationValues.Center, bold: true, before: "300", after: "140");
    private static Paragraph Paragraph(string text, int fontSize, JustificationValues? alignment = null, bool bold = false, bool underline = false, string? before = null, string? after = "80")
    {
        var runProperties = new RunProperties(new RunFonts { Ascii = "Verdana", HighAnsi = "Verdana" }, new FontSize { Val = (fontSize * 2).ToString(CultureInfo.InvariantCulture) });
        if (bold) runProperties.Append(new Bold()); if (underline) runProperties.Append(new Underline { Val = UnderlineValues.Single });
        return new Paragraph(new ParagraphProperties(new SpacingBetweenLines { Before = before, After = after }, new Justification { Val = alignment ?? JustificationValues.Left }), new Run(runProperties, new Text(text ?? "-") { Space = SpaceProcessingModeValues.Preserve }));
    }
    private static Table KeyValueTable(IEnumerable<(string Label, string Value)> pairs, bool accentLabels)
    {
        var table = NewTable(2); foreach (var (label, value) in pairs) table.Append(Row(new[] { label, value }, header: false, labelAccent: accentLabels)); return table;
    }
    private static Table SimpleTable(IEnumerable<string> headers, IEnumerable<string[]> rows)
    {
        var headerArray = headers.ToArray(); var table = NewTable(headerArray.Length); table.Append(Row(headerArray, true)); foreach (var row in rows) table.Append(Row(row, false)); return table;
    }
    private static Table NewTable(int columns) => new(new TableProperties(new TableWidth { Width = "0", Type = TableWidthUnitValues.Auto }, new TableBorders(new TopBorder { Val = BorderValues.Single, Size = 6 }, new BottomBorder { Val = BorderValues.Single, Size = 6 }, new LeftBorder { Val = BorderValues.Single, Size = 6 }, new RightBorder { Val = BorderValues.Single, Size = 6 }, new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 }, new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 }), new TableLayout { Type = TableLayoutValues.Fixed }), new TableGrid(Enumerable.Range(0, columns).Select(_ => new GridColumn { Width = (9000 / columns).ToString(CultureInfo.InvariantCulture) })));
    private static TableRow Row(IEnumerable<string> values, bool header, bool labelAccent = false)
    {
        var row = new TableRow(); if (header) row.Append(new TableRowProperties(new TableHeader()));
        foreach (var value in values)
        {
            var shading = header ? "D9EAD3" : labelAccent ? "DAEEF3" : null;
            var cellProperties = new TableCellProperties(new TableCellVerticalAlignment { Val = TableVerticalAlignmentValues.Center });
            if (shading is not null) cellProperties.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = shading });
            row.Append(new TableCell(cellProperties, Paragraph(value, header ? 8 : 9, header ? JustificationValues.Center : JustificationValues.Left, bold: header || labelAccent)));
        }
        return row;
    }
    private static string SafeFileName(string companyName, string cin)
    {
        var stripped = Regex.Replace(companyName ?? string.Empty, "[^A-Za-z0-9 ._-]+", string.Empty).Trim();
        return string.IsNullOrEmpty(stripped) ? cin : stripped;
    }
}
