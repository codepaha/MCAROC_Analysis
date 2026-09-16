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
        if (c.IsPartnership)
        {
            ReplaceCellLabel(companyDetails, "CIN", "PAN / Registration Number");
            ReplaceCellLabel(companyDetails, "Company Name", "Partnership Name");
        }

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
        StyleChargesTable(chargesTable);
        // Charges.Count == 0: leave the placeholder row untouched — it already renders correctly (matches
        // the template's own sample company, which also had zero charges).

        if (data.LegalCases is not null)
        {
            var legalCasesTable = FindTableAfterHeading(body, "Legal Cases")
                ?? throw new PreLoginReportException("The selected report template is invalid.");
            var legalCasesRow = legalCasesTable.Elements<TableRow>().Skip(1).FirstOrDefault()
                ?? throw new PreLoginReportException("The selected report template is invalid.");
            var values = new[]
            {
                data.LegalCases.SupremeCourt, data.LegalCases.HighCourt, data.LegalCases.DistrictCourt,
                data.LegalCases.ConsumerForum, data.LegalCases.ItatTax, data.LegalCases.NcltNclat,
                data.LegalCases.DrtDrat, data.LegalCases.Rera, data.LegalCases.NgtOthers
            };
            var cells = legalCasesRow.Elements<TableCell>().ToList();
            if (cells.Count != values.Length) throw new PreLoginReportException("The selected report template is invalid.");
            for (var i = 0; i < values.Length; i++) SetCellText(cells[i], values[i].ToString(CultureInfo.InvariantCulture));
        }

        var legalCaseDetailsTable = FindTableAfterHeading(body, "Details of legal cases")
            ?? throw new PreLoginReportException("The selected report template is invalid.");
        if (data.LegalCases?.Cases is { Count: > 0 } cases)
        {
            var templateRows = legalCaseDetailsTable.Elements<TableRow>().ToList();
            if (templateRows.Count != 9) throw new PreLoginReportException("The selected report template is invalid.");
            var prototypeCells = templateRows[0].Elements<TableCell>().ToList();
            if (prototypeCells.Count != 2) throw new PreLoginReportException("The selected report template is invalid.");
            foreach (var row in templateRows) row.Remove();

            foreach (var (legalCase, index) in cases.Select((lc, i) => (lc, i)))
            {
                var fields = new (string Label, string Value)[]
                {
                    ("Court", legalCase.Court),
                    ("Sr No", (index + 1).ToString(CultureInfo.InvariantCulture)),
                    ("Case no", legalCase.CaseNo),
                    ("Case Type", legalCase.CaseType),
                    ("Case Year", legalCase.CaseYear),
                    ("Case Stage", legalCase.CaseStage),
                    ("Act", legalCase.Act),
                    ("Date of Filing", legalCase.DateOfFiling),
                    ("State", legalCase.State),
                    ("District", legalCase.District),
                    ("Case details", legalCase.CaseDetails),
                    // LDOH has no reliable source data (see LegalCaseFileParser), so it remains "-".
                    ("Last Date of Hearing", "-"),
                    ("Next Date of Hearing", legalCase.DateOfHearing),
                    ("Status", legalCase.Status)
                };

                foreach (var (field, fieldIndex) in fields.Select((field, fieldIndex) => (field, fieldIndex)))
                    legalCaseDetailsTable.Append(CreateLegalCaseFieldRow(
                        prototypeCells[0], prototypeCells[1], field.Label, field.Value,
                        isFirst: fieldIndex == 0, isLast: fieldIndex == fields.Length - 1));

                legalCaseDetailsTable.Append(CreateLegalCaseSpacerRow(prototypeCells[0], prototypeCells[1]));
            }
            StyleLegalCaseTable(legalCaseDetailsTable);
        }
        // No cases attached: leave the placeholder card's "No litigation cases on file." text untouched,
        // matching the same zero-row convention as the MCA-ROC charges table above.

        StyleDetailHeading(body, "Details of MCA-ROC CHARGES");
        StyleDetailHeading(body, "Details of legal cases");

        ReplacePreparedOnDateField(body);
        ReserveLetterheadSpaceForDisclaimer(body);
    }

    // The default letterhead artwork is page-relative, square-wrapped content. Its lower edge reaches below
    // the normal 720-twip body margin, so a Disclaimer beginning at the top of a page collides with it.
    // Word can suppress spacing at the top of a page, and PageBreakBefore can introduce a blank page when
    // pagination already breaks there. A dedicated final section reliably preserves space below the letterhead.
    private static void ReserveLetterheadSpaceForDisclaimer(Body body)
    {
        var paragraphs = body.Elements<Paragraph>().ToList();
        var disclaimerIndex = paragraphs.FindIndex(p => GetElementText(p) == "Disclaimer");
        if (disclaimerIndex <= 0) throw new PreLoginReportException("The selected report template is invalid.");

        var disclaimer = paragraphs[disclaimerIndex];
        var properties = disclaimer.GetFirstChild<ParagraphProperties>() ?? disclaimer.PrependChild(new ParagraphProperties());
        properties.RemoveAllChildren<PageBreakBefore>();
        if (properties.GetFirstChild<SpacingBetweenLines>() is { } spacing)
            spacing.Before = null;

        // The case cards are a table, not body paragraphs. The section marker must therefore remain in the
        // final spacer immediately after that table; attaching it to the last non-empty body paragraph puts
        // the cards and Disclaimer in the same final section. Keep exactly one marker spacer and remove the
        // earlier redundant spacers, which were responsible for the intervening blank page.
        var precedingParagraph = paragraphs[disclaimerIndex - 1];
        for (var i = disclaimerIndex - 2; i >= 0 && string.IsNullOrWhiteSpace(GetElementText(paragraphs[i])); i--)
            paragraphs[i].Remove();
        var finalSection = body.Elements<SectionProperties>().SingleOrDefault()
            ?? throw new PreLoginReportException("The selected report template is invalid.");
        if (precedingParagraph.ParagraphProperties?.GetFirstChild<SectionProperties>() is null)
        {
            var precedingProperties = precedingParagraph.GetFirstChild<ParagraphProperties>()
                ?? precedingParagraph.PrependChild(new ParagraphProperties());
            precedingProperties.AppendChild(new KeepNext());
            var firstSection = (SectionProperties)finalSection.CloneNode(true);
            firstSection.InsertBefore(new SectionType { Val = SectionMarkValues.NextPage }, firstSection.GetFirstChild<PageSize>());
            precedingProperties.AppendChild(firstSection);
        }

        // The Disclaimer is the first page in its new section but must use the normal default letterhead,
        // rather than the report's first-page header.
        finalSection.RemoveAllChildren<TitlePage>();
        var margins = finalSection.GetFirstChild<PageMargin>() ?? finalSection.AppendChild(new PageMargin());
        margins.Top = 1440; // Clears page-relative letterhead artwork that ends at about 1120 twips.
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

    private static void ReplaceCellLabel(Table table, string oldLabel, string newLabel)
    {
        var row = table.Elements<TableRow>().FirstOrDefault(r => GetElementText(r.Elements<TableCell>().First()) == oldLabel);
        if (row is not null) SetCellText(row.Elements<TableCell>().First(), newLabel);
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

    // Sets only a "Label : value" cell's trailing value run, leaving the leading bold label run (and its
    // "Label : " text) untouched — unlike SetCellText, which would wipe the label along with the value.
    private static void SetCellValueRun(TableCell cell, string text)
    {
        var run = cell.Descendants<Run>().LastOrDefault() ?? throw new PreLoginReportException("The selected report template is invalid.");
        var textElement = run.GetFirstChild<Text>() ?? throw new PreLoginReportException("The selected report template is invalid.");
        textElement.Text = text;
        textElement.Space = SpaceProcessingModeValues.Preserve;
    }

    private static void StyleChargesTable(Table table)
    {
        SetTableBorders(table, showHorizontalSeparators: true);
        var rows = table.Elements<TableRow>().ToList();
        foreach (var (row, index) in rows.Select((row, index) => (row, index)))
        {
            var rowProperties = row.GetFirstChild<TableRowProperties>() ?? row.PrependChild(new TableRowProperties());
            if (index == 0 && rowProperties.GetFirstChild<TableHeader>() is null)
                rowProperties.AppendChild(new TableHeader());
            if (rowProperties.GetFirstChild<CantSplit>() is null)
                rowProperties.AppendChild(new CantSplit());

            var fill = index == 0 ? "D9EAF7" : index % 2 == 0 ? "EDF4FB" : "FFFFFF";
            foreach (var cell in row.Elements<TableCell>())
                SetCellVisualStyle(cell, fill, showBottomBorder: true, fontSize: index == 0 ? "18" : "19");
        }
    }

    private static TableRow CreateLegalCaseFieldRow(
        TableCell labelPrototype, TableCell valuePrototype, string label, string value, bool isFirst, bool isLast)
    {
        var row = new TableRow(new TableRowProperties(new CantSplit()));
        var labelCell = (TableCell)labelPrototype.CloneNode(true);
        var valueCell = (TableCell)valuePrototype.CloneNode(true);
        SetCellText(labelCell, $"{label} : ");
        SetCellText(valueCell, value);
        StyleLegalCaseCell(labelCell, bold: true, width: "1900", isFirst, isLast);
        StyleLegalCaseCell(valueCell, bold: false, width: "6900", isFirst, isLast);

        foreach (var paragraph in new[] { labelCell, valueCell }.SelectMany(c => c.Elements<Paragraph>()))
        {
            var properties = paragraph.GetFirstChild<ParagraphProperties>() ?? paragraph.PrependChild(new ParagraphProperties());
            properties.SpacingBetweenLines = new SpacingBetweenLines
                { Before = "0", After = "0", Line = "240", LineRule = LineSpacingRuleValues.Auto };
            properties.RemoveAllChildren<KeepNext>();
            if (!isLast) properties.AppendChild(new KeepNext());
        }

        row.Append(labelCell, valueCell);
        return row;
    }

    private static TableRow CreateLegalCaseSpacerRow(TableCell labelPrototype, TableCell valuePrototype)
    {
        var row = CreateLegalCaseFieldRow(labelPrototype, valuePrototype, string.Empty, string.Empty, isFirst: false, isLast: true);
        row.TableRowProperties!.RemoveAllChildren<TableRowHeight>();
        row.TableRowProperties.AppendChild(new TableRowHeight { Val = 180U, HeightType = HeightRuleValues.Exact });
        foreach (var cell in row.Elements<TableCell>())
            cell.TableCellProperties!.TableCellBorders = new TableCellBorders(
                new TopBorder { Val = BorderValues.Nil }, new LeftBorder { Val = BorderValues.Nil },
                new BottomBorder { Val = BorderValues.Nil }, new RightBorder { Val = BorderValues.Nil });
        return row;
    }

    private static void StyleLegalCaseTable(Table table)
    {
        SetTableBorders(table, showHorizontalSeparators: false);
        var grid = table.GetFirstChild<TableGrid>() ?? table.InsertAfter(new TableGrid(), table.GetFirstChild<TableProperties>());
        grid.RemoveAllChildren<GridColumn>();
        grid.Append(new GridColumn { Width = "1900" }, new GridColumn { Width = "6900" });
    }

    private static void StyleLegalCaseCell(TableCell cell, bool bold, string width, bool isFirst, bool isLast)
    {
        var properties = cell.GetFirstChild<TableCellProperties>() ?? cell.PrependChild(new TableCellProperties());
        properties.TableCellWidth = new TableCellWidth { Type = TableWidthUnitValues.Dxa, Width = width };
        properties.Shading = new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = "FFFFFF" };
        properties.TableCellBorders = new TableCellBorders(
            new TopBorder { Val = isFirst ? BorderValues.Single : BorderValues.Nil, Color = "7F8C8D", Size = 6U },
            new LeftBorder { Val = BorderValues.Nil },
            new BottomBorder { Val = isLast ? BorderValues.Single : BorderValues.Nil, Color = "7F8C8D", Size = 6U },
            new RightBorder { Val = BorderValues.Nil });
        foreach (var run in cell.Descendants<Run>())
        {
            var runProperties = run.GetFirstChild<RunProperties>() ?? run.PrependChild(new RunProperties());
            runProperties.Bold = bold ? new Bold() : null;
            runProperties.FontSize = new FontSize { Val = "19" };
            runProperties.FontSizeComplexScript = new FontSizeComplexScript { Val = "19" };
            runProperties.Color = new Color { Val = "000000" };
        }
    }

    private static void SetTableBorders(Table table, bool showHorizontalSeparators)
    {
        var properties = table.GetFirstChild<TableProperties>() ?? table.PrependChild(new TableProperties());
        properties.RemoveAllChildren<TableBorders>();
        var horizontal = showHorizontalSeparators
            ? new InsideHorizontalBorder { Val = BorderValues.Single, Color = "B4C7E7", Size = 4U }
            : new InsideHorizontalBorder { Val = BorderValues.Nil };
        properties.AppendChild(new TableBorders(
            new TopBorder { Val = BorderValues.Nil },
            new LeftBorder { Val = BorderValues.Nil },
            new BottomBorder { Val = BorderValues.Nil },
            new RightBorder { Val = BorderValues.Nil },
            horizontal,
            new InsideVerticalBorder { Val = BorderValues.Nil }));
    }

    private static void SetCellVisualStyle(TableCell cell, string fill, bool showBottomBorder, string fontSize)
    {
        var properties = cell.GetFirstChild<TableCellProperties>() ?? cell.PrependChild(new TableCellProperties());
        properties.RemoveAllChildren<Shading>();
        properties.AppendChild(new Shading { Val = ShadingPatternValues.Clear, Color = "auto", Fill = fill });
        properties.RemoveAllChildren<TableCellBorders>();
        properties.AppendChild(new TableCellBorders(
            new TopBorder { Val = BorderValues.Nil },
            new LeftBorder { Val = BorderValues.Nil },
            showBottomBorder
                ? new BottomBorder { Val = BorderValues.Single, Color = "B4C7E7", Size = 4U }
                : new BottomBorder { Val = BorderValues.Nil },
            new RightBorder { Val = BorderValues.Nil }));
        foreach (var run in cell.Descendants<Run>())
        {
            var runProperties = run.GetFirstChild<RunProperties>() ?? run.PrependChild(new RunProperties());
            runProperties.FontSize = new FontSize { Val = fontSize };
            runProperties.FontSizeComplexScript = new FontSizeComplexScript { Val = fontSize };
            runProperties.Color = new Color { Val = "1F2937" };
        }
    }

    private static void StyleDetailHeading(Body body, string headingText)
    {
        var heading = body.Elements<Paragraph>().FirstOrDefault(p => GetElementText(p) == headingText);
        if (heading is null) return;
        foreach (var run in heading.Elements<Run>())
        {
            var properties = run.GetFirstChild<RunProperties>() ?? run.PrependChild(new RunProperties());
            properties.Color = new Color { Val = "2E75B6" };
        }
    }

    public static PreLoginReportDraftViewModel ToDraft(long jobId, Guid batchId, string cin, PreLoginReportFormat format, InstaReportData data) => new()
    {
        JobId = jobId, BatchId = batchId, Cin = cin, Format = format, Company = ToEditable(data.Company),
        IsPartnership = data.Company.IsPartnership,
        LegalCases = data.LegalCases is null ? null : ToEditable(data.LegalCases),
        AttachedLegalCaseCount = data.LegalCases?.Cases?.Count,
        Charges = data.Charges.Select(c => new EditableChargeViewModel { Srn = c.Srn, Id = c.Id, Holder = c.Holder, Created = c.Created, Modified = c.Modified, Satisfied = c.Satisfied, Amount = c.Amount, IsOpen = c.IsOpen }).ToList(),
        Directors = data.Directors.Select(d => new EditableDirectorViewModel { Name = d.Name, DinOrPan = d.DinOrPan, Designation = d.Designation, Appointed = d.Appointed }).ToList()
    };

    public static InstaReportData ApplyEdits(PreLoginReportDraftViewModel draft) => new(
        new InstaCompany(draft.Company.Name, draft.Company.RocName, draft.Company.RegistrationNumber, draft.Company.Category, draft.Company.Subcategory,
            draft.Company.Class, draft.Company.AuthorisedCapital, draft.Company.PaidUpCapital, draft.Company.Members, draft.Company.Incorporated,
            draft.Company.Address, draft.Company.Email, draft.Company.Listed, draft.Company.LastAgm, draft.Company.BalanceSheetDate, draft.Company.Status,
            draft.Company.ActiveCompliance, draft.Company.BooksOfAccountAddress),
        draft.Charges.Select(c => new InstaCharge(c.Id, c.Holder, c.Created, c.Modified, c.Satisfied, c.Amount, c.IsOpen, c.Srn)).ToList(),
        draft.Directors.Select(d => new InstaDirector(d.Name, d.DinOrPan, d.Designation, d.Appointed)).ToList(),
        draft.LegalCases is null ? null : new InstaLegalCases(draft.LegalCases.SupremeCourt, draft.LegalCases.HighCourt,
            draft.LegalCases.DistrictCourt, draft.LegalCases.ConsumerForum, draft.LegalCases.ItatTax, draft.LegalCases.NcltNclat,
            draft.LegalCases.DrtDrat, draft.LegalCases.Rera, draft.LegalCases.NgtOthers));

    private static EditableCompanyViewModel ToEditable(InstaCompany c) => new()
    {
        Name = c.Name, RocName = c.RocName, RegistrationNumber = c.RegistrationNumber, Category = c.Category, Subcategory = c.Subcategory,
        Class = c.Class, AuthorisedCapital = c.AuthorisedCapital, PaidUpCapital = c.PaidUpCapital, Members = c.Members, Incorporated = c.Incorporated,
        Address = c.Address, Email = c.Email, Listed = c.Listed, LastAgm = c.LastAgm, BalanceSheetDate = c.BalanceSheetDate, Status = c.Status,
        ActiveCompliance = c.ActiveCompliance, BooksOfAccountAddress = c.BooksOfAccountAddress
    };

    private static EditableLegalCasesViewModel ToEditable(InstaLegalCases cases) => new()
    {
        SupremeCourt = cases.SupremeCourt, HighCourt = cases.HighCourt, DistrictCourt = cases.DistrictCourt,
        ConsumerForum = cases.ConsumerForum, ItatTax = cases.ItatTax, NcltNclat = cases.NcltNclat,
        DrtDrat = cases.DrtDrat, Rera = cases.Rera, NgtOthers = cases.NgtOthers
    };

    private static string SafeFileName(string companyName, string cin)
    {
        var stripped = Regex.Replace(companyName ?? string.Empty, "[^A-Za-z0-9 ._-]+", string.Empty).Trim();
        return string.IsNullOrEmpty(stripped) ? cin : stripped;
    }
}
