using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Wordprocessing;

namespace MCAROC_Analysis.Services.PreLoginReports;

/// <summary>Fills the retained PRR layout without rebuilding its tables or letterhead.</summary>
internal static class PrrTemplateFiller
{
    public static void Fill(Body body, string cin, string name, InstaReportData data)
    {
        var tables = body.Elements<Table>().ToArray();
        if (tables.Length != 5)
            throw new PreLoginReportException("The selected report template is invalid.");

        var now = DateTime.Now;
        FillValues(tables[0], ["", "", cin, name,
            now.ToString("dd/MM/yyyy h:mm:ss tt", CultureInfo.InvariantCulture),
            $"{data.Charges.Count} {(data.Charges.Count == 1 ? "charge" : "charges")} found."]);
        var c = data.Company;
        FillValues(tables[1], [cin, name, c.RocName, c.RegistrationNumber, c.Category, c.Subcategory,
            c.Class, c.AuthorisedCapital, c.PaidUpCapital, c.Members, c.Incorporated, c.Address,
            c.Email, c.Listed, c.LastAgm, c.BalanceSheetDate, c.Status]);
        var openAmount = data.Charges.Where(x => x.IsOpen).Sum(x =>
            decimal.TryParse(x.Amount, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0);
        FillValues(tables[2], [data.Charges.Count.ToString(CultureInfo.InvariantCulture),
            openAmount.ToString("N0", CultureInfo.InvariantCulture)]);

        FillRows(tables[3], data.Charges.Select((charge, i) => new[] {
            (i + 1).ToString(CultureInfo.InvariantCulture), charge.Id, charge.Holder, charge.Created,
            charge.Modified, charge.Satisfied, charge.Amount }),
            ["", "", "No charges found.", "", "", "", ""]);
        FillRows(tables[4], data.Directors.Select((director, i) => new[] {
            (i + 1).ToString(CultureInfo.InvariantCulture), director.Name, director.DinOrPan,
            director.Designation, director.Appointed }),
            ["", "No director records found.", "", "", ""]);

        ReplaceParagraph(body, "Total Directors / Signatories:", $"Total Directors / Signatories: {data.Directors.Count}");
        ReplaceParagraph(body, "Report generated on", $"Report generated on {now:dd-MM-yyyy}");
    }

    private static void FillValues(Table table, string[] values)
    {
        var rows = table.Elements<TableRow>().ToArray();
        if (rows.Length != values.Length || rows.Any(r => r.Elements<TableCell>().Count() != 2))
            throw new PreLoginReportException("The selected report template is invalid.");
        for (var i = 0; i < values.Length; i++) SetCell(rows[i].Elements<TableCell>().Last(), values[i]);
    }

    private static void FillRows(Table table, IEnumerable<string[]> values, string[] emptyRow)
    {
        var rows = table.Elements<TableRow>().ToArray();
        if (rows.Length < 2 || rows[1].Elements<TableCell>().Count() != emptyRow.Length)
            throw new PreLoginReportException("The selected report template is invalid.");
        var prototype = (TableRow)rows[1].CloneNode(true);
        var headerProperties = rows[0].GetFirstChild<TableRowProperties>()
            ?? rows[0].PrependChild(new TableRowProperties());
        headerProperties.RemoveAllChildren<TableHeader>();
        headerProperties.Append(new TableHeader());
        foreach (var row in rows.Skip(1)) row.Remove();

        var dataRows = values.ToList();
        if (dataRows.Count == 0) dataRows.Add(emptyRow);
        foreach (var valuesRow in dataRows)
        {
            var row = (TableRow)prototype.CloneNode(true);
            var properties = row.GetFirstChild<TableRowProperties>() ?? row.PrependChild(new TableRowProperties());
            properties.RemoveAllChildren<CantSplit>();
            properties.Append(new CantSplit());
            var cells = row.Elements<TableCell>().ToArray();
            for (var i = 0; i < cells.Length; i++) SetCell(cells[i], valuesRow[i]);
            table.Append(row);
        }
    }

    private static void SetCell(TableCell cell, string value)
    {
        var paragraph = cell.Elements<Paragraph>().FirstOrDefault()
            ?? throw new PreLoginReportException("The selected report template is invalid.");
        foreach (var extra in cell.Elements<Paragraph>().Skip(1).ToArray()) extra.Remove();
        SetParagraph(paragraph, value);
    }

    private static void ReplaceParagraph(Body body, string prefix, string value)
    {
        var paragraph = body.Elements<Paragraph>().SingleOrDefault(p => p.InnerText.StartsWith(prefix, StringComparison.Ordinal))
            ?? throw new PreLoginReportException("The selected report template is invalid.");
        SetParagraph(paragraph, value);
    }

    private static void SetParagraph(Paragraph paragraph, string value)
    {
        var format = paragraph.Descendants<Run>().LastOrDefault(r => r.RunProperties is not null)
            ?.RunProperties?.CloneNode(true) as RunProperties;
        foreach (var child in paragraph.ChildElements.Where(x => x is not ParagraphProperties).ToArray()) child.Remove();
        var run = new Run();
        if (format is not null) run.Append(format);
        run.Append(new Text(value) { Space = SpaceProcessingModeValues.Preserve });
        paragraph.Append(run);
    }
}
