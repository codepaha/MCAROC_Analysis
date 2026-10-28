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
using MCAROC_Analysis.Services.LitigationModule.Freshness;

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

        if (doc.Status == LitigationOrderDocumentStatus.NotPublished)
            return (LitigationOrderAvailabilityBucket.Failed, "Not published at the source", "NotPublished", extractionLabel);

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
    IReadOnlyList<StandaloneReportPropertyMatchDto>? PropertyMatches = null,
    IReadOnlyList<LitigationOrderOutcome>? Outcomes = null,
    decimal? FineAmount = null,
    ClassificationConfidence? Confidence = null,
    bool EvidenceTruncated = false);

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
    ChargeLitigationSummary? ChargeLinks = null,
    LitigationDataCurrencySummary? DataCurrency = null)
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
public sealed record LitigationCaseAnalysis(
    string Status, string? RiskLevel, string? Summary, IReadOnlyList<string>? KeyIssues, string? RecommendedAction, string? EvidenceNote = null,
    string? RiskTrigger = null, string? RiskReason = null, string? BaselineRisk = null, bool RiskRaised = false,
    bool ActiveRestraint = false, string? RestraintType = null, bool LisPendens = false,
    string? Consequence = null, string? Outcome = null, string? Subject = null, string? Amount = null,
    IReadOnlyList<string>? Provisions = null, IReadOnlyList<LitigationAnalysisQuote>? Passages = null, IReadOnlyList<string>? KeyPoints = null,
    string? AnalysisBasis = null)
{
    /// <summary>True for the detailed analysis (tier, trigger, quoted passages); false for a summary-only one.</summary>
    public bool IsDetailed => RiskTrigger is not null || Passages is { Count: > 0 };

    /// <summary>The analysis read no order text: its basis, as the analysis checks set it from the text actually supplied
    /// (an expired PDF's retained text still counts), is "Metadata-Led".</summary>
    public bool IsMetadataLed => string.Equals(AnalysisBasis, "Metadata-Led", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Renders the standalone litigation deliverables for one MCA ROC assignment.</summary>
public static class LitigationReportArtifacts
{
    public static byte[] RenderPdf(StandaloneLitigationReport report, byte[]? logo = null)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        return new LitigationReportPdfDocument(report, logo).GeneratePdf();
    }

    /// <summary>The Cubictree mark used in the dossier's running header; null when the file is missing, in which case the
    /// header falls back to a text monogram.</summary>
    public static byte[]? TryLoadLogo(string webRootPath)
    {
        try
        {
            var path = Path.Combine(webRootPath, "images", "favicon.png");
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (IOException) { return null; }
    }

    /// <summary>One case as the report presents it: which section and forum it belongs to, whose side the company is on
    /// and how old it is.</summary>
    internal sealed record CaseView(
        StandaloneReportCaseDto Item, LitigationCaseStatusBucket Bucket, LitigationInstitution Institution,
        LitigationCompanySide Side, LitigationCaseAge Age);

    /// <summary>Every case, in report order: pending, then disposed, then status not reported; within a section by forum
    /// seniority, cases against the company before ones it brought, newest filing first.</summary>
    internal static IReadOnlyList<CaseView> BuildCaseViews(StandaloneLitigationReport report)
    {
        // Age is measured to the day the data was retrieved (IST), as on the case page, so the two always agree.
        var asOf = DateOnly.FromDateTime(Ist.FromUtc(report.AuthoritativeSnapshotRetrievedUtc));
        var targets = report.KeywordsSearched.Append(report.CompanyName).ToList();
        return report.Cases
            .Select(item =>
            {
                var bucket = LitigationCaseStatusClassifier.Classify(item.CaseStatus, item.CaseStage);
                return new CaseView(
                    item, bucket, LitigationCasePresentation.Resolve(item.Type, item.Court, item.CourtCategory),
                    LitigationCasePresentation.Side(item.Direction, item.PetitionersJson, item.RespondentsJson, targets),
                    LitigationCaseAges.Compute(item.FilingDate, item.DecisionDate, item.CaseYear, bucket,
                        item.Orders.Select(o => o.OrderDate), asOf, item.CnrNumber, item.CaseNumber));
            })
            .OrderBy(v => LitigationCasePresentation.SortKey(v.Bucket, v.Institution, v.Side, v.Item.FilingDate, v.Item.LitigationCaseId))
            .ToList();
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
            foreach (var view in BuildCaseViews(report))
            {
                serial++;
                WriteRow(csv, serial, report, view);
            }
        }
        return stream.ToArray();
    }

    private static readonly string[] CsvHeaders =
    [
        "Sr No", "Assignment Number", "Company", "Provider Case ID", "CNR Number", "Court Category", "Direction", "Forum", "Case Age",
        "Type", "Court", "Bench", "Case Number", "Case Type", "Case Year", "Case Stage", "Case Status", "Act", "Filing Date",
        "Last Hearing Date", "Next Hearing Date", "Decision Date", "State", "District", "Petitioners", "Respondents",
        "Petitioner Advocates", "Respondent Advocates", "Order Count", "Downloaded Order Count", "Expired Order Count",
        "Order Dates", "Order Types", "Order Availability Statuses", "Order Retained Until Dates", "Order Text Extraction Statuses",
        "Contested Property Match Count", "Contested Property Details", "Charge Link Count", "Charge Links",
        "Analysis Status", "Analysis Risk", "Analysis Summary", "Analysis Key Issues", "Analysis Recommended Action",
        "Report Reused", "Reused From Request ID", "Reused From Snapshot ID", "Report Retrieved Date", "Reuse Disclosure",
        "Analysis Trigger", "Analysis Baseline Risk", "Analysis Restraint", "Analysis Outcome", "Analysis Passages", "Analysis Key Points",
        "Data Currency As Of", "Refreshed Cases Count", "Unrefreshed Cases Count", "Data Currency Note"
    ];

    private static void WriteRow(CsvWriter csv, int serial, StandaloneLitigationReport report, CaseView view)
    {
        var item = view.Item;
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
            item.ProviderCaseId, item.CnrNumber, item.CourtCategory, view.Side switch { LitigationCompanySide.ByCompany => "By", LitigationCompanySide.AgainstCompany => "Against", LitigationCompanySide.Both => "Both sides", _ => "Not determined" },
            LitigationCasePresentation.Label(view.Institution), view.Age.Headline, item.Type, item.Court, item.Bench,
            item.CaseNumber, item.CaseType, item.CaseYear, item.CaseStage, item.CaseStatus, item.Act, item.FilingDate,
            item.LastHearingDate, item.NextHearingDate, item.DecisionDate, item.State, item.District,
            PartyText(item.PetitionersJson), PartyText(item.RespondentsJson), PartyText(item.PetitionerAdvocatesJson),
            PartyText(item.RespondentAdvocatesJson), item.Orders.Count.ToString(CultureInfo.InvariantCulture),
            downloadedCount.ToString(CultureInfo.InvariantCulture), expiredCount.ToString(CultureInfo.InvariantCulture),
            orderDates, orderTypes, orderStatuses, orderRetainedUntil, orderExtraction,
            propertyMatchCount, propertyMatchDetails, chargeLinkCount, chargeLinkDetails,
            analysis?.Status ?? "Pending", analysis?.RiskLevel, analysis?.Summary,
            analysis is null ? null : string.Join("; ", analysis.KeyIssues ?? []), analysis?.RecommendedAction,
            isReused, reusedReqId, reusedSnapId, retrievedDate, reuseDisclosure,
            analysis?.RiskTrigger, analysis?.BaselineRisk,
            string.Join("; ", new[]
            {
                analysis is { ActiveRestraint: true } ? "Restraint in force" + (string.IsNullOrWhiteSpace(analysis.RestraintType) || analysis.RestraintType == "NONE" ? "" : " (" + analysis.RestraintType + ")") : null,
                analysis is { LisPendens: true } ? "Lis pendens" : null
            }.Where(x => x is not null)) is { Length: > 0 } flags ? flags : null,
            analysis?.Outcome,
            analysis?.Passages is { Count: > 0 } ps ? string.Join(" | ", ps.Select(q => $"{q.Claim}: \"{q.Quote}\" (order #{q.OrderId}, p. {q.Page})")) : null,
            analysis?.KeyPoints is { Count: > 0 } kp ? string.Join("; ", kp) : null,
            report.DataCurrency is { AsOfUtc: not null } dcAsOf ? dcAsOf.AsOfIst : Ist.Format(report.AuthoritativeSnapshotRetrievedUtc, "dd-MMM-yyyy, HH:mm"),
            report.DataCurrency?.RefreshedCasesCount.ToString(CultureInfo.InvariantCulture) ?? "0",
            report.DataCurrency?.UnrefreshedCasesCount.ToString(CultureInfo.InvariantCulture) ?? report.Cases.Count.ToString(CultureInfo.InvariantCulture),
            report.DataCurrency is { InProgressNote.Length: > 0 } dcProg ? $"{dcProg.InProgressNote} {dcProg.CurrencyNote}" : (report.DataCurrency?.CurrencyNote ?? "Unrefreshed cases show the last known values.")
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

internal sealed class LitigationReportPdfDocument(StandaloneLitigationReport report, byte[]? logo = null) : IDocument
{
    private readonly IReadOnlyList<LitigationReportArtifacts.CaseView> views = LitigationReportArtifacts.BuildCaseViews(report);

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
                // #554: every property the orders name, before the cases, charged first.
                if (report.ChargeLinks?.PropertyLitigation is { ShouldShow: true } property)
                {
                    column.Item().PageBreak();
                    ComposePropertyLitigation(column, property);
                }
                var serial = 0;
                foreach (var section in new[] { LitigationCaseStatusBucket.Pending, LitigationCaseStatusBucket.Disposed, LitigationCaseStatusBucket.Unknown })
                {
                    var inSection = views.Where(v => v.Bucket == section).ToList();
                    if (inSection.Count == 0) continue;
                    column.Item().PageBreak();
                    column.Item().Element(c => SectionHeading(c, section, inSection.Count));
                    LitigationInstitution? current = null;
                    foreach (var view in inSection)
                    {
                        var number = ++serial;
                        var startsForum = current != view.Institution;
                        current = view.Institution;
                        var forumCount = inSection.Count(v => v.Institution == view.Institution);
                        // The forum heading is part of its first card's item, so the two can never be split across pages.
                        column.Item().EnsureSpace(180).Column(entry =>
                        {
                            if (startsForum)
                                entry.Item().PaddingTop(4).PaddingBottom(4).Text($"{LitigationCasePresentation.Label(view.Institution).ToUpperInvariant()}  ·  {forumCount}")
                                    .Bold().FontSize(8.5f).FontColor(Navy);
                            entry.Item().Element(card => ComposeCaseCard(card, number, view, report.AnalysisFor(view.Item.LitigationCaseId)));
                        });
                    }
                }
            });
            page.Footer().Element(ComposeFooter);
        });
    }

    private void ComposeHeader(IContainer container) => container.PaddingBottom(8).BorderBottom(1.4f).BorderColor(Navy).Row(row =>
    {
        // Same mark as the dossier's running header, on every page; a text monogram stands in if the file is missing.
        row.ConstantItem(20).Element(c =>
        {
            if (logo is { Length: > 0 }) c.AlignMiddle().Image(logo);
            else c.AlignMiddle().Width(14).Height(14).Background(Navy).AlignCenter().AlignMiddle().Text("CT").Bold().FontSize(6).FontColor("#FFFFFF");
        });
        row.ConstantItem(8);
        row.RelativeItem().AlignMiddle().Text("CUBICTREE  |  MCA ROC INTELLIGENCE").Bold().FontSize(8).FontColor(Navy);
        row.AutoItem().AlignMiddle().Text("LITIGATION DUE DILIGENCE").SemiBold().FontSize(8).FontColor(Colors.Grey.Darken1);
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
        // Counted from the same list the summary table and case sections use, so the cover can never disagree with them.
        var pending = views.Count(v => v.Bucket == LitigationCaseStatusBucket.Pending);
        var disposed = views.Count(v => v.Bucket == LitigationCaseStatusBucket.Disposed);
        var orders = views.Sum(v => v.Item.Orders.Count);
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

        var dataCurrency = report.DataCurrency is { AsOfUtc: null } dcNull
            ? dcNull with
            {
                AsOfUtc = report.AuthoritativeSnapshotRetrievedUtc,
                AsOfIst = Ist.Format(report.AuthoritativeSnapshotRetrievedUtc, "dd-MMM-yyyy, HH:mm")
            }
            : report.DataCurrency ?? new LitigationDataCurrencySummary(
                report.AuthoritativeSnapshotRetrievedUtc,
                Ist.Format(report.AuthoritativeSnapshotRetrievedUtc, "dd-MMM-yyyy, HH:mm"),
                report.Cases.Count,
                0,
                report.Cases.Count);

        column.Item().PaddingTop(6).Background("#F8FAFC").Border(0.8f).BorderColor("#CBD5E1").Padding(8).Column(c =>
        {
            c.Item().Row(r =>
            {
                r.AutoItem().Text("DATA CURRENCY").Bold().FontSize(8.5f).FontColor(Navy);
                r.RelativeItem().AlignRight().Text($"As of {dataCurrency.AsOfIst}").SemiBold().FontSize(8f).FontColor(Ink);
            });
            c.Item().PaddingTop(3).Text(text =>
            {
                text.Span($"{dataCurrency.RefreshedCasesCount} of {dataCurrency.TotalCases} case(s) refreshed from court portals · {dataCurrency.UnrefreshedCasesCount} not updated. ").SemiBold().FontSize(8f).FontColor(Ink);
                if (!string.IsNullOrWhiteSpace(dataCurrency.InProgressNote))
                {
                    text.Span(dataCurrency.InProgressNote + " ").SemiBold().FontSize(7.8f).FontColor(Amber);
                }
                text.Span(dataCurrency.CurrencyNote).Italic().FontSize(7.8f).FontColor(Colors.Grey.Darken2);
            });
        });

        var infoRows = new List<(string Label, string Value)>
        {
            ("SOURCE", "Litigation data service"),
            ("DATA CURRENCY", $"{dataCurrency.RefreshedCasesCount} of {dataCurrency.TotalCases} case(s) refreshed · {dataCurrency.UnrefreshedCasesCount} not updated as of {dataCurrency.AsOfIst}{(string.IsNullOrWhiteSpace(dataCurrency.InProgressNote) ? string.Empty : $" · {dataCurrency.InProgressNote}")}"),
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
            Metric(row.RelativeItem(), "CASES", views.Count.ToString(CultureInfo.InvariantCulture), Navy); row.ConstantItem(7);
            Metric(row.RelativeItem(), "PENDING", pending.ToString(CultureInfo.InvariantCulture), Red); row.ConstantItem(7);
            Metric(row.RelativeItem(), "DISPOSED", disposed.ToString(CultureInfo.InvariantCulture), Green); row.ConstantItem(7);
            Metric(row.RelativeItem(), "ORDERS ON RECORD", orders.ToString(CultureInfo.InvariantCulture), Amber);
        });

        column.Item().PaddingTop(12).Text(text =>
        {
            text.Span("SUMMARY: FORUM WISE").Bold().FontSize(8.5f).FontColor(Navy);
            text.Span("     each figure is total (").FontSize(7).FontColor(Colors.Grey.Darken1);
            text.Span("pending").FontSize(7).FontColor(Red);
            text.Span(" / ").FontSize(7).FontColor(Colors.Grey.Darken1);
            text.Span("disposed").FontSize(7).FontColor(Green);
            text.Span(" / status unknown)").FontSize(7).FontColor(Colors.Grey.Darken1);
        });
        if (views.Count == 0)
        {
            column.Item().PaddingTop(4).Background("#FAFCFF").Border(0.8f).BorderColor(Border).Padding(8)
                .Text("No court proceedings found in authoritative snapshot.").FontSize(8.2f).FontColor(Colors.Grey.Darken1);
        }
        else
        {
            column.Item().PaddingTop(4).Element(ComposeForumSummary);
        }

        column.Item().PaddingTop(12).Element(container => PortfolioPanel(container, report.PortfolioAnalysis, views.Count));
        column.Item().PaddingTop(10).Background("#FAFCFF").Border(0.8f).BorderColor(Border).Padding(9).Column(note =>
        {
            note.Item().Text("REPORT USE AND EVIDENCE").Bold().FontSize(8).FontColor(Navy);
            note.Item().PaddingTop(4).Text("Each following card preserves case metadata returned by the source. Case analysis is shown only where an evidence-backed analysis has been supplied. Original order files are accessed from the portal and are not embedded in this report.").FontSize(8.2f).FontColor(Colors.Grey.Darken1);
        });
    }

    /// <summary>One row per forum, zero rows included so a forum with nothing found reads as "searched, none". Cases against
    /// the company and cases it brought each show total (pending / disposed / unknown).</summary>
    private static void ComposePropertyLitigation(ColumnDescriptor column, MCAROC_Analysis.Services.PropertyParticulars.PropertyLitigationSummary summary)
    {
        column.Item().Text("PROPERTY IN LITIGATION").Bold().FontSize(11).FontColor(Navy);
        column.Item().Text("Every specific property the court orders name, read from the orders and compared with the properties the open charges secure. " +
            "Party and office addresses in case titles are not counted.").FontSize(7.5f).FontColor(Colors.Grey.Darken2);

        foreach (var (title, note, entries) in new[]
        {
            ("Charged property in litigation", "The order names the property an open charge secures.", summary.ChargedProperty),
            ("Units of charged projects", "Another unit or part of a project or building an open charge secures.", summary.InChargedProject),
            ("Not matched to an open charge", summary.NotMatchedNote(), summary.NotMatchedToCharge)
        })
        {
            if (entries.Count == 0) continue;
            column.Item().PaddingTop(6).Text($"{title.ToUpperInvariant()}  ·  {entries.Count}").Bold().FontSize(8.5f).FontColor(Navy);
            column.Item().Text(note).FontSize(7f).FontColor(Colors.Grey.Darken1);
            var withCharge = entries.Any(e => e.Ties.Count > 0);
            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(3.4f); columns.RelativeColumn(2); columns.RelativeColumn(1.8f);
                    if (withCharge) columns.RelativeColumn(1.2f);
                });
                IContainer Head(IContainer c) => c.Background("#EAF2FD").BorderBottom(1).BorderColor(Navy).Padding(4);
                IContainer Cell(IContainer c) => c.BorderBottom(0.5f).BorderColor("#D7E3F2").Padding(4);
                table.Header(header =>
                {
                    header.Cell().Element(Head).Text("PROPERTY").Bold().FontSize(7).FontColor(Navy);
                    header.Cell().Element(Head).Text("WHAT THE ORDERS SAY").Bold().FontSize(7).FontColor(Navy);
                    header.Cell().Element(Head).Text("CASES").Bold().FontSize(7).FontColor(Navy);
                    if (withCharge) header.Cell().Element(Head).Text("CHARGE").Bold().FontSize(7).FontColor(Navy);
                });
                foreach (var e in entries)
                {
                    var relation = MCAROC_Analysis.Services.PropertyParticulars.OrderPropertyLabels.Relation(e.Relation);
                    var issue = MCAROC_Analysis.Services.PropertyParticulars.OrderPropertyLabels.Issue(e.Issue);
                    table.Cell().Element(Cell).Text(e.Description + (relation is null ? "" : " — " + relation)).FontSize(7.5f);
                    table.Cell().Element(Cell).Text(t =>
                    {
                        t.Span(MCAROC_Analysis.Services.PropertyParticulars.OrderPropertyLabels.Role(e.Role) + (issue is null ? "" : ": " + issue)).FontSize(7.5f).Bold();
                        if (e.Directions.Count > 0)
                            t.Line("").FontSize(4);
                        if (e.Directions.Count > 0)
                            t.Span(string.Join(", ", e.Directions.Select(MCAROC_Analysis.Services.PropertyParticulars.OrderPropertyLabels.Direction))).FontSize(7f);
                    });
                    table.Cell().Element(Cell).Text(string.Join("\n", e.Cases.Select(c => c.CaseNumber ?? $"Case #{c.LitigationCaseId}"))).FontSize(7f);
                    if (withCharge) table.Cell().Element(Cell).Text(string.Join("\n", e.Ties.Select(t => t.Charge.ChargeNumber ?? $"Charge {t.Charge.ChargeId}"))).FontSize(7f);
                }
            });
        }

        var notes = new List<string>();
        if (!summary.HasAny)
            notes.Add(summary.OrdersPending > 0
                ? "No property in issue has been read from the orders yet."
                : "The orders name no property in issue (in dispute, under a lender's security or attached).");
        if (summary.UnidentifiedSecurityCases.Count > 0)
            notes.Add($"{summary.UnidentifiedSecurityCases.Count} case(s) act on secured or attached property the orders describe only by reference (for example \"the mortgaged properties described in the schedule\").");
        if (summary.MentionedOnly > 0) notes.Add($"{summary.MentionedOnly} more propert{(summary.MentionedOnly == 1 ? "y is" : "ies are")} named only in passing.");
        if (summary.OrdersPending > 0) notes.Add($"{summary.OrdersPending} order(s) were still being read when this report was made.");
        foreach (var n in notes) column.Item().Text(n).FontSize(7f).FontColor(Colors.Grey.Darken1);
    }

    private void ComposeForumSummary(IContainer container)
    {
        // "Both sides" is a known result, kept apart from "not determined" (the side could not be told); each column only when used.
        var showBoth = views.Any(v => v.Side == LitigationCompanySide.Both);
        var showUnspecified = views.Any(v => v.Side == LitigationCompanySide.Unknown);
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(2.2f); columns.RelativeColumn(1); columns.RelativeColumn(2); columns.RelativeColumn(2);
                if (showBoth) columns.RelativeColumn(2);
                if (showUnspecified) columns.RelativeColumn(2);
            });
            IContainer Head(IContainer c) => c.Background("#EAF2FD").BorderBottom(1).BorderColor(Navy).Padding(4);
            table.Header(header =>
            {
                header.Cell().Element(Head).Text("FORUM").Bold().FontSize(7).FontColor(Navy);
                header.Cell().Element(Head).AlignRight().Text("TOTAL").Bold().FontSize(7).FontColor(Navy);
                header.Cell().Element(Head).AlignRight().Text("AGAINST COMPANY").Bold().FontSize(7).FontColor(Navy);
                header.Cell().Element(Head).AlignRight().Text("BY COMPANY").Bold().FontSize(7).FontColor(Navy);
                if (showBoth) header.Cell().Element(Head).AlignRight().Text("BOTH SIDES").Bold().FontSize(7).FontColor(Navy);
                if (showUnspecified) header.Cell().Element(Head).AlignRight().Text("NOT DETERMINED").Bold().FontSize(7).FontColor(Navy);
            });

            void Row(string label, IReadOnlyList<LitigationReportArtifacts.CaseView> rows, bool total)
            {
                IContainer Cell(IContainer c) => total
                    ? c.Background("#F0F4FA").BorderTop(1).BorderColor(Navy).Padding(4)
                    : c.BorderBottom(0.5f).BorderColor("#D7E3F2").Padding(4);
                table.Cell().Element(Cell).Text(t => { var span = t.Span(label).FontSize(7.5f); if (total) span.Bold(); });
                table.Cell().Element(Cell).AlignRight().Text(t => { var span = t.Span(rows.Count.ToString(CultureInfo.InvariantCulture)).FontSize(7.5f); if (total) span.Bold(); });
                Breakdown(table.Cell().Element(Cell), rows.Where(v => v.Side == LitigationCompanySide.AgainstCompany).ToList());
                Breakdown(table.Cell().Element(Cell), rows.Where(v => v.Side == LitigationCompanySide.ByCompany).ToList());
                if (showBoth) Breakdown(table.Cell().Element(Cell), rows.Where(v => v.Side == LitigationCompanySide.Both).ToList());
                if (showUnspecified) Breakdown(table.Cell().Element(Cell), rows.Where(v => v.Side == LitigationCompanySide.Unknown).ToList());
            }

            foreach (var institution in Enum.GetValues<LitigationInstitution>())
                Row(LitigationCasePresentation.Label(institution), views.Where(v => v.Institution == institution).ToList(), false);
            Row("Total", views, true);
        });
    }

    /// <summary>"12 (3/8/1)": cases, then pending in red, disposed in green and status unknown in grey.</summary>
    private static void Breakdown(IContainer container, IReadOnlyList<LitigationReportArtifacts.CaseView> rows) =>
        container.AlignRight().Text(text =>
        {
            text.DefaultTextStyle(style => style.FontSize(7.5f));
            int Count(LitigationCaseStatusBucket b) => rows.Count(v => v.Bucket == b);
            text.Span(rows.Count.ToString(CultureInfo.InvariantCulture)).Bold();
            text.Span(" (");
            text.Span(Count(LitigationCaseStatusBucket.Pending).ToString(CultureInfo.InvariantCulture)).FontColor(Red);
            text.Span("/");
            text.Span(Count(LitigationCaseStatusBucket.Disposed).ToString(CultureInfo.InvariantCulture)).FontColor(Green);
            text.Span("/");
            text.Span(Count(LitigationCaseStatusBucket.Unknown).ToString(CultureInfo.InvariantCulture)).FontColor(Colors.Grey.Darken1);
            text.Span(")");
        });

    private static void SectionHeading(IContainer container, LitigationCaseStatusBucket section, int count)
    {
        var (title, color) = section switch
        {
            LitigationCaseStatusBucket.Pending => ("PENDING CASES", Red),
            LitigationCaseStatusBucket.Disposed => ("DISPOSED CASES", Green),
            _ => ("CASES WITH STATUS NOT REPORTED", Amber)
        };
        container.BorderBottom(1.2f).BorderColor(color).PaddingBottom(3).Text(text =>
        {
            text.Span(title).Bold().FontSize(11).FontColor(color);
            text.Span($"   {count}").Bold().FontSize(11).FontColor(Ink);
        });
    }

    private static void Metric(IContainer container, string label, string value, string color) => container.Background(BlueTint).Border(0.8f).BorderColor(Border).Padding(8).Column(card =>
    {
        card.Item().Text(label).Bold().FontSize(7).FontColor(color);
        card.Item().PaddingTop(3).Text(value).Bold().FontSize(18).FontColor(Ink);
    });

    /// <param name="caseCount">With no cases there is nothing to analyse: the panel says so rather than "pending", which would read as unfinished work.</param>
    private static void PortfolioPanel(IContainer container, LitigationPortfolioAnalysis? analysis, int caseCount) => container.Background("#F7FAFE").BorderLeft(4).BorderColor(analysis is null ? Navy : RiskColor(analysis.RiskLevel)).Border(0.8f).BorderColor(Border).Padding(10).Column(panel =>
    {
        var noCases = analysis is null && caseCount == 0;
        panel.Item().Row(row => { row.RelativeItem().Text("PORTFOLIO ANALYSIS").Bold().FontSize(8.5f).FontColor(Navy); row.AutoItem().Element(c => Pill(c, analysis?.RiskLevel ?? (noCases ? "NOT APPLICABLE" : "ANALYSIS PENDING"), analysis is null ? Navy : RiskColor(analysis.RiskLevel))); });
        panel.Item().PaddingTop(5).Text(analysis?.Summary ?? (noCases
            ? "No court proceedings were found for the keywords searched, so there are no cases to analyse."
            : "Portfolio analysis will be generated after the relevant case metadata and retained order text have been assessed.")).FontSize(8.5f);
        if (analysis?.KeyFindings is { Count: > 0 }) foreach (var finding in analysis.KeyFindings) panel.Item().PaddingTop(2).Text(text => { text.Span("• ").FontColor(Navy); text.Span(finding); });
    });

    private static void ComposeCaseCard(IContainer container, int serial, LitigationReportArtifacts.CaseView view, LitigationCaseAnalysis? analysis) => container.Border(0.9f).BorderColor(Border).Column(card =>
    {
        var item = view.Item;
        var bucket = view.Bucket;
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
                title.Item().PaddingTop(2).Text(Display(LitigationCasePresentation.CaseTitle(item.CaseNumber, item.CaseType, item.CaseYear))).Bold().FontSize(14).FontColor(Ink);
                title.Item().PaddingTop(1).Text($"{LitigationCasePresentation.Label(view.Institution)}  ·  {Display(item.Court)}").FontSize(8.5f).FontColor(Colors.Grey.Darken1);
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
            body.Item().PaddingTop(5).Element(c => CaseDetailsGrid(c, view));
            body.Item().PaddingTop(9).Element(c => PartiesPanel(c, item));
            if (item.PropertyMatches is { Count: > 0 })
                body.Item().PaddingTop(9).Element(c => PropertyMatchesPanel(c, item.PropertyMatches));
            body.Item().PaddingTop(9).Element(c => AnalysisPanel(c, analysis, item.Orders));
            body.Item().PaddingTop(9).Element(c => OrdersPanel(c, item.Orders, analysis?.IsMetadataLed == true));
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

    private static void CaseDetailsGrid(IContainer container, LitigationReportArtifacts.CaseView view) => container.Table(table =>
    {
        var item = view.Item;
        table.ColumnsDefinition(columns => { columns.RelativeColumn(); columns.RelativeColumn(); });
        var fields = new (string Label, string Value)[]
        {
            ("CNR NO", Display(item.CnrNumber)), ("CASE AGE", view.Age.Headline), ("FORUM", LitigationCasePresentation.Label(view.Institution)), ("TYPE", Humanize(item.Type)),
            ("DIRECTION", LitigationCasePresentation.SideLabel(view.Side)), ("BENCH", Display(item.Bench)), ("CASE TYPE / YEAR", JoinValues(item.CaseType, item.CaseYear)),
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

    private static void AnalysisPanel(IContainer container, LitigationCaseAnalysis? analysis, IReadOnlyList<StandaloneReportOrderDto> orders) => container.Background("#F5F8FD").Border(0.8f).BorderColor(Border).Padding(8).Column(panel =>
    {
        panel.Item().Row(row => { row.RelativeItem().Text("CASE ANALYSIS").Bold().FontSize(8.5f).FontColor(Navy); row.AutoItem().Element(c => Pill(c, analysis?.RiskLevel ?? "PENDING", analysis is null ? Navy : RiskColor(analysis.RiskLevel))); });
        if (analysis is { IsDetailed: true })
        {
            if (analysis.RiskRaised && !string.IsNullOrWhiteSpace(analysis.BaselineRisk))
                panel.Item().PaddingTop(3).Text($"Raised from {analysis.BaselineRisk} on the orders").FontSize(7.2f).FontColor(Colors.Grey.Darken1);
            if (!string.IsNullOrWhiteSpace(analysis.RiskTrigger))
                panel.Item().PaddingTop(4).Text(text => { text.Span(analysis.RiskTrigger).Bold().FontSize(8.2f); if (!string.IsNullOrWhiteSpace(analysis.RiskReason)) text.Span(" - " + analysis.RiskReason).FontSize(8.2f); });
            if (analysis.ActiveRestraint || analysis.LisPendens)
                panel.Item().PaddingTop(3).Text(string.Join("  |  ", new[]
                {
                    analysis.ActiveRestraint ? "RESTRAINT IN FORCE" + (string.IsNullOrWhiteSpace(analysis.RestraintType) || analysis.RestraintType == "NONE" ? "" : " (" + Humanize(analysis.RestraintType) + ")") : null,
                    analysis.LisPendens ? "LIS PENDENS" : null
                }.Where(x => x is not null))).Bold().FontSize(7.4f).FontColor(Red);
        }
        panel.Item().PaddingTop(5).Text(analysis?.Summary ?? "Analysis pending. It will be generated only after extracted order text and case metadata are available for this case.").FontSize(8.2f).SemiBold();
        if (analysis?.KeyPoints is { Count: > 0 })
            foreach (var point in analysis.KeyPoints) panel.Item().PaddingTop(2).Text(text => { text.Span("\u2022 ").FontColor(Navy); text.Span(point).FontSize(8.2f); });
        if (analysis is { IsDetailed: true })
        {
            if (!string.IsNullOrWhiteSpace(analysis.Outcome)) panel.Item().PaddingTop(4).Text(text => { text.Span("OUTCOME  ").Bold().FontSize(7).FontColor(Navy); text.Span(analysis.Outcome).FontSize(8); });
            if (!string.IsNullOrWhiteSpace(analysis.Consequence)) panel.Item().PaddingTop(4).Text(text => { text.Span("IF DECIDED AGAINST THE COMPANY  ").Bold().FontSize(7).FontColor(Navy); text.Span(analysis.Consequence).FontSize(8); });
            if (analysis.Provisions is { Count: > 0 }) panel.Item().PaddingTop(4).Text(text => { text.Span("PROVISIONS  ").Bold().FontSize(7).FontColor(Navy); text.Span(string.Join("; ", analysis.Provisions)).FontSize(7.6f); });
            if (analysis.Passages is { Count: > 0 })
            {
                panel.Item().PaddingTop(5).Text("PASSAGES THIS RESTS ON").Bold().FontSize(7).FontColor(Navy);
                foreach (var passage in analysis.Passages)
                    panel.Item().PaddingTop(2).BorderLeft(2).BorderColor(Border).PaddingLeft(5).Text(text =>
                    {
                        text.Span(PassageClaim(passage.Claim) + ": ").SemiBold().FontSize(7.2f).FontColor(Colors.Grey.Darken1);
                        text.Span("\u201C" + passage.Quote + "\u201D").FontSize(7.6f);
                        text.Span($"  (order #{passage.OrderId}, p. {passage.Page})").FontSize(7).FontColor(Colors.Grey.Darken1);
                    });
            }
        }
        if (EvidenceLimitations(analysis?.KeyIssues, orders, analysis?.IsMetadataLed == true) is { Count: > 0 } limitations)
        {
            panel.Item().PaddingTop(5).Text("EVIDENCE LIMITATIONS").Bold().FontSize(7).FontColor(Navy);
            foreach (var limitation in limitations) panel.Item().PaddingTop(2).Text(text => { text.Span("\u2022 ").FontColor(Navy); text.Span(limitation).FontSize(8); });
        }
        if (!string.IsNullOrWhiteSpace(analysis?.RecommendedAction)) panel.Item().PaddingTop(5).Text(text => { text.Span("RECOMMENDED ACTION  ").Bold().FontSize(7).FontColor(Green); text.Span(analysis.RecommendedAction).FontSize(8); });
        if (!string.IsNullOrWhiteSpace(analysis?.EvidenceNote)) panel.Item().PaddingTop(4).Text(analysis.EvidenceNote).Italic().FontSize(7.2f).FontColor(Colors.Grey.Darken1);
    });

    private static void OrdersPanel(IContainer container, IReadOnlyList<StandaloneReportOrderDto> orders, bool metadataLed) => container.Column(panel =>
    {
        panel.Item().EnsureSpace(80).Text("ORDERS & JUDGMENTS").Bold().FontSize(8).FontColor(Navy);
        if (orders.Count == 0) { panel.Item().PaddingTop(4).Text("No order record was returned for this case." + (metadataLed ? RestsOnRecord : "")).FontSize(8).FontColor(Colors.Grey.Darken1); return; }
        var available = orders.Count(o => o.AvailabilityBucket == LitigationOrderAvailabilityBucket.Downloaded);
        if (available < orders.Count)
            // Neutral wording: whether a document failed, expired or is still pending is a pipeline detail the reader cannot act on.
            panel.Item().PaddingTop(2).Text(OrderAvailabilityNote(available, orders.Count) + (available == 0 && metadataLed ? RestsOnRecord : "")).FontSize(7.2f).FontColor(Colors.Grey.Darken1);
        foreach (var order in orders)
        {
            panel.Item().PaddingTop(4).Background("#FFFCF4").BorderLeft(3).BorderColor(Amber).Padding(5).Column(entry =>
            {
                entry.Item().Text(text =>
                {
                    text.Span(Display(order.OrderDate)).Bold().FontColor(Amber);
                    text.Span("  |  ");
                    text.Span(Display(order.OrderType));
                    if (order.AvailabilityBucket != LitigationOrderAvailabilityBucket.Downloaded)
                        text.Span("  ·  document not available").FontSize(7.2f).FontColor(Colors.Grey.Darken1);
                });
                if (order.PropertyMatches is { Count: > 0 })
                {
                    entry.Item().PaddingTop(2).Text(text =>
                    {
                        text.Span("Address Overlap: ").Bold().FontSize(7.2f).FontColor(Amber);
                        text.Span(string.Join("; ", order.PropertyMatches.Select(m => $"[{m.SourceLabel}] {m.AddressText} (p. {m.PageNumber})"))).FontSize(7.2f).FontColor(Colors.Grey.Darken2);
                    });
                }
                if (order.Outcomes is { Count: > 0 })
                {
                    entry.Item().PaddingTop(2).Text(text =>
                    {
                        text.Span("Outcomes: ").Bold().FontSize(7.2f).FontColor(Navy);
                        var outcomeStrs = order.Outcomes.Select(LitigationOrderOutcomeLabels.Format).ToList();
                        if (order.FineAmount.HasValue && order.FineAmount > 0)
                        {
                            outcomeStrs.Add($"Fine: ₹{order.FineAmount.Value:N0}");
                        }
                        if (order.Confidence == ClassificationConfidence.Low)
                        {
                            outcomeStrs.Add("(Low Confidence)");
                        }
                        text.Span(string.Join(", ", outcomeStrs)).FontSize(7.2f).FontColor(Colors.Grey.Darken2);
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
    private static string PassageClaim(string claim) => claim switch
    {
        "risk_trigger" => "Why the tier was raised", "restraint" => "Restraint", "lis_pendens" => "Lis pendens",
        "operative_order" => "Operative order", "outcome" => "Outcome", "target_party" => "The company's role", _ => claim
    };

    /// <summary>Said under the orders when the analysis read no order text (its basis is Metadata-Led) and none of the
    /// documents is in the portal, so the reader knows why it rests on metadata.</summary>
    private const string RestsOnRecord = " The analysis rests on the case record alone.";

    /// <summary>The analysis's unknowns, as the deliverable shows them. They hold material warnings the analysis
    /// checks add (a figure not confirmed by the printed order text, an amount dropped, order text left out for length),
    /// which must reach the reader. A note is left out only when it says nothing beyond what the card already discloses
    /// in full: that no order text was there to read — and only when the analysis is Metadata-Led and no order document
    /// of the case is in the portal, so the orders panel states both — or that the record gives no case stage or amount in
    /// dispute. A compound note ("no order text available to determine the relief sought"), a note on a case with a
    /// downloaded order (whose text may be unreadable) and a note on an analysis that did read order text are kept as written.</summary>
    internal static IReadOnlyList<string> EvidenceLimitations(IEnumerable<string>? unknowns, IReadOnlyList<StandaloneReportOrderDto> orders, bool metadataLed)
    {
        var disclosedByOrdersPanel = metadataLed && orders.All(o => o.AvailabilityBucket != LitigationOrderAvailabilityBucket.Downloaded);
        return (unknowns ?? [])
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Where(u => !(disclosedByOrdersPanel && OnlyNoOrderText.IsMatch(u.Trim())) && !OnlyFieldNotGiven.IsMatch(u.Trim()))
            .ToList();
    }

    private const System.Text.RegularExpressions.RegexOptions NoteOptions =
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant;

    /// <summary>A whole note saying only that no order text was available, optionally that the analysis is therefore
    /// metadata-based — nothing about what in particular could not be determined.</summary>
    private static readonly System.Text.RegularExpressions.Regex OnlyNoOrderText = new(
        @"^(?:(?:no\s+order\s+texts?(?:\s+(?:is|was))?\s+(?:available|provided)" +
        @"|order\s+texts?(?:\s+(?:is|are|was|were))?\s+not\s+available)" +
        @"(?:\s+for\s+(?:any|all)(?:\s+of\s+the)?\s+orders?(?:\s+in\s+this\s+case)?|\s+for\s+(?:detailed\s+)?analysis)?" +
        @"(?:\s*[;,]\s*(?:the\s+)?analysis\s+(?:is\s+)?(?:based\s+(?:solely\s+|only\s+)?on\s+(?:case\s+)?metadata(?:\s+only)?|metadata-led(?:\s+only)?))?" +
        @"|analysis\s+is\s+metadata-led\s+as\s+no\s+order\s+text\s+was\s+available)\.?$", NoteOptions);

    /// <summary>A whole note saying only that the record gives no case stage or amount in dispute — the card shows "-".</summary>
    private static readonly System.Text.RegularExpressions.Regex OnlyFieldNotGiven = new(
        @"^(?:the\s+)?(?:case\s+stage|amount\s+in\s+dispute)(?:\s+(?:is|was))?\s+" +
        @"(?:not\s+(?:specified|available|recorded|stated|given)|could\s+not\s+be\s+determined)" +
        @"(?:\s+(?:in|from)\s+(?:the\s+)?(?:provided\s+)?(?:court\s+record|record|metadata))?\.?$", NoteOptions);

    /// <summary>The orders are listed by the court record either way; this says which of their documents the portal holds.</summary>
    internal static string OrderAvailabilityNote(int available, int listed) => available == 0
        ? listed == 1
            ? "The court record lists this order, but its document is not available."
            : $"The court record lists these {listed} orders, but their documents are not available."
        : $"Documents are available for {available} of the {listed} orders listed.";

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;
    private static string JoinValues(string? first, string? second) => string.Join(" / ", new[] { first, second }.Where(value => !string.IsNullOrWhiteSpace(value)));
    private static string Humanize(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value.Replace('_', ' ').ToUpperInvariant();
}
