using System.Globalization;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses the two EPFO sheets.
/// <para><see cref="Parse"/> reads "Annexure - EPFO Establishments" — one row per wage-month
/// contribution. Columns: WORKING STATUS, ESTABLISHMENT ID, ESTABLISHMENT NAME, WAGE MONTH, TRRN,
/// NO. OF EMPLOYEES, AMOUNT (Rs. Crore), DATE OF CREDIT, PAYMENT DUE DATE, STATUS.</para>
/// <para><see cref="ParseEstablishments"/> reads the "EPFO Establishments" summary sheet for the
/// per-establishment header the annexure omits. Header columns: WORKING STATUS, ESTABLISHMENT ID,
/// ESTABLISHMENT NAME, CITY, LATEST WAGE MONTH, LATEST DATE OF CREDIT, NO. OF EMPLOYEES, AMOUNT —
/// then unlabelled trailing columns observed as DATE OF SETUP, PRINCIPAL BUSINESS ACTIVITIES,
/// ADDRESS, EXEMPTION STATUS, with FLAGS a possible extra.</para></summary>
public static class EpfoParser
{
    private const string ParserName = nameof(EpfoParser);

    public static ParseResult<EpfoContribution> Parse(SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId)
    {
        var result = new ParseResult<EpfoContribution>();

        for (var r = 1; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            var establishmentId = Text(Cell(row, 1));
            var wageMonth = Text(Cell(row, 3));
            if (string.IsNullOrEmpty(establishmentId) || string.IsNullOrEmpty(wageMonth)) continue;

            var contribution = new EpfoContribution
            {
                RequestId = requestId,
                IngestionRunId = ingestionRunId,
                SourceDocumentId = sourceDocumentId,
                SourceSheetName = sheet.Name,
                SourceRowNumber = r + 1,
                EstablishmentId = establishmentId,
                EstablishmentName = Text(Cell(row, 2)),
                WorkingStatus = Text(Cell(row, 0)),
                WageMonth = wageMonth,
                Trrn = Text(Cell(row, 4)),
                PaymentStatus = Text(Cell(row, 9))
            };

            if (AmountNormalizer.TryParse(Cell(row, 5), out var emp, out _) && emp is not null)
                contribution.EmployeeCount = (int)emp.Value;
            if (AmountNormalizer.TryParse(Cell(row, 6), out var amount, out _))
                contribution.ContributionAmountCrore = amount;
            if (DateNormalizer.TryParse(Cell(row, 7), out var paymentDate))
                contribution.PaymentDate = paymentDate;
            if (DateNormalizer.TryParse(Cell(row, 8), out var dueDate))
                contribution.PaymentDueDate = dueDate;

            result.Items.Add(contribution);
        }

        return result;
    }

    /// <summary>The per-establishment metadata rows from the "EPFO Establishments" summary sheet — one
    /// <see cref="EpfoEstablishment"/> per establishment id (first occurrence wins on a repeat).</summary>
    public static ParseResult<EpfoEstablishment> ParseEstablishments(
        SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId)
    {
        var result = new ParseResult<EpfoEstablishment>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var r = 1; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            var establishmentId = Text(Cell(row, 1));
            if (string.IsNullOrEmpty(establishmentId)) continue;

            if (!seen.Add(establishmentId))
            {
                result.AddWarning(new ParseIssue(IssueSeverity.Warning, ParserName, "EstablishmentId",
                    establishmentId, "EPFO_ESTABLISHMENT_DUPLICATE",
                    $"Establishment id '{establishmentId}' appears more than once on the summary sheet; kept the first.", r + 1));
                continue;
            }

            var establishment = new EpfoEstablishment
            {
                RequestId = requestId,
                IngestionRunId = ingestionRunId,
                SourceDocumentId = sourceDocumentId,
                SourceSheetName = sheet.Name,
                SourceRowNumber = r + 1,
                EstablishmentId = establishmentId,
                Name = Text(Cell(row, 2)),
                City = Text(Cell(row, 3)),
                WorkingStatus = Text(Cell(row, 0)),
                LatestWageMonth = Text(Cell(row, 4)),
                // Trailing metadata columns carry no header in the observed export — read positionally.
                PrincipalBusinessActivities = Text(Cell(row, 9)),
                Address = Text(Cell(row, 10)),
                ExemptionStatus = Text(Cell(row, 11)),
                Flags = Text(Cell(row, 12)),
            };

            if (DateNormalizer.TryParse(Cell(row, 8), out var setup))
                establishment.DateOfSetup = setup;

            result.Items.Add(establishment);
        }

        return result;
    }

    private static object? Cell(IReadOnlyList<object?> row, int index) => index < row.Count ? row[index] : null;

    /// <summary>A trimmed string for a cell. A whole-number double (Excel stores a numeric TRRN as a
    /// double) renders without a decimal point or scientific notation; "-" / blank become null.</summary>
    private static string? Text(object? cell)
    {
        var s = cell switch
        {
            null => null,
            string str => str,
            double d when !double.IsNaN(d) && !double.IsInfinity(d) && d == Math.Floor(d) => ((long)d).ToString(CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => cell.ToString(),
        };
        s = s?.Trim();
        return string.IsNullOrEmpty(s) || s == "-" ? null : s;
    }
}
