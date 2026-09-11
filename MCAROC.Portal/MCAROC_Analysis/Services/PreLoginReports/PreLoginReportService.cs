using System.Globalization;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using MCAROC_Analysis.Models;

namespace MCAROC_Analysis.Services.PreLoginReports;

public sealed record GeneratedReport(byte[] Bytes, string FileName);

public sealed class PreLoginReportService(InstaFinancialsClient client, IWebHostEnvironment environment)
{
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
                if (format == PreLoginReportFormat.Sbi)
                {
                    // The SBI template is a real, natively-formatted sample report (not a blank scaffold) —
                    // fill its existing cells/rows in place rather than wiping and rebuilding, so its Word
                    // formatting (fonts, borders, shading, letterhead) survives untouched.
                    FillSbiTemplate(document, cin, companyName, data);
                }
                else
                {
                    PrrTemplateFiller.Fill(body, cin, companyName, data);
                }
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

    private static void FillSbiTemplate(WordprocessingDocument document, string cin, string companyName, InstaReportData data)
    {
        var body = document.MainDocumentPart!.Document!.Body!;
        var c = data.Company;

        var companyDetails = FindTableAfterHeading(body, "Company Details")
            ?? throw new PreLoginReportException("The selected report template is invalid.");
        var companyFields = new (string Label, string Value)[]
        {
            ("CIN", cin), ("Company Name", companyName), ("ROC Name", c.RocName), ("Registration Number", c.RegistrationNumber),
            ("Date of Incorporation", c.Incorporated), ("Email Id", c.Email), ("Registered Address", c.Address),
            ("Address at which the books of account are to be maintained", c.BooksOfAccountAddress),
            ("Listed in Stock Exchange(s) (Y/N)", c.Listed), ("Category of Company", c.Category),
            ("Subcategory of the Company", c.Subcategory), ("Class of Company", c.Class), ("ACTIVE compliance", c.ActiveCompliance),
            ("Authorised Capital (Rs)", c.AuthorisedCapital), ("Paid up Capital (Rs)", c.PaidUpCapital),
            ("Date of last AGM", c.LastAgm), ("Date of Balance Sheet", c.BalanceSheetDate), ("Company Status", c.Status)
        };
        foreach (var (label, value) in companyFields) ReplaceCellValue(companyDetails, label, value);

        var summary = FindTableAfterHeading(body, "SUMMARY")
            ?? throw new PreLoginReportException("The selected report template is invalid.");
        var summaryRows = summary.Elements<TableRow>().ToList();
        SetCellText(summaryRows[0].Elements<TableCell>().Last(), cin);
        SetCellText(summaryRows[1].Elements<TableCell>().Last(), companyName);

        // The remaining occurrence(s) of the company name are Word content controls (Quick Parts) — one was in
        // the SUMMARY line (already handled above: SetCellText strips SdtElement children too, so that one no
        // longer exists by this point), one is in the disclaimer paragraph. Unwrap each into a plain run rather
        // than just editing its displayed text — removes the (dangling, since there's no customXml part backing
        // it) w:dataBinding entirely, so there's no risk of it ever re-rendering stale bound content.
        foreach (var sdt in document.MainDocumentPart.Document.Descendants<SdtRun>()
            .Where(sdt => sdt.SdtProperties?.GetFirstChild<SdtAlias>()?.Val?.Value == "Company").ToList())
        {
            var runProps = sdt.SdtContentRun?.Descendants<Run>().LastOrDefault()?.RunProperties?.CloneNode(true) as RunProperties;
            var plainRun = runProps is not null ? new Run(runProps, new Text(companyName)) : new Run(new Text(companyName));
            sdt.InsertAfterSelf(plainRun);
            sdt.Remove();
        }

        var mcaRocDetails = FindTableAfterHeading(body, "MCA-ROC Details")
            ?? throw new PreLoginReportException("The selected report template is invalid.");
        SetCellText(mcaRocDetails.Elements<TableRow>().Last().Elements<TableCell>().Last(), data.Charges.Count.ToString(CultureInfo.InvariantCulture));

        var chargesTable = FindTableAfterHeading(body, "Details of MCA-ROC CHARGES")
            ?? throw new PreLoginReportException("The selected report template is invalid.");
        if (data.Charges.Count > 0)
        {
            var chargeRows = chargesTable.Elements<TableRow>().ToList();
            var placeholderRow = chargeRows[1];
            var anchor = chargeRows[0];
            foreach (var (charge, index) in data.Charges.Select((ch, i) => (ch, i)))
            {
                var clone = (TableRow)placeholderRow.CloneNode(true);
                var cells = clone.Elements<TableCell>().ToList();
                SetCellText(cells[0], (index + 1).ToString(CultureInfo.InvariantCulture));
                SetCellText(cells[1], charge.Srn == "-" ? string.Empty : charge.Srn);
                SetCellText(cells[2], charge.Id);
                SetCellText(cells[3], charge.Holder);
                SetCellText(cells[4], charge.Created);
                SetCellText(cells[5], charge.Modified);
                SetCellText(cells[6], charge.Satisfied);
                SetCellText(cells[7], charge.Amount);
                anchor.InsertAfterSelf(clone);
                anchor = clone;
            }
            placeholderRow.Remove();
        }
        // Charges.Count == 0: leave the placeholder row untouched — it already renders correctly (matches
        // the template's own sample company, which also had zero charges).

        ReplacePreparedOnDateField(body);
    }

    // Replaces the template's live "PREPARED ON" DATE field with a static value — deterministic at
    // generation time, rather than depending on Word recalculating the field whenever the file is opened.
    private static void ReplacePreparedOnDateField(Body body)
    {
        var fieldBegin = body.Descendants<FieldChar>().FirstOrDefault(f => f.FieldCharType?.Value == FieldCharValues.Begin);
        if (fieldBegin?.Parent is not Run beginRun || beginRun.Parent is not Paragraph datePara) return;

        var runs = datePara.Elements<Run>().ToList();
        var beginIndex = runs.IndexOf(beginRun);
        var endIndex = runs.FindIndex(beginIndex, r => r.Descendants<FieldChar>().Any(f => f.FieldCharType?.Value == FieldCharValues.End));
        if (endIndex < beginIndex) return;

        var runProps = runs[beginIndex].RunProperties?.CloneNode(true) as RunProperties;
        var dateText = DateTime.Now.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
        var newRun = runProps is not null ? new Run(runProps, new Text(dateText)) : new Run(new Text(dateText));
        for (var i = endIndex; i >= beginIndex; i--) runs[i].Remove();
        if (beginIndex > 0) runs[beginIndex - 1].InsertAfterSelf(newRun); else datePara.PrependChild(newRun);
    }

    // Finds the Table immediately following the paragraph whose text matches headingText (skipping blank
    // spacer paragraphs in between) — robust to row/column changes elsewhere in the template.
    private static Table? FindTableAfterHeading(Body body, string headingText)
    {
        var children = body.ChildElements.ToList();
        for (var i = 0; i < children.Count; i++)
        {
            if (children[i] is not Paragraph p || GetElementText(p) != headingText) continue;
            for (var j = i + 1; j < children.Count; j++)
            {
                if (children[j] is Table t) return t;
                if (children[j] is Paragraph p2 && !string.IsNullOrWhiteSpace(GetElementText(p2))) break;
            }
        }
        return null;
    }

    // Finds the row whose label cell (first cell) matches `label` and replaces its value cell (second cell).
    private static void ReplaceCellValue(Table table, string label, string newValue)
    {
        var row = table.Elements<TableRow>().FirstOrDefault(r => GetElementText(r.Elements<TableCell>().First()) == label);
        if (row is null) return;
        SetCellText(row.Elements<TableCell>().ElementAt(1), newValue);
    }

    private static string GetElementText(OpenXmlElement element) => string.Concat(element.Descendants<Text>().Select(t => t.Text)).Trim();

    // Replaces a cell's entire content with a single clean run, cloning the last existing run's formatting
    // (font/size/bold) — sidesteps Word's spellcheck-driven run-splitting (proofErr markers) uniformly.
    private static void SetCellText(TableCell cell, string text)
    {
        var firstPara = cell.Elements<Paragraph>().FirstOrDefault() ?? cell.AppendChild(new Paragraph());
        var runProps = firstPara.Descendants<Run>().LastOrDefault()?.RunProperties?.CloneNode(true) as RunProperties;
        foreach (var extraPara in cell.Elements<Paragraph>().Skip(1).ToList()) extraPara.Remove();
        // Includes SdtRun (content controls) — a cell whose value is entirely a content control (as the
        // SUMMARY table's Company Name cell is) would otherwise be left untouched and get a second, duplicate
        // run appended beside it rather than actually being replaced.
        foreach (var child in firstPara.ChildElements.Where(x => x is Run or ProofError or BookmarkStart or BookmarkEnd or SdtRun).ToList()) child.Remove();
        var newRun = runProps is not null ? new Run(runProps, new Text(text) { Space = SpaceProcessingModeValues.Preserve })
            : new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        firstPara.Append(newRun);
    }

    public static PreLoginReportDraftViewModel ToDraft(long jobId, string cin, PreLoginReportFormat format, InstaReportData data) => new()
    {
        JobId = jobId, Cin = cin, Format = format, Company = ToEditable(data.Company),
        Charges = data.Charges.Select(c => new EditableChargeViewModel { Srn = c.Srn, Id = c.Id, Holder = c.Holder, Created = c.Created, Modified = c.Modified, Satisfied = c.Satisfied, Amount = c.Amount, IsOpen = c.IsOpen }).ToList(),
        Directors = data.Directors.Select(d => new EditableDirectorViewModel { Name = d.Name, DinOrPan = d.DinOrPan, Designation = d.Designation, Appointed = d.Appointed }).ToList()
    };

    public static InstaReportData ApplyEdits(PreLoginReportDraftViewModel draft) => new(
        new InstaCompany(draft.Company.Name, draft.Company.RocName, draft.Company.RegistrationNumber, draft.Company.Category, draft.Company.Subcategory,
            draft.Company.Class, draft.Company.AuthorisedCapital, draft.Company.PaidUpCapital, draft.Company.Members, draft.Company.Incorporated,
            draft.Company.Address, draft.Company.Email, draft.Company.Listed, draft.Company.LastAgm, draft.Company.BalanceSheetDate, draft.Company.Status,
            draft.Company.ActiveCompliance, draft.Company.BooksOfAccountAddress),
        draft.Charges.Select(c => new InstaCharge(c.Id, c.Holder, c.Created, c.Modified, c.Satisfied, c.Amount, c.IsOpen, c.Srn)).ToList(),
        draft.Directors.Select(d => new InstaDirector(d.Name, d.DinOrPan, d.Designation, d.Appointed)).ToList());

    private static EditableCompanyViewModel ToEditable(InstaCompany c) => new()
    {
        Name = c.Name, RocName = c.RocName, RegistrationNumber = c.RegistrationNumber, Category = c.Category, Subcategory = c.Subcategory,
        Class = c.Class, AuthorisedCapital = c.AuthorisedCapital, PaidUpCapital = c.PaidUpCapital, Members = c.Members, Incorporated = c.Incorporated,
        Address = c.Address, Email = c.Email, Listed = c.Listed, LastAgm = c.LastAgm, BalanceSheetDate = c.BalanceSheetDate, Status = c.Status,
        ActiveCompliance = c.ActiveCompliance, BooksOfAccountAddress = c.BooksOfAccountAddress
    };

    private static string SafeFileName(string companyName, string cin)
    {
        var stripped = Regex.Replace(companyName ?? string.Empty, "[^A-Za-z0-9 ._-]+", string.Empty).Trim();
        return string.IsNullOrEmpty(stripped) ? cin : stripped;
    }
}
