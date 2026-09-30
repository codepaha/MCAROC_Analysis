using System.Globalization;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.McaFilings;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using MCAROC_Analysis.Services;

namespace MCAROC_Analysis.Services.LitigationData;

public enum LitigationOrderAvailabilityBucket
{
    Downloaded,
    Expired,
    Failed,
    Pending
}

/// <summary>
/// Canonical resolver for order availability and disclosure text.
/// Retention Contract:
/// - RetainedUntilUtc governs the vendor API retrieval deadline.
/// - If Status == Downloaded, the PDF has been successfully stored locally and remains permanently
///   available in the portal regardless of whether RetainedUntilUtc has passed.
/// - If Status != Downloaded and RetainedUntilUtc <= asOfUtc, the vendor retrieval window has expired.
/// </summary>
public static class LitigationOrderAvailabilityResolver
{
    public static (LitigationOrderAvailabilityBucket Bucket, string Disclosure, string CsvStatus, string ExtractionLabel) Resolve(
        LitigationOrderDocument? doc,
        DateTime asOfUtc)
    {
        var extractionLabel = doc?.TextExtractionStatus?.ToString() ?? "NotAttempted";

        if (doc is null)
            return (LitigationOrderAvailabilityBucket.Pending, "Retrieval pending", "Pending", extractionLabel);

        // Downloaded file is retained locally — permanent portal availability takes precedence over vendor deadline.
        // RetainedUntilUtc is the vendor API retrieval deadline, NOT the local storage expiry.
        // We label it as "vendor deadline" to avoid implying the file disappears after that date.
        if (doc.Status == LitigationOrderDocumentStatus.Downloaded)
        {
            var deadlineStr = doc.RetainedUntilUtc > DateTime.MinValue
                ? $" (vendor deadline: {Ist.Date(doc.RetainedUntilUtc, "dd-MMM-yyyy")})"
                : string.Empty;
            var textNote = doc.TextExtractionStatus == FilingDocumentProcessingStatus.TextExtracted
                ? "; text extracted"
                : string.Empty;
            return (LitigationOrderAvailabilityBucket.Downloaded, $"Available via portal{deadlineStr}{textNote}", "Downloaded", extractionLabel);
        }

        bool isExpired = doc.Status == LitigationOrderDocumentStatus.Expired
            || (doc.RetainedUntilUtc > DateTime.MinValue && doc.RetainedUntilUtc <= asOfUtc);

        if (isExpired)
        {
            var dateStr = doc.RetainedUntilUtc > DateTime.MinValue
                ? $" on {Ist.Date(doc.RetainedUntilUtc, "dd-MMM-yyyy")}"
                : string.Empty;
            var textNote = doc.TextExtractionStatus == FilingDocumentProcessingStatus.TextExtracted
                ? "; extracted text retained in portal"
                : string.Empty;
            return (LitigationOrderAvailabilityBucket.Expired, $"Vendor PDF expired{dateStr}{textNote}", "Expired", extractionLabel);
        }

        if (doc.Status == LitigationOrderDocumentStatus.Failed)
            return (LitigationOrderAvailabilityBucket.Failed, "Retrieval failed", "Failed", extractionLabel);

        return (LitigationOrderAvailabilityBucket.Pending, "Retrieval pending", "Pending", extractionLabel);
    }
}

public sealed record StandaloneReportPropertyMatchDto(
    long LitigationCaseOrderId,
    string? OrderDate,
    string? OrderType,
    int PageNumber,
    string SourceLabel,
    string AddressText,
    AddressMatchStrength Strength,
    string? MatchedPinCode,
    IReadOnlyList<string> MatchedPlotNumbers,
    IReadOnlyList<string> MatchedLocalities,
    string Excerpt,
    bool IsCompanyPremises,
    long? RocChargeId = null,
    string? RocChargeNumber = null,
    string? ChargeHolder = null);

public sealed record StandaloneReportOrderDto(
    long LitigationCaseOrderId,
    string? OrderDate,
    string? OrderType,
    LitigationOrderAvailabilityBucket AvailabilityBucket,
    LitigationOrderDocumentStatus? DocumentStatus,
    FilingDocumentProcessingStatus? TextExtractionStatus,
    DateTime? RetainedUntilUtc,
    string AvailabilityDisclosure,
    string CsvStatus,
    string ExtractionLabel,
    IReadOnlyList<StandaloneReportPropertyMatchDto>? PropertyMatches = null);

public sealed record StandaloneReportCaseDto(
    long LitigationCaseId,
    string? ProviderCaseId,
    string? CspId,
    string? CnrNumber,
    string? CourtCategory,
    string? Direction,
    string? CaseClassification,
    string? Type,
    string? Court,
    string? Bench,
    string? CaseNumber,
    string? CaseType,
    string? CaseYear,
    string? CaseStage,
    string? CaseStatus,
    string? Act,
    string? FilingDate,
    string? LastHearingDate,
    string? NextHearingDate,
    string? DecisionDate,
    string? State,
    string? District,
    string? PetitionersJson,
    string? RespondentsJson,
    string? PetitionerAdvocatesJson,
    string? RespondentAdvocatesJson,
    IReadOnlyList<StandaloneReportOrderDto> Orders,
    IReadOnlyList<StandaloneReportPropertyMatchDto>? PropertyMatches = null);

public sealed record StandaloneLitigationReport(
    string AssignmentNumber,
    string CompanyName,
    DateTimeOffset GeneratedAtUtc,
    long AuthoritativeSnapshotId,
    DateTime AuthoritativeSnapshotRetrievedUtc,
    bool IsPriorRunDataShown,
    IReadOnlyList<string> KeywordsSearched,
    LitigationCourtSummaryGrid CourtSummaryGrid,
    IReadOnlyList<StandaloneReportCaseDto> Cases,
    LitigationPortfolioAnalysis? PortfolioAnalysis = null,
    IReadOnlyDictionary<long, LitigationCaseAnalysis>? CaseAnalysesByCaseId = null,
    long? ReusedFromSnapshotId = null,
    long? ReusedFromRequestId = null,
    long? OriginSnapshotId = null,
    ChargeLitigationSummary? ChargeLinks = null)
{
    public bool IsReused => ReusedFromSnapshotId.HasValue;

    public LitigationCaseAnalysis? AnalysisFor(long litigationCaseId) =>
        CaseAnalysesByCaseId is not null && CaseAnalysesByCaseId.TryGetValue(litigationCaseId, out var analysis)
            ? analysis
            : null;
}

/// <summary>Evidence-backed portfolio interpretation, supplied by the future analysis pipeline.</summary>
public sealed record LitigationPortfolioAnalysis(string Status, string? RiskLevel, string? Summary, IReadOnlyList<string>? KeyFindings);

/// <summary>Evidence-backed interpretation for a case. A missing entry means analysis has not run.</summary>
public sealed record LitigationCaseAnalysis(string Status, string? RiskLevel, string? Summary, IReadOnlyList<string>? KeyIssues, string? RecommendedAction, string? EvidenceNote = null);

/// <summary>Renders the standalone litigation deliverables for one MCA ROC assignment.</summary>
public static class LitigationReportArtifacts
{
    public static byte[] RenderPdf(StandaloneLitigationReport report)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        return new LitigationReportPdfDocument(report).GeneratePdf();
    }

    public static byte[] RenderCsv(StandaloneLitigationReport report)
    {
        using var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true))
        using (var csv = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture) { NewLine = "\r\n" }))
        {
            foreach (var header in CsvHeaders) csv.WriteField(header);
            csv.NextRecord();
            var serial = 0;
            foreach (var item in report.Cases)
            {
                serial++;
                WriteRow(csv, serial, report, item);
            }
        }
        return stream.ToArray();
    }

    private static readonly string[] CsvHeaders =
    [
        "Sr No", "Assignment Number", "Company", "Provider Case ID", "CSP ID", "CNR Number", "Court Category", "Direction",
        "Type", "Court", "Bench", "Case Number", "Case Type", "Case Year", "Case Stage", "Case Status", "Act", "Filing Date",
        "Last Hearing Date", "Next Hearing Date", "Decision Date", "State", "District", "Petitioners", "Respondents",
        "Petitioner Advocates", "Respondent Advocates", "Order Count", "Downloaded Order Count", "Expired Order Count",
        "Order Dates", "Order Types", "Order Availability Statuses", "Order Retained Until Dates", "Order Text Extraction Statuses",
        "Contested Property Match Count", "Contested Property Details", "Charge Link Count", "Charge Links",
        "Analysis Status", "Analysis Risk", "Analysis Summary", "Analysis Key Issues", "Analysis Recommended Action",
        "Report Reused", "Reused From Request ID", "Reused From Snapshot ID", "Report Retrieved Date", "Reuse Disclosure"
    ];

    private static void WriteRow(CsvWriter csv, int serial, StandaloneLitigationReport report, StandaloneReportCaseDto item)
    {
        var analysis = report.AnalysisFor(item.LitigationCaseId);
        var downloadedCount = item.Orders.Count(o => o.AvailabilityBucket == LitigationOrderAvailabilityBucket.Downloaded);
        var expiredCount = item.Orders.Count(o => o.AvailabilityBucket == LitigationOrderAvailabilityBucket.Expired);
        var orderDates = string.Join("; ", item.Orders.Select(o => o.OrderDate ?? "-"));
        var orderTypes = string.Join("; ", item.Orders.Select(o => o.OrderType ?? "-"));
        var orderStatuses = string.Join("; ", item.Orders.Select(o => o.CsvStatus));
        var orderRetainedUntil = string.Join("; ", item.Orders.Select(o => Ist.Date(o.RetainedUntilUtc, "yyyy-MM-dd", "-")));
        var orderExtraction = string.Join("; ", item.Orders.Select(o => o.ExtractionLabel));
        var propertyMatches = item.PropertyMatches ?? [];
        var propertyMatchCount = propertyMatches.Count.ToString(CultureInfo.InvariantCulture);
        var propertyMatchDetails = propertyMatches.Count > 0
            ? string.Join("; ", propertyMatches.Select(m => $"Order {m.OrderDate ?? "-"} (p. {m.PageNumber}): [{m.SourceLabel}] {m.AddressText}"))
            : "-";

        // Links between this case and the company's open charges (charged property, named assets, lender recovery case).
        var chargeLinks = (report.ChargeLinks?.Links ?? []).Where(l => l.Case.LitigationCaseId == item.LitigationCaseId).ToList();
        var chargeLinkCount = chargeLinks.Count.ToString(CultureInfo.InvariantCulture);
        var chargeLinkDetails = chargeLinks.Count > 0
            ? string.Join("; ", chargeLinks.Select(l => $"{l.ChargeNumber} ({l.ChargeHolder}) [{ChargeLitigationLabels.Phrase(l.Signal)}]{(l.PageNumber is { } p ? $" p. {p}" : "")}: {l.Explanation}"))
            : "-";

        var isReused = report.IsReused ? "Yes" : "No";
        var reusedReqId = report.ReusedFromRequestId?.ToString(CultureInfo.InvariantCulture) ?? "-";
        var reusedSnapId = report.ReusedFromSnapshotId?.ToString(CultureInfo.InvariantCulture) ?? "-";
        var retrievedDate = Ist.Date(report.AuthoritativeSnapshotRetrievedUtc);
        var reuseDisclosure = report.IsReused
            ? $"Report retrieved {retrievedDate} — reused from another request. Originated from Request #{report.ReusedFromRequestId} (Snapshot #{report.ReusedFromSnapshotId}), originally retrieved on {retrievedDate}. Satisfied the <= 7-day eligibility limit when reused; the 7-day bound governed reuse admission, not an ongoing freshness guarantee."
            : "-";

        foreach (var value in new[]
        {
            serial.ToString(CultureInfo.InvariantCulture), report.AssignmentNumber, report.CompanyName,
            item.ProviderCaseId, item.CspId, item.CnrNumber, item.CourtCategory, item.Direction, item.Type, item.Court, item.Bench,
            item.CaseNumber, item.CaseType, item.CaseYear, item.CaseStage, item.CaseStatus, item.Act, item.FilingDate,
            item.LastHearingDate, item.NextHearingDate, item.DecisionDate, item.State, item.District,
            PartyText(item.PetitionersJson), PartyText(item.RespondentsJson), PartyText(item.PetitionerAdvocatesJson),
            PartyText(item.RespondentAdvocatesJson), item.Orders.Count.ToString(CultureInfo.InvariantCulture),
            downloadedCount.ToString(CultureInfo.InvariantCulture), expiredCount.ToString(CultureInfo.InvariantCulture),
            orderDates, orderTypes, orderStatuses, orderRetainedUntil, orderExtraction,
            propertyMatchCount, propertyMatchDetails, chargeLinkCount, chargeLinkDetails,
            analysis?.Status ?? "Pending", analysis?.RiskLevel, analysis?.Summary,
            analysis is null ? null : string.Join("; ", analysis.KeyIssues ?? []), analysis?.RecommendedAction,
            isReused, reusedReqId, reusedSnapId, retrievedDate, reuseDisclosure
        }) csv.WriteField(SafeCsv(value));
        csv.NextRecord();
    }

    private static string SafeCsv(string? value)
    {
        var text = value ?? string.Empty;
        return text.Length > 0 && text[0] is '=' or '+' or '-' or '@' ? "'" + text : text;
    }

    internal static string PartyText(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "-";
        try
        {
            using var document = JsonDocument.Parse(json);
            var values = new List<string>();
            CollectPartyText(document.RootElement, values);
            return values.Count > 0 ? string.Join("; ", values.Distinct(StringComparer.Ordinal)) : json;
        }
        catch (JsonException) { return json; }
    }

    private static void CollectPartyText(JsonElement node, List<string> values)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.String: if (!string.IsNullOrWhiteSpace(node.GetString())) values.Add(node.GetString()!); return;
            case JsonValueKind.Array: foreach (var item in node.EnumerateArray()) CollectPartyText(item, values); return;
            case JsonValueKind.Object: foreach (var property in node.EnumerateObject()) CollectPartyText(property.Value, values); return;
        }
    }
}

internal sealed class LitigationReportPdfDocument(StandaloneLitigationReport report) : IDocument
{
    private const string Ink = "#15233D";
    private const string Navy = "#1B4F91";
    private const string BlueTint = "#F2F7FE";
    private const string Border = "#BFD1EA";
    private const string Red = "#B4232A";
    private const string Green = "#1D7A4D";
    private const string Amber = "#A66A11";

    public DocumentMetadata GetMetadata() => new() { Title = $"{report.CompanyName} - Litigation Due Diligence Report", Subject = "Standalone litigation case register and analysis" };
    public DocumentSettings GetSettings() => DocumentSettings.Default;

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.MarginHorizontal(34);
            page.MarginVertical(28);
            page.DefaultTextStyle(style => style.FontSize(8.5f).FontColor(Ink));
            page.Header().Element(ComposeHeader);
            page.Content().Column(column =>
            {
                column.Spacing(9);
                ComposeCover(column);
                if (report.Cases.Count > 0)
                {
                    column.Item().PageBreak();
                    var serial = 0;
                    foreach (var item in report.Cases)
                    {
                        serial++;
                        column.Item().EnsureSpace(180).Element(card => ComposeCaseCard(card, serial, item, report.AnalysisFor(item.LitigationCaseId)));
                    }
                }
            });
            page.Footer().Element(ComposeFooter);
        });
    }

    private void ComposeHeader(IContainer container) => container.PaddingBottom(8).BorderBottom(1.4f).BorderColor(Navy).Row(row =>
    {
        row.RelativeItem().Text("CUBICTREE  |  MCA ROC INTELLIGENCE").Bold().FontSize(8).FontColor(Navy);
        row.AutoItem().Text("LITIGATION DUE DILIGENCE").SemiBold().FontSize(8).FontColor(Colors.Grey.Darken1);
    });

    private void ComposeFooter(IContainer container) => container.PaddingTop(7).BorderTop(0.5f).BorderColor(Border).Row(row =>
    {
        row.RelativeItem().Text("Strictly Private & Confidential").FontSize(7).FontColor(Colors.Grey.Darken1);
        row.AutoItem().DefaultTextStyle(style => style.FontSize(7).FontColor(Colors.Grey.Darken1)).Text(text =>
        {
            text.Span("Page "); text.CurrentPageNumber(); text.Span(" of "); text.TotalPages();
        });
    });

    private void ComposeCover(ColumnDescriptor column)
    {
        var grid = report.CourtSummaryGrid;
        var pending = grid.TotalPendingCases;
        var disposed = grid.TotalDisposedCases;
        var orders = grid.TotalOrders;
        column.Item().PaddingTop(6).Text("Litigation Due Diligence Report").Bold().FontSize(22).FontColor(Ink);
        column.Item().PaddingTop(2).Text(report.CompanyName).SemiBold().FontSize(13).FontColor(Navy);
        column.Item().Text($"Assignment {report.AssignmentNumber}  |  Generated {Ist.Format(report.GeneratedAtUtc, "dd MMM yyyy, HH:mm")}").FontSize(8).FontColor(Colors.Grey.Darken1);

        if (report.IsReused)
        {
            column.Item().PaddingTop(6).Background("#EFF6FF").Border(1f).BorderColor("#3B82F6").Padding(8).Row(row =>
            {
                row.AutoItem().PaddingRight(8).Text("ℹ").FontSize(14).FontColor("#1D4ED8");
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text($"Report retrieved {Ist.Date(report.AuthoritativeSnapshotRetrievedUtc)} — reused from another request").Bold().FontSize(8.5f).FontColor("#1E40AF");
                    c.Item().PaddingTop(2).Text($"Data originated from Request #{report.ReusedFromRequestId} (Snapshot #{report.ReusedFromSnapshotId}), originally retrieved on {Ist.Date(report.AuthoritativeSnapshotRetrievedUtc)}. It satisfied the ≤ 7-day eligibility limit when reused; the 7-day bound governed reuse admission, not an ongoing freshness guarantee. Reused with zero new vendor spend.").FontSize(7.8f).FontColor("#1E3A8A");
                });
            });
        }

        var infoRows = new List<(string Label, string Value)>
        {
            ("SOURCE", "BPR Litigation Data Lake"),
            ("AUTHORITATIVE SNAPSHOT", $"{Ist.Format(report.AuthoritativeSnapshotRetrievedUtc, "dd MMM yyyy, HH:mm")} (ID: {report.AuthoritativeSnapshotId}){(report.IsPriorRunDataShown ? " [Prior Run Snapshot]" : string.Empty)}"),
        };
        if (report.IsReused)
        {
            infoRows.Add(("REPORT REUSE", $"Reused from Request #{report.ReusedFromRequestId} (Snapshot #{report.ReusedFromSnapshotId}); original retrieval {Ist.Date(report.AuthoritativeSnapshotRetrievedUtc)} (met ≤ 7-day admission limit)"));
        }
        infoRows.Add(("KEYWORDS SEARCHED", report.KeywordsSearched.Count == 0 ? "Not supplied by source" : string.Join(" | ", report.KeywordsSearched)));
        infoRows.Add(("REPORT SCOPE", "Standalone litigation report; separate from the MCA ROC dossier"));
        infoRows.Add(("SNAPSHOT ANCHORING", "Membership-only snapshot anchoring; case metadata reflects current persisted records."));
        column.Item().PaddingTop(10).Element(container => InformationTable(container, infoRows));
        column.Item().PaddingTop(12).Row(row =>
        {
            Metric(row.RelativeItem(), "CASES", grid.TotalCases.ToString(CultureInfo.InvariantCulture), Navy); row.ConstantItem(7);
            Metric(row.RelativeItem(), "PENDING", pending.ToString(CultureInfo.InvariantCulture), Red); row.ConstantItem(7);
            Metric(row.RelativeItem(), "DISPOSED", disposed.ToString(CultureInfo.InvariantCulture), Green); row.ConstantItem(7);
            Metric(row.RelativeItem(), "ORDERS ON RECORD", orders.ToString(CultureInfo.InvariantCulture), Amber);
        });

        // Court Summary Grid
        column.Item().PaddingTop(12).Text("COURT / TRIBUNAL SUMMARY").Bold().FontSize(8.5f).FontColor(Navy);
        if (grid.Rows.Count == 0)
        {
            column.Item().PaddingTop(4).Background("#FAFCFF").Border(0.8f).BorderColor(Border).Padding(8)
                .Text("No court proceedings found in authoritative snapshot.").FontSize(8.2f).FontColor(Colors.Grey.Darken1);
        }
        else
        {
            column.Item().PaddingTop(4).Element(c => ComposeCourtGridTable(c, grid));
        }

        if (report.ChargeLinks is { } chargeLinks)
            column.Item().PaddingTop(12).Element(container => ChargeLinksSection(container, chargeLinks));

        column.Item().PaddingTop(12).Element(container => PortfolioPanel(container, report.PortfolioAnalysis));
        column.Item().PaddingTop(10).Background("#FAFCFF").Border(0.8f).BorderColor(Border).Padding(9).Column(note =>
        {
            note.Item().Text("REPORT USE AND EVIDENCE").Bold().FontSize(8).FontColor(Navy);
            note.Item().PaddingTop(4).Text("Each following card preserves case metadata returned by the source. Case analysis is shown only where an evidence-backed analysis has been supplied. Original order files are accessed from the portal and are not embedded in this report.").FontSize(8.2f).FontColor(Colors.Grey.Darken1);
        });
    }

    private static void ComposeCourtGridTable(IContainer container, LitigationCourtSummaryGrid grid) => container.Table(table =>
    {
        table.ColumnsDefinition(columns =>
        {
            columns.RelativeColumn(3); // Court
            columns.RelativeColumn(2); // Category
            columns.RelativeColumn(1); // Total
            columns.RelativeColumn(1); // Pending
            columns.RelativeColumn(1); // Disposed
            columns.RelativeColumn(1); // Unknown
            columns.RelativeColumn(1); // Orders
        });

        table.Header(header =>
        {
            header.Cell().Background("#EAF2FD").BorderBottom(1).BorderColor(Navy).Padding(4).Text("COURT / TRIBUNAL").Bold().FontSize(7).FontColor(Navy);
            header.Cell().Background("#EAF2FD").BorderBottom(1).BorderColor(Navy).Padding(4).Text("CATEGORY").Bold().FontSize(7).FontColor(Navy);
            header.Cell().Background("#EAF2FD").BorderBottom(1).BorderColor(Navy).Padding(4).AlignRight().Text("TOTAL").Bold().FontSize(7).FontColor(Navy);
            header.Cell().Background("#EAF2FD").BorderBottom(1).BorderColor(Navy).Padding(4).AlignRight().Text("PENDING").Bold().FontSize(7).FontColor(Navy);
            header.Cell().Background("#EAF2FD").BorderBottom(1).BorderColor(Navy).Padding(4).AlignRight().Text("DISPOSED").Bold().FontSize(7).FontColor(Navy);
            header.Cell().Background("#EAF2FD").BorderBottom(1).BorderColor(Navy).Padding(4).AlignRight().Text("UNKNOWN").Bold().FontSize(7).FontColor(Navy);
            header.Cell().Background("#EAF2FD").BorderBottom(1).BorderColor(Navy).Padding(4).AlignRight().Text("ORDERS").Bold().FontSize(7).FontColor(Navy);
        });

        foreach (var row in grid.Rows)
        {
            table.Cell().BorderBottom(0.5f).BorderColor("#D7E3F2").Padding(4).Text(Display(row.CourtName)).FontSize(7.5f);
            table.Cell().BorderBottom(0.5f).BorderColor("#D7E3F2").Padding(4).Text(Humanize(row.CourtCategory)).FontSize(7.5f);
            table.Cell().BorderBottom(0.5f).BorderColor("#D7E3F2").Padding(4).AlignRight().Text(row.TotalCases.ToString(CultureInfo.InvariantCulture)).FontSize(7.5f);
            table.Cell().BorderBottom(0.5f).BorderColor("#D7E3F2").Padding(4).AlignRight().Text(row.PendingCases.ToString(CultureInfo.InvariantCulture)).FontSize(7.5f);
            table.Cell().BorderBottom(0.5f).BorderColor("#D7E3F2").Padding(4).AlignRight().Text(row.DisposedCases.ToString(CultureInfo.InvariantCulture)).FontSize(7.5f);
            table.Cell().BorderBottom(0.5f).BorderColor("#D7E3F2").Padding(4).AlignRight().Text(row.UnknownCases.ToString(CultureInfo.InvariantCulture)).FontSize(7.5f);
            table.Cell().BorderBottom(0.5f).BorderColor("#D7E3F2").Padding(4).AlignRight().Text(row.TotalOrders.ToString(CultureInfo.InvariantCulture)).FontSize(7.5f);
        }

        // Total footer row
        table.Cell().Background("#F0F4FA").BorderTop(1).BorderColor(Navy).Padding(4).Text("Total (Reconciled)").Bold().FontSize(7.5f).FontColor(Navy);
        table.Cell().Background("#F0F4FA").BorderTop(1).BorderColor(Navy).Padding(4).Text("").FontSize(7.5f);
        table.Cell().Background("#F0F4FA").BorderTop(1).BorderColor(Navy).Padding(4).AlignRight().Text(grid.TotalCases.ToString(CultureInfo.InvariantCulture)).Bold().FontSize(7.5f).FontColor(Navy);
        table.Cell().Background("#F0F4FA").BorderTop(1).BorderColor(Navy).Padding(4).AlignRight().Text(grid.TotalPendingCases.ToString(CultureInfo.InvariantCulture)).Bold().FontSize(7.5f).FontColor(Navy);
        table.Cell().Background("#F0F4FA").BorderTop(1).BorderColor(Navy).Padding(4).AlignRight().Text(grid.TotalDisposedCases.ToString(CultureInfo.InvariantCulture)).Bold().FontSize(7.5f).FontColor(Navy);
        table.Cell().Background("#F0F4FA").BorderTop(1).BorderColor(Navy).Padding(4).AlignRight().Text(grid.TotalUnknownCases.ToString(CultureInfo.InvariantCulture)).Bold().FontSize(7.5f).FontColor(Navy);
        table.Cell().Background("#F0F4FA").BorderTop(1).BorderColor(Navy).Padding(4).AlignRight().Text(grid.TotalOrders.ToString(CultureInfo.InvariantCulture)).Bold().FontSize(7.5f).FontColor(Navy);
    });

    /// <summary>The cases that touch what the company's open charges secure, or an honest statement of what was
    /// compared when there are none (never a bare "clean").</summary>
    private static void ChargeLinksSection(IContainer container, ChargeLitigationSummary summary) => container.Column(section =>
    {
        section.Item().Text("CHARGED PROPERTY & LITIGATION").Bold().FontSize(8.5f).FontColor(Navy);

        var caveats = new List<string> { "Only strong matches are shown." };
        if (summary.NcltOrdersSkipped) caveats.Add("NCLT/NCLAT orders are not scanned.");
        if (summary.OrdersWithoutText > 0) caveats.Add($"{summary.OrdersWithoutText} order(s) had no extractable text and could not be compared.");
        if (summary.MovableChargesWithoutIdentifiers > 0) caveats.Add($"{summary.MovableChargesWithoutIdentifiers} movable-asset charge(s) name no vehicle or serial numbers, so could not be compared.");

        if (!summary.HasAny)
        {
            section.Item().PaddingTop(4).Background("#FAFCFF").Border(0.8f).BorderColor(Border).Padding(8).Column(c =>
            {
                c.Item().Text("No case was tied to a charged property, a named asset or a charge holder's recovery proceeding.").FontSize(8.2f);
                c.Item().PaddingTop(2).Text($"{summary.OrdersScanned} order(s) with text were compared. Charges that name no specific property or asset cannot be compared. " + string.Join(" ", caveats.Skip(1))).FontSize(7.5f).FontColor(Colors.Grey.Darken1);
            });
            return;
        }

        section.Item().PaddingTop(4).Background("#FFF7F7").BorderLeft(4).BorderColor(Red).Border(0.8f).BorderColor("#F3C7C7").Padding(8).Column(c =>
        {
            c.Item().Text($"{summary.ChargesWithLinks} open charge(s) have litigation linked: {summary.CasesNamingAssets} case(s) name charged property or assets; {summary.CasesFor(ChargeLitigationSignal.LenderRecoveryCase)} are recovery proceedings by a charge holder.").Bold().FontSize(8.2f);
            c.Item().PaddingTop(5).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(2);   // Charge
                    columns.RelativeColumn(2);   // Link
                    columns.RelativeColumn(2.4f); // Case
                    columns.RelativeColumn(4);   // Evidence
                });
                foreach (var h in new[] { "CHARGE", "LINK", "CASE", "EVIDENCE" })
                    table.Cell().Background("#EAF2FD").BorderBottom(1).BorderColor(Navy).Padding(4).Text(h).Bold().FontSize(7).FontColor(Navy);

                foreach (var l in summary.Links)
                {
                    var asset = ChargeLitigationLabels.NamesTheAsset(l.Signal);
                    table.Cell().BorderBottom(0.5f).BorderColor("#E4D0D0").Padding(4).Column(x =>
                    {
                        x.Item().Text(Display(l.ChargeNumber)).Bold().FontSize(7.6f);
                        x.Item().Text(Display(l.ChargeHolder)).FontSize(7).FontColor(Colors.Grey.Darken1);
                    });
                    table.Cell().BorderBottom(0.5f).BorderColor("#E4D0D0").Padding(4).Text(ChargeLitigationLabels.Heading(l.Signal)).SemiBold().FontSize(7.4f).FontColor(asset ? Red : Amber);
                    table.Cell().BorderBottom(0.5f).BorderColor("#E4D0D0").Padding(4).Column(x =>
                    {
                        x.Item().Text(Display(l.Case.CaseNumber)).FontSize(7.6f);
                        x.Item().Text($"{Display(l.Case.Court)} · {l.Case.Source}").FontSize(7).FontColor(Colors.Grey.Darken1);
                    });
                    table.Cell().BorderBottom(0.5f).BorderColor("#E4D0D0").Padding(4).Column(x =>
                    {
                        x.Item().Text(l.Explanation).FontSize(7.4f);
                        if (!string.IsNullOrWhiteSpace(l.Excerpt))
                            x.Item().PaddingTop(2).Text($"“{l.Excerpt}”{(l.PageNumber is { } pg ? $" (page {pg})" : "")}").Italic().FontSize(7).FontColor(Colors.Grey.Darken2);
                    });
                }
            });
            c.Item().PaddingTop(4).Text(string.Join(" ", caveats)).FontSize(7).FontColor(Colors.Grey.Darken1);
        });
    });

    private static void Metric(IContainer container, string label, string value, string color) => container.Background(BlueTint).Border(0.8f).BorderColor(Border).Padding(8).Column(card =>
    {
        card.Item().Text(label).Bold().FontSize(7).FontColor(color);
        card.Item().PaddingTop(3).Text(value).Bold().FontSize(18).FontColor(Ink);
    });

    private static void PortfolioPanel(IContainer container, LitigationPortfolioAnalysis? analysis) => container.Background("#F7FAFE").BorderLeft(4).BorderColor(analysis is null ? Navy : RiskColor(analysis.RiskLevel)).Border(0.8f).BorderColor(Border).Padding(10).Column(panel =>
    {
        panel.Item().Row(row => { row.RelativeItem().Text("PORTFOLIO ANALYSIS").Bold().FontSize(8.5f).FontColor(Navy); row.AutoItem().Element(c => Pill(c, analysis?.RiskLevel ?? "ANALYSIS PENDING", analysis is null ? Navy : RiskColor(analysis.RiskLevel))); });
        panel.Item().PaddingTop(5).Text(analysis?.Summary ?? "Portfolio analysis will be generated after the relevant case metadata and retained order text have been assessed.").FontSize(8.5f);
        if (analysis?.KeyFindings is { Count: > 0 }) foreach (var finding in analysis.KeyFindings) panel.Item().PaddingTop(2).Text(text => { text.Span("• ").FontColor(Navy); text.Span(finding); });
    });

    private static void ComposeCaseCard(IContainer container, int serial, StandaloneReportCaseDto item, LitigationCaseAnalysis? analysis) => container.Border(0.9f).BorderColor(Border).Column(card =>
    {
        var bucket = LitigationCaseStatusClassifier.Classify(item.CaseStatus, item.CaseStage);
        var accent = bucket switch
        {
            LitigationCaseStatusBucket.Pending => Red,
            LitigationCaseStatusBucket.Disposed => Green,
            _ => Amber
        };
        var statusBadgeText = bucket switch
        {
            LitigationCaseStatusBucket.Pending => "PENDING",
            LitigationCaseStatusBucket.Disposed => "DISPOSED",
            _ => !string.IsNullOrWhiteSpace(item.CaseStatus) ? item.CaseStatus.ToUpperInvariant() : "UNKNOWN"
        };

        card.Item().Background(BlueTint).BorderLeft(4).BorderColor(accent).Padding(9).Row(row =>
        {
            row.RelativeItem().Column(title =>
            {
                title.Item().Text($"CASE {serial:000}").Bold().FontSize(7.5f).FontColor(Navy);
                title.Item().PaddingTop(2).Text(Display(item.CaseNumber)).Bold().FontSize(14).FontColor(Ink);
                title.Item().PaddingTop(1).Text(Display(item.Court)).FontSize(8.5f).FontColor(Colors.Grey.Darken1);
            });
            row.AutoItem().Column(badges =>
            {
                badges.Item().AlignRight().Element(c => Pill(c, statusBadgeText, accent));
                badges.Item().PaddingTop(4).AlignRight().Text(item.Orders.Count == 1 ? "1 ORDER ON RECORD" : $"{item.Orders.Count} ORDERS ON RECORD").FontSize(7).FontColor(Amber);
            });
        });
        card.Item().Padding(9).Column(body =>
        {
            body.Item().Text("CASE PARTICULARS").Bold().FontSize(8).FontColor(Navy);
            body.Item().PaddingTop(5).Element(c => CaseDetailsGrid(c, item));
            body.Item().PaddingTop(9).Element(c => PartiesPanel(c, item));
            if (item.PropertyMatches is { Count: > 0 })
                body.Item().PaddingTop(9).Element(c => PropertyMatchesPanel(c, item.PropertyMatches));
            body.Item().PaddingTop(9).Element(c => AnalysisPanel(c, analysis));
            body.Item().PaddingTop(9).Element(c => OrdersPanel(c, item.Orders));
        });
    });

    private static void PropertyMatchesPanel(IContainer container, IReadOnlyList<StandaloneReportPropertyMatchDto> matches) => container.Background("#FFFDF5").BorderLeft(4).BorderColor(Amber).Border(0.8f).BorderColor("#FFE8A3").Padding(8).Column(panel =>
    {
        panel.Item().Row(row =>
        {
            row.RelativeItem().Text("PREMISES & PROPERTY ADDRESS MENTIONS").Bold().FontSize(8.5f).FontColor(Amber);
            row.AutoItem().Element(c => Pill(c, "TEXT OVERLAP (UNVERIFIED)", Amber));
        });
        panel.Item().PaddingTop(3).Text("Extracted court order text matches known company premises or mortgaged charge property. Note: Text overlap in party cause titles or service memos does not legally establish that the property is the contested subject of litigation without analyst verification:").FontSize(7.8f).FontColor(Colors.Grey.Darken2);
        foreach (var match in matches)
        {
            panel.Item().PaddingTop(4).Column(col =>
            {
                col.Item().Text(text =>
                {
                    text.Span($"• Order {Display(match.OrderDate)} (p. {match.PageNumber}): ").Bold().FontSize(7.8f).FontColor(Navy);
                    text.Span($"[{match.SourceLabel}] ").Bold().FontSize(7.8f).FontColor(match.IsCompanyPremises ? Amber : Navy);
                    text.Span(match.AddressText).FontSize(7.8f);
                });
                if (!string.IsNullOrWhiteSpace(match.Excerpt))
                {
                    col.Item().PaddingTop(2).PaddingLeft(8).Text($"\"{match.Excerpt}\"").Italic().FontSize(7.2f).FontColor(Colors.Grey.Darken1);
                }
            });
        }
    });

    private static void CaseDetailsGrid(IContainer container, StandaloneReportCaseDto item) => container.Table(table =>
    {
        table.ColumnsDefinition(columns => { columns.RelativeColumn(); columns.RelativeColumn(); });
        var fields = new (string Label, string Value)[]
        {
            ("CNR NO", Display(item.CnrNumber)), ("CSP ID", Display(item.CspId)), ("COURT CATEGORY", Humanize(item.CourtCategory)), ("TYPE", Humanize(item.Type)),
            ("DIRECTION", Humanize(item.Direction)), ("BENCH", Display(item.Bench)), ("CASE TYPE / YEAR", JoinValues(item.CaseType, item.CaseYear)),
            ("CASE STAGE", Display(item.CaseStage)), ("FILING DATE", Display(item.FilingDate)), ("LAST HEARING", Display(item.LastHearingDate)),
            ("NEXT HEARING", Display(item.NextHearingDate)), ("DECISION DATE", Display(item.DecisionDate)), ("STATE / DISTRICT", JoinValues(item.State, item.District)), ("ACT", Display(item.Act))
        };
        foreach (var field in fields) table.Cell().BorderBottom(0.5f).BorderColor("#D7E3F2").PaddingVertical(3).PaddingRight(7).Text(text => { text.Span(field.Label + "  ").Bold().FontSize(6.8f).FontColor(Navy); text.Span(field.Value).FontSize(8.1f); });
    });

    private static void PartiesPanel(IContainer container, StandaloneReportCaseDto item) => container.Background("#FBFDFF").Border(0.7f).BorderColor(Border).Padding(8).Column(panel =>
    {
        panel.Item().Text("PARTIES & REPRESENTATION").Bold().FontSize(8).FontColor(Navy);
        panel.Item().PaddingTop(5).Row(row => { PartyColumn(row.RelativeItem(), "PETITIONER", LitigationReportArtifacts.PartyText(item.PetitionersJson), LitigationReportArtifacts.PartyText(item.PetitionerAdvocatesJson)); row.ConstantItem(10); PartyColumn(row.RelativeItem(), "RESPONDENT", LitigationReportArtifacts.PartyText(item.RespondentsJson), LitigationReportArtifacts.PartyText(item.RespondentAdvocatesJson)); });
    });

    private static void PartyColumn(IContainer container, string role, string parties, string advocates) => container.Column(col =>
    {
        col.Item().Text(role).Bold().FontSize(6.8f).FontColor(Navy);
        col.Item().PaddingTop(2).Text(parties).FontSize(8);
        if (advocates != "-") col.Item().PaddingTop(3).Text(text => { text.Span("Advocate: ").SemiBold().FontSize(7.2f).FontColor(Colors.Grey.Darken1); text.Span(advocates).FontSize(7.5f); });
    });

    private static void AnalysisPanel(IContainer container, LitigationCaseAnalysis? analysis) => container.Background("#F5F8FD").Border(0.8f).BorderColor(Border).Padding(8).Column(panel =>
    {
        panel.Item().Row(row => { row.RelativeItem().Text("CASE ANALYSIS").Bold().FontSize(8.5f).FontColor(Navy); row.AutoItem().Element(c => Pill(c, analysis?.RiskLevel ?? "PENDING", analysis is null ? Navy : RiskColor(analysis.RiskLevel))); });
        panel.Item().PaddingTop(5).Text(analysis?.Summary ?? "Analysis pending. It will be generated only after extracted order text and case metadata are available for this case.").FontSize(8.2f);
        if (analysis?.KeyIssues is { Count: > 0 }) { panel.Item().PaddingTop(5).Text("KEY ISSUES").Bold().FontSize(7).FontColor(Navy); foreach (var issue in analysis.KeyIssues) panel.Item().PaddingTop(2).Text(text => { text.Span("• ").FontColor(Navy); text.Span(issue); }); }
        if (!string.IsNullOrWhiteSpace(analysis?.RecommendedAction)) panel.Item().PaddingTop(5).Text(text => { text.Span("RECOMMENDED ACTION  ").Bold().FontSize(7).FontColor(Green); text.Span(analysis.RecommendedAction).FontSize(8); });
        if (!string.IsNullOrWhiteSpace(analysis?.EvidenceNote)) panel.Item().PaddingTop(4).Text(analysis.EvidenceNote).Italic().FontSize(7.2f).FontColor(Colors.Grey.Darken1);
    });

    private static void OrdersPanel(IContainer container, IReadOnlyList<StandaloneReportOrderDto> orders) => container.Column(panel =>
    {
        panel.Item().Text("ORDERS & JUDGMENTS").Bold().FontSize(8).FontColor(Navy);
        if (orders.Count == 0) { panel.Item().PaddingTop(4).Text("No order record was returned for this case.").FontSize(8).FontColor(Colors.Grey.Darken1); return; }
        foreach (var order in orders)
        {
            panel.Item().PaddingTop(4).Background("#FFFCF4").BorderLeft(3).BorderColor(Amber).Padding(5).Column(entry =>
            {
                entry.Item().Text(text =>
                {
                    text.Span(Display(order.OrderDate)).Bold().FontColor(Amber);
                    text.Span("  |  ");
                    text.Span(Display(order.OrderType));
                    text.Span("  |  ");
                    text.Span(order.AvailabilityDisclosure).FontColor(Colors.Grey.Darken1);
                });
                if (order.PropertyMatches is { Count: > 0 })
                {
                    entry.Item().PaddingTop(2).Text(text =>
                    {
                        text.Span("Address Overlap: ").Bold().FontSize(7.2f).FontColor(Amber);
                        text.Span(string.Join("; ", order.PropertyMatches.Select(m => $"[{m.SourceLabel}] {m.AddressText} (p. {m.PageNumber})"))).FontSize(7.2f).FontColor(Colors.Grey.Darken2);
                    });
                }
            });
        }
    });

    private static void InformationTable(IContainer container, IEnumerable<(string Label, string Value)> values) => container.Table(table =>
    {
        table.ColumnsDefinition(columns => { columns.ConstantColumn(145); columns.RelativeColumn(); });
        foreach (var (label, value) in values) { table.Cell().Background("#EAF2FD").Border(0.5f).BorderColor(Border).Padding(6).Text(label).Bold().FontSize(7.5f).FontColor(Navy); table.Cell().Border(0.5f).BorderColor(Border).Padding(6).Text(value).FontSize(8.4f); }
    });

    private static void Pill(IContainer container, string text, string color) => container.Background("#FFFFFF").Border(0.8f).BorderColor(color).CornerRadius(7).PaddingVertical(3).PaddingHorizontal(7).Text(text).Bold().FontSize(6.8f).FontColor(color);
    private static string RiskColor(string? risk) => risk?.StartsWith("R1", StringComparison.OrdinalIgnoreCase) == true ? Green : risk?.StartsWith("R2", StringComparison.OrdinalIgnoreCase) == true ? Amber : risk?.StartsWith("R3", StringComparison.OrdinalIgnoreCase) == true ? Red : Navy;
    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;
    private static string JoinValues(string? first, string? second) => string.Join(" / ", new[] { first, second }.Where(value => !string.IsNullOrWhiteSpace(value)));
    private static string Humanize(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value.Replace('_', ' ').ToUpperInvariant();
}
