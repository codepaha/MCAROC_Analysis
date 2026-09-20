using System.Globalization;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace MCAROC_Analysis.Services.LitigationData;

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
            foreach (var item in report.Source.Cases) { serial++; WriteRow(csv, serial, report, item); }
        }
        return stream.ToArray();
    }

    private static readonly string[] CsvHeaders =
    [
        "Sr No", "Assignment Number", "Company", "Provider Case ID", "CSP ID", "CNR Number", "Court Category", "Direction",
        "Court", "Bench", "Case Number", "Case Type", "Case Year", "Case Stage", "Case Status", "Act", "Filing Date",
        "Last Hearing Date", "Next Hearing Date", "Decision Date", "State", "District", "Petitioners", "Respondents",
        "Petitioner Advocates", "Respondent Advocates", "Order Count", "Order Dates", "Order Types", "Analysis Status",
        "Analysis Risk", "Analysis Summary", "Analysis Key Issues", "Analysis Recommended Action"
    ];

    private static void WriteRow(CsvWriter csv, int serial, StandaloneLitigationReport report, BprLitigationCase item)
    {
        var analysis = report.AnalysisFor(item);
        foreach (var value in new[]
        {
            serial.ToString(CultureInfo.InvariantCulture), report.AssignmentNumber, report.CompanyName,
            item.ProviderCaseId, item.CspId, item.CnrNumber, item.CourtCategory, item.Direction, item.Court, item.Bench,
            item.CaseNumber, item.CaseType, item.CaseYear, item.CaseStage, item.CaseStatus, item.Act, item.FilingDate,
            item.LastHearingDate, item.NextHearingDate, item.DecisionDate, item.State, item.District,
            PartyText(item.PetitionersJson), PartyText(item.RespondentsJson), PartyText(item.PetitionerAdvocatesJson),
            PartyText(item.RespondentAdvocatesJson), item.Orders.Count.ToString(CultureInfo.InvariantCulture),
            string.Join("; ", item.Orders.Select(order => order.OrderDate).Where(value => !string.IsNullOrWhiteSpace(value))),
            string.Join("; ", item.Orders.Select(order => order.OrderType).Where(value => !string.IsNullOrWhiteSpace(value))),
            analysis?.Status ?? "Pending", analysis?.RiskLevel, analysis?.Summary,
            analysis is null ? null : string.Join("; ", analysis.KeyIssues ?? []), analysis?.RecommendedAction
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

public sealed record StandaloneLitigationReport(
    string AssignmentNumber,
    string CompanyName,
    DateTimeOffset GeneratedAtUtc,
    BprLitigationReport Source,
    LitigationPortfolioAnalysis? PortfolioAnalysis = null,
    IReadOnlyDictionary<string, LitigationCaseAnalysis>? CaseAnalysesByProviderCaseId = null)
{
    public LitigationCaseAnalysis? AnalysisFor(BprLitigationCase item) =>
        !string.IsNullOrWhiteSpace(item.ProviderCaseId) && CaseAnalysesByProviderCaseId is not null &&
        CaseAnalysesByProviderCaseId.TryGetValue(item.ProviderCaseId, out var analysis) ? analysis : null;
}

/// <summary>Evidence-backed portfolio interpretation, supplied by the future analysis pipeline.</summary>
public sealed record LitigationPortfolioAnalysis(string Status, string? RiskLevel, string? Summary, IReadOnlyList<string>? KeyFindings);

/// <summary>Evidence-backed interpretation for a provider case. A missing entry means analysis has not run.</summary>
public sealed record LitigationCaseAnalysis(string Status, string? RiskLevel, string? Summary, IReadOnlyList<string>? KeyIssues, string? RecommendedAction, string? EvidenceNote = null);

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
                column.Item().PageBreak();
                var serial = 0;
                foreach (var item in report.Source.Cases)
                {
                    serial++;
                    column.Item().EnsureSpace(180).Element(card => ComposeCaseCard(card, serial, item, report.AnalysisFor(item)));
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
        var cases = report.Source.Cases;
        var pending = cases.Count(item => IsPending(item.CaseStatus));
        var disposed = cases.Count(item => IsDisposed(item.CaseStatus));
        var orders = cases.Sum(item => item.Orders.Count);
        column.Item().PaddingTop(6).Text("Litigation Due Diligence Report").Bold().FontSize(22).FontColor(Ink);
        column.Item().PaddingTop(2).Text(report.CompanyName).SemiBold().FontSize(13).FontColor(Navy);
        column.Item().Text($"Assignment {report.AssignmentNumber}  |  Generated {report.GeneratedAtUtc:dd MMM yyyy, HH:mm} UTC").FontSize(8).FontColor(Colors.Grey.Darken1);
        column.Item().PaddingTop(10).Element(container => InformationTable(container, new[]
        {
            ("SOURCE", "BPR Litigation Data Lake"), ("SOURCE REPORT DATE", Display(report.Source.Request.ReportDate)),
            ("KEYWORDS SEARCHED", report.Source.Request.Keywords.Count == 0 ? "Not supplied by source" : string.Join(" | ", report.Source.Request.Keywords)),
            ("REPORT SCOPE", "Standalone litigation report; separate from the MCA ROC dossier")
        }));
        column.Item().PaddingTop(12).Row(row =>
        {
            Metric(row.RelativeItem(), "CASES", cases.Count.ToString(CultureInfo.InvariantCulture), Navy); row.ConstantItem(7);
            Metric(row.RelativeItem(), "PENDING", pending.ToString(CultureInfo.InvariantCulture), Red); row.ConstantItem(7);
            Metric(row.RelativeItem(), "DISPOSED", disposed.ToString(CultureInfo.InvariantCulture), Green); row.ConstantItem(7);
            Metric(row.RelativeItem(), "ORDERS ON RECORD", orders.ToString(CultureInfo.InvariantCulture), Amber);
        });
        column.Item().PaddingTop(13).Element(container => PortfolioPanel(container, report.PortfolioAnalysis));
        column.Item().PaddingTop(10).Background("#FAFCFF").Border(0.8f).BorderColor(Border).Padding(9).Column(note =>
        {
            note.Item().Text("REPORT USE AND EVIDENCE").Bold().FontSize(8).FontColor(Navy);
            note.Item().PaddingTop(4).Text("Each following card preserves case metadata returned by the source. Case analysis is shown only where an evidence-backed analysis has been supplied. Original order files are accessed from the portal and are not embedded in this report.").FontSize(8.2f).FontColor(Colors.Grey.Darken1);
        });
    }

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

    private static void ComposeCaseCard(IContainer container, int serial, BprLitigationCase item, LitigationCaseAnalysis? analysis) => container.Border(0.9f).BorderColor(Border).Column(card =>
    {
        var accent = StatusColor(item.CaseStatus);
        card.Item().Background(BlueTint).BorderLeft(4).BorderColor(accent).Padding(9).Row(row =>
        {
            row.RelativeItem().Column(title => { title.Item().Text($"CASE {serial:000}").Bold().FontSize(7.5f).FontColor(Navy); title.Item().PaddingTop(2).Text(Display(item.CaseNumber)).Bold().FontSize(14).FontColor(Ink); title.Item().PaddingTop(1).Text(Display(item.Court)).FontSize(8.5f).FontColor(Colors.Grey.Darken1); });
            row.AutoItem().Column(badges => { badges.Item().AlignRight().Element(c => Pill(c, Display(item.CaseStatus).ToUpperInvariant(), accent)); badges.Item().PaddingTop(4).AlignRight().Text(item.Orders.Count == 1 ? "1 ORDER ON RECORD" : $"{item.Orders.Count} ORDERS ON RECORD").FontSize(7).FontColor(Amber); });
        });
        card.Item().Padding(9).Column(body =>
        {
            body.Item().Text("CASE PARTICULARS").Bold().FontSize(8).FontColor(Navy);
            body.Item().PaddingTop(5).Element(c => CaseDetailsGrid(c, item));
            body.Item().PaddingTop(9).Element(c => PartiesPanel(c, item));
            body.Item().PaddingTop(9).Element(c => AnalysisPanel(c, analysis));
            body.Item().PaddingTop(9).Element(c => OrdersPanel(c, item.Orders));
        });
    });

    private static void CaseDetailsGrid(IContainer container, BprLitigationCase item) => container.Table(table =>
    {
        table.ColumnsDefinition(columns => { columns.RelativeColumn(); columns.RelativeColumn(); });
        var fields = new (string Label, string Value)[]
        {
            ("CNR NO", Display(item.CnrNumber)), ("CSP ID", Display(item.CspId)), ("COURT CATEGORY", Humanize(item.CourtCategory)), ("CASE TYPE / YEAR", JoinValues(item.CaseType, item.CaseYear)),
            ("CASE STAGE", Display(item.CaseStage)), ("DIRECTION", Humanize(item.Direction)), ("FILING DATE", Display(item.FilingDate)), ("LAST HEARING", Display(item.LastHearingDate)),
            ("NEXT HEARING", Display(item.NextHearingDate)), ("DECISION DATE", Display(item.DecisionDate)), ("STATE / DISTRICT", JoinValues(item.State, item.District)), ("ACT", Display(item.Act))
        };
        foreach (var field in fields) table.Cell().BorderBottom(0.5f).BorderColor("#D7E3F2").PaddingVertical(3).PaddingRight(7).Text(text => { text.Span(field.Label + "  ").Bold().FontSize(6.8f).FontColor(Navy); text.Span(field.Value).FontSize(8.1f); });
    });

    private static void PartiesPanel(IContainer container, BprLitigationCase item) => container.Background("#FBFDFF").Border(0.7f).BorderColor(Border).Padding(8).Column(panel =>
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
        panel.Item().Row(row => { row.RelativeItem().Text("CASE ANALYSIS").Bold().FontSize(8).FontColor(Navy); row.AutoItem().Element(c => Pill(c, analysis?.RiskLevel ?? "PENDING", analysis is null ? Navy : RiskColor(analysis.RiskLevel))); });
        panel.Item().PaddingTop(5).Text(analysis?.Summary ?? "Analysis pending. It will be generated only after extracted order text and case metadata are available for this case.").FontSize(8.2f);
        if (analysis?.KeyIssues is { Count: > 0 }) { panel.Item().PaddingTop(5).Text("KEY ISSUES").Bold().FontSize(7).FontColor(Navy); foreach (var issue in analysis.KeyIssues) panel.Item().PaddingTop(2).Text(text => { text.Span("• ").FontColor(Navy); text.Span(issue); }); }
        if (!string.IsNullOrWhiteSpace(analysis?.RecommendedAction)) panel.Item().PaddingTop(5).Text(text => { text.Span("RECOMMENDED ACTION  ").Bold().FontSize(7).FontColor(Green); text.Span(analysis.RecommendedAction).FontSize(8); });
        if (!string.IsNullOrWhiteSpace(analysis?.EvidenceNote)) panel.Item().PaddingTop(4).Text(analysis.EvidenceNote).Italic().FontSize(7.2f).FontColor(Colors.Grey.Darken1);
    });

    private static void OrdersPanel(IContainer container, IReadOnlyList<BprLitigationOrder> orders) => container.Column(panel =>
    {
        panel.Item().Text("ORDERS & JUDGMENTS").Bold().FontSize(8).FontColor(Navy);
        if (orders.Count == 0) { panel.Item().PaddingTop(4).Text("No order record was returned for this case.").FontSize(8).FontColor(Colors.Grey.Darken1); return; }
        foreach (var order in orders) panel.Item().PaddingTop(4).Background("#FFFCF4").BorderLeft(3).BorderColor(Amber).Padding(5).Text(text => { text.Span(Display(order.OrderDate)).Bold().FontColor(Amber); text.Span("  |  "); text.Span(Display(order.OrderType)); text.Span("  |  Available through the portal while retained.").FontColor(Colors.Grey.Darken1); });
    });

    private static void InformationTable(IContainer container, IEnumerable<(string Label, string Value)> values) => container.Table(table =>
    {
        table.ColumnsDefinition(columns => { columns.ConstantColumn(145); columns.RelativeColumn(); });
        foreach (var (label, value) in values) { table.Cell().Background("#EAF2FD").Border(0.5f).BorderColor(Border).Padding(6).Text(label).Bold().FontSize(7.5f).FontColor(Navy); table.Cell().Border(0.5f).BorderColor(Border).Padding(6).Text(value).FontSize(8.4f); }
    });

    private static void Pill(IContainer container, string text, string color) => container.Background("#FFFFFF").Border(0.8f).BorderColor(color).CornerRadius(7).PaddingVertical(3).PaddingHorizontal(7).Text(text).Bold().FontSize(6.8f).FontColor(color);
    private static bool IsPending(string? status) => status?.Contains("PENDING", StringComparison.OrdinalIgnoreCase) == true;
    private static bool IsDisposed(string? status) => status?.Contains("DISPOSED", StringComparison.OrdinalIgnoreCase) == true;
    private static string StatusColor(string? status) => IsPending(status) ? Red : IsDisposed(status) ? Green : Amber;
    private static string RiskColor(string? risk) => risk?.StartsWith("R1", StringComparison.OrdinalIgnoreCase) == true ? Green : risk?.StartsWith("R2", StringComparison.OrdinalIgnoreCase) == true ? Amber : risk?.StartsWith("R3", StringComparison.OrdinalIgnoreCase) == true ? Red : Navy;
    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value;
    private static string JoinValues(string? first, string? second) => string.Join(" / ", new[] { first, second }.Where(value => !string.IsNullOrWhiteSpace(value)));
    private static string Humanize(string? value) => string.IsNullOrWhiteSpace(value) ? "-" : value.Replace('_', ' ').ToUpperInvariant();
}
