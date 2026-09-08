using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses "Annexure - EPFO Establishments" — already one row per wage-month contribution, so no
/// need to also read the summary "EPFO Establishments" sheet (same fields, just latest-only).
/// Columns: WORKING STATUS, ESTABLISHMENT ID, ESTABLISHMENT NAME, WAGE MONTH, TRRN, NO. OF EMPLOYEES,
/// AMOUNT (Rs. Crore), DATE OF CREDIT, PAYMENT DUE DATE, STATUS.</summary>
public static class EpfoParser
{
    public static ParseResult<EpfoContribution> Parse(SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId)
    {
        var result = new ParseResult<EpfoContribution>();

        for (var r = 1; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            var establishmentId = row.Count > 1 ? row[1]?.ToString()?.Trim() : null;
            var wageMonth = row.Count > 3 ? row[3]?.ToString()?.Trim() : null;
            if (string.IsNullOrEmpty(establishmentId) || string.IsNullOrEmpty(wageMonth)) continue;

            var contribution = new EpfoContribution
            {
                RequestId = requestId,
                IngestionRunId = ingestionRunId,
                SourceDocumentId = sourceDocumentId,
                SourceSheetName = sheet.Name,
                SourceRowNumber = r + 1,
                EstablishmentId = establishmentId,
                EstablishmentName = row.Count > 2 ? row[2]?.ToString()?.Trim() : null,
                WorkingStatus = row.Count > 0 ? row[0]?.ToString()?.Trim() : null,
                WageMonth = wageMonth,
                PaymentStatus = row.Count > 9 ? row[9]?.ToString()?.Trim() : null
            };

            if (AmountNormalizer.TryParse(row.Count > 5 ? row[5] : null, out var emp, out _) && emp is not null)
                contribution.EmployeeCount = (int)emp.Value;
            if (AmountNormalizer.TryParse(row.Count > 6 ? row[6] : null, out var amount, out _))
                contribution.ContributionAmountCrore = amount;
            if (DateNormalizer.TryParse(row.Count > 7 ? row[7] : null, out var paymentDate))
                contribution.PaymentDate = paymentDate;
            if (DateNormalizer.TryParse(row.Count > 8 ? row[8] : null, out var dueDate))
                contribution.PaymentDueDate = dueDate;

            result.Items.Add(contribution);
        }

        return result;
    }
}
