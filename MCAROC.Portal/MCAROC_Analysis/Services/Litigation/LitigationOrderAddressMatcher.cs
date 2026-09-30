using System.Text.RegularExpressions;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>
/// A target address from the company premises pool or open charge immovable property particulars,
/// to be matched against litigation order/judgment text (issue #191 remainder).
/// </summary>
public sealed record LitigationAddressTarget(
    string SourceLabel,
    string AddressText,
    IReadOnlyList<string?> ExcludedPlaceNames,
    bool IsCompanyPremises,
    long? RocChargeId = null,
    string? RocChargeNumber = null,
    string? ChargeHolder = null);

/// <summary>
/// Result of matching a court order/judgment text against a target address.
/// </summary>
public sealed record LitigationPropertyMatchResult(
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

/// <summary>
/// Matches litigation order/judgment text against company filed premises (registered office,
/// business address, EPFO establishments) and open charge immovable property particulars.
/// Fulfills the deferred second half of issue #191 without requiring schema changes.
/// </summary>
public static partial class LitigationOrderAddressMatcher
{
    private static readonly string[] ImmovableKeywords =
        ["MORTGAGE", "LAND", "BUILDING", "PLOT", "FLAT", "PREMISES", "IMMOVABLE", "FACTORY", "SURVEY"];

    /// <summary>
    /// Builds the structured address pool for a company:
    /// 1. Registered office (from CompanyProfile)
    /// 2. Business address (from CompanyProfile, if distinct from registered office)
    /// 3. EPFO establishments (from EpfoEstablishments)
    /// 4. Open/unsatisfied charges with immovable property particulars (from RocCharges)
    /// </summary>
    public static List<LitigationAddressTarget> BuildAddressPool(
        CompanyProfile? companyProfile,
        IReadOnlyList<EpfoEstablishment>? epfoEstablishments,
        IReadOnlyList<RocCharge>? charges)
    {
        var pool = new List<LitigationAddressTarget>();

        // 1. Registered office
        if (!string.IsNullOrWhiteSpace(companyProfile?.RegisteredAddress))
        {
            pool.Add(new LitigationAddressTarget(
                SourceLabel: "Registered office",
                AddressText: companyProfile.RegisteredAddress,
                ExcludedPlaceNames: [companyProfile.RegisteredAddressCity, companyProfile.RegisteredAddressState],
                IsCompanyPremises: true));
        }

        // 2. Business address (if different from registered office)
        var businessAddress = companyProfile?.BusinessAddress?.Trim();
        var registeredAddress = companyProfile?.RegisteredAddress?.Trim();
        if (!string.IsNullOrWhiteSpace(businessAddress)
            && !string.Equals(businessAddress, registeredAddress, StringComparison.OrdinalIgnoreCase))
        {
            pool.Add(new LitigationAddressTarget(
                SourceLabel: "Business address",
                AddressText: businessAddress,
                ExcludedPlaceNames: [companyProfile?.RegisteredAddressCity],
                IsCompanyPremises: true));
        }

        // 3. EPFO establishments
        if (epfoEstablishments is not null)
        {
            foreach (var est in epfoEstablishments.Where(e => !string.IsNullOrWhiteSpace(e.Address)))
            {
                pool.Add(new LitigationAddressTarget(
                    SourceLabel: $"EPFO establishment {est.EstablishmentId}",
                    AddressText: est.Address!,
                    ExcludedPlaceNames: [est.City],
                    IsCompanyPremises: true));
            }
        }

        // 4. Open/unsatisfied charges describing immovable property
        if (charges is not null)
        {
            foreach (var charge in charges.Where(c => c.SatisfactionDate is null))
            {
                var immovableEvents = charge.Events
                    .Where(e => !string.IsNullOrWhiteSpace(e.PropertyParticulars) && DescribesImmovableProperty(e))
                    .ToList();

                foreach (var ev in immovableEvents)
                {
                    var label = !string.IsNullOrWhiteSpace(charge.RocChargeNumber)
                        ? $"Charge {charge.RocChargeNumber} ({charge.LatestChargeHolderRaw ?? "Charge Holder"})"
                        : $"Charge #{charge.ChargeId} ({charge.LatestChargeHolderRaw ?? "Charge Holder"})";

                    // Avoid duplicate entries for the same charge if modified events repeat the exact particulars
                    if (pool.Any(p => p.RocChargeId == charge.ChargeId && string.Equals(p.AddressText.Trim(), ev.PropertyParticulars!.Trim(), StringComparison.OrdinalIgnoreCase)))
                        continue;

                    pool.Add(new LitigationAddressTarget(
                        SourceLabel: label,
                        AddressText: ev.PropertyParticulars!,
                        ExcludedPlaceNames: [],
                        IsCompanyPremises: false,
                        RocChargeId: charge.ChargeId,
                        RocChargeNumber: charge.RocChargeNumber,
                        ChargeHolder: charge.LatestChargeHolderRaw));
                }
            }
        }

        return pool;
    }

    /// <summary>
    /// Checks whether a litigation case belongs to the NCLT / NCLAT forum.
    /// Per issue #191 hard prerequisite: "The Coastal litigation-orders corpus is not usable as-is:
    /// the dedupe scanner from #189 found NCLT 93% duplicate/mislabeled (205 of 221 files)...
    /// Do not start property-extraction work against NCLT until that archive is re-sourced".
    /// </summary>
    public static bool IsNcltCase(LitigationCase? c)
    {
        if (c is null) return false;

        return IsNcltText(c.CourtCategory)
            || IsNcltText(c.Court)
            || IsNcltText(c.Bench)
            || IsNcltText(c.Type)
            || IsNcltText(c.CaseType);
    }

    /// <summary>
    /// Checks whether a litigation order belongs to an NCLT / NCLAT case or archive.
    /// </summary>
    public static bool IsNcltOrder(LitigationCaseOrder? order, IReadOnlyDictionary<long, LitigationCase>? casesByOrderId = null)
    {
        if (order is null) return false;
        var caseItem = order.Case ?? (casesByOrderId != null && casesByOrderId.TryGetValue(order.LitigationCaseOrderId, out var c) ? c : null);
        if (IsNcltCase(caseItem)) return true;
        if (IsNcltText(order.OrderType)) return true;
        if (IsNcltText(order.PdfUrl)) return true;
        return false;
    }

    private static bool IsNcltText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains("NCLT", StringComparison.OrdinalIgnoreCase)
            || text.Contains("NCLAT", StringComparison.OrdinalIgnoreCase)
            || text.Contains("National Company Law", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Matches a collection of litigation cases and their orders against the address pool,
    /// skipping NCLT cases whose order corpus is known to be corrupted (#191).
    /// </summary>
    public static List<LitigationPropertyMatchResult> MatchCases(
        IReadOnlyList<LitigationAddressTarget> addressPool,
        IReadOnlyList<LitigationCase> cases,
        IReadOnlyDictionary<long, LitigationOrderDocument> orderDocuments)
    {
        if (addressPool.Count == 0 || cases.Count == 0)
            return [];

        var results = new List<LitigationPropertyMatchResult>();

        foreach (var c in cases)
        {
            if (IsNcltCase(c))
                continue;

            foreach (var order in c.Orders)
            {
                if (IsNcltOrder(order))
                    continue;

                if (!orderDocuments.TryGetValue(order.LitigationCaseOrderId, out var doc))
                    continue;

                if (string.IsNullOrWhiteSpace(doc.ExtractedText) ||
                    doc.TextExtractionStatus != FilingDocumentProcessingStatus.TextExtracted)
                    continue;

                var orderMatches = MatchText(addressPool, order.LitigationCaseOrderId, order.OrderDate, order.OrderType, doc.ExtractedText);
                results.AddRange(orderMatches);
            }
        }

        return results;
    }

    /// <summary>
    /// Strong matches only, for scanning a whole request (every case, not one page of cards). Produces exactly the
    /// Strong results of <see cref="MatchCases"/> but skips the costly address comparison wherever it cannot
    /// succeed: a Strong match needs a plot/survey/door number in common, so each paragraph is tokenised once and a
    /// charged address is only compared when it shares a plot key with that paragraph. Addresses with no plot key
    /// can never be Strong and are dropped. On a 22 MB corpus this is the difference between minutes and seconds.
    /// </summary>
    public static List<LitigationPropertyMatchResult> MatchCasesStrongOnly(
        IReadOnlyList<LitigationAddressTarget> addressPool,
        IReadOnlyList<LitigationCase> cases,
        IReadOnlyDictionary<long, LitigationOrderDocument> orderDocuments)
    {
        var targets = addressPool
            .Select(t => (Target: t, Keys: AddressMatcher.PlotKeysOfAddress(t.AddressText)))
            .Where(x => x.Keys.Count > 0)
            .ToList();
        if (targets.Count == 0 || cases.Count == 0) return [];

        var results = new List<LitigationPropertyMatchResult>();
        foreach (var c in cases)
        {
            if (IsNcltCase(c)) continue;
            foreach (var order in c.Orders)
            {
                if (IsNcltOrder(order)) continue;
                if (!orderDocuments.TryGetValue(order.LitigationCaseOrderId, out var doc)) continue;
                if (string.IsNullOrWhiteSpace(doc.ExtractedText) || doc.TextExtractionStatus != FilingDocumentProcessingStatus.TextExtracted) continue;
                results.AddRange(MatchTextStrongOnly(targets, order.LitigationCaseOrderId, order.OrderDate, order.OrderType, doc.ExtractedText));
            }
        }
        return results;
    }

    private static List<LitigationPropertyMatchResult> MatchTextStrongOnly(
        IReadOnlyList<(LitigationAddressTarget Target, HashSet<string> Keys)> targets,
        long orderId, string? orderDate, string? orderType, string extractedText)
    {
        var matches = new List<LitigationPropertyMatchResult>();
        foreach (var page in SegmentPages(extractedText))
        {
            var paragraphs = SplitParagraphs(page.Text);
            var paragraphKeys = paragraphs.Select(AddressMatcher.PlotKeysIn).ToList();
            var pageKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var k in paragraphKeys) pageKeys.UnionWith(k);
            var wholePageKeys = paragraphs.Count > 1 ? AddressMatcher.PlotKeysIn(page.Text) : pageKeys;

            foreach (var (target, keys) in targets)
            {
                if (!keys.Overlaps(wholePageKeys)) continue; // no shared plot number anywhere on the page: cannot be Strong

                var matched = false;
                for (var i = 0; i < paragraphs.Count; i++)
                {
                    if (!keys.Overlaps(paragraphKeys[i])) continue;
                    var r = AddressMatcher.Match(target.AddressText, paragraphs[i], target.ExcludedPlaceNames);
                    if (r.Strength != AddressMatchStrength.Strong) continue;
                    matches.Add(ToResult(target, orderId, orderDate, orderType, page.PageNumber, r, CleanExcerpt(paragraphs[i])));
                    matched = true;
                    break; // one match per (page, target), as in MatchText
                }
                if (matched) continue;

                var pageResult = AddressMatcher.Match(target.AddressText, page.Text, target.ExcludedPlaceNames);
                if (pageResult.Strength == AddressMatchStrength.Strong)
                    matches.Add(ToResult(target, orderId, orderDate, orderType, page.PageNumber, pageResult, ExtractSnippet(page.Text, pageResult)));
            }
        }

        return matches
            .GroupBy(m => (m.LitigationCaseOrderId, m.SourceLabel, m.AddressText, m.PageNumber))
            .Select(g => g.First())
            .ToList();
    }

    private static LitigationPropertyMatchResult ToResult(LitigationAddressTarget target, long orderId, string? orderDate, string? orderType,
        int pageNumber, AddressMatchResult r, string excerpt) => new(
            orderId, orderDate, orderType, pageNumber, target.SourceLabel, target.AddressText, r.Strength, r.MatchedPinCode,
            r.MatchedPlotNumbers, r.MatchedLocalities, excerpt, target.IsCompanyPremises, target.RocChargeId, target.RocChargeNumber, target.ChargeHolder);

    /// <summary>
    /// Matches a collection of litigation case orders against the address pool.
    /// Excludes orders from NCLT / NCLAT cases (#191).
    /// </summary>
    public static List<LitigationPropertyMatchResult> MatchOrders(
        IReadOnlyList<LitigationAddressTarget> addressPool,
        IReadOnlyList<LitigationCaseOrder> orders,
        IReadOnlyDictionary<long, LitigationOrderDocument> orderDocuments,
        IReadOnlyDictionary<long, LitigationCase>? casesByOrderId = null)
    {
        if (addressPool.Count == 0 || orders.Count == 0)
            return [];

        var results = new List<LitigationPropertyMatchResult>();

        foreach (var order in orders)
        {
            if (IsNcltOrder(order, casesByOrderId))
                continue;

            if (!orderDocuments.TryGetValue(order.LitigationCaseOrderId, out var doc))
                continue;

            if (string.IsNullOrWhiteSpace(doc.ExtractedText) ||
                doc.TextExtractionStatus != FilingDocumentProcessingStatus.TextExtracted)
                continue;

            var orderMatches = MatchText(addressPool, order.LitigationCaseOrderId, order.OrderDate, order.OrderType, doc.ExtractedText);
            results.AddRange(orderMatches);
        }

        return results;
    }

    /// <summary>
    /// Segments an extracted order text into pages and paragraphs, and evaluates them against the address pool.
    /// </summary>
    public static List<LitigationPropertyMatchResult> MatchText(
        IReadOnlyList<LitigationAddressTarget> addressPool,
        long orderId,
        string? orderDate,
        string? orderType,
        string extractedText)
    {
        if (addressPool.Count == 0 || string.IsNullOrWhiteSpace(extractedText))
            return [];

        var pages = SegmentPages(extractedText);
        var matches = new List<LitigationPropertyMatchResult>();

        foreach (var page in pages)
        {
            var paragraphs = SplitParagraphs(page.Text);

            foreach (var target in addressPool)
            {
                // First pass: test paragraph by paragraph. This bounds the text scope and avoids false PIN conflicts
                // caused by unrelated court headers, parties, or advocates appearing elsewhere on the same page.
                bool matchedInParagraph = false;
                foreach (var para in paragraphs)
                {
                    if (string.IsNullOrWhiteSpace(para)) continue;

                    var paraResult = AddressMatcher.Match(target.AddressText, para, target.ExcludedPlaceNames);
                    if (paraResult.Strength != AddressMatchStrength.None)
                    {
                        matches.Add(new LitigationPropertyMatchResult(
                            LitigationCaseOrderId: orderId,
                            OrderDate: orderDate,
                            OrderType: orderType,
                            PageNumber: page.PageNumber,
                            SourceLabel: target.SourceLabel,
                            AddressText: target.AddressText,
                            Strength: paraResult.Strength,
                            MatchedPinCode: paraResult.MatchedPinCode,
                            MatchedPlotNumbers: paraResult.MatchedPlotNumbers,
                            MatchedLocalities: paraResult.MatchedLocalities,
                            Excerpt: CleanExcerpt(para),
                            IsCompanyPremises: target.IsCompanyPremises,
                            RocChargeId: target.RocChargeId,
                            RocChargeNumber: target.RocChargeNumber,
                            ChargeHolder: target.ChargeHolder));
                        matchedInParagraph = true;
                        break; // One match per (page, target)
                    }
                }

                if (matchedInParagraph) continue;

                // Fallback pass: test entire page text (in case property description spanned a line break)
                var pageResult = AddressMatcher.Match(target.AddressText, page.Text, target.ExcludedPlaceNames);
                if (pageResult.Strength != AddressMatchStrength.None)
                {
                    matches.Add(new LitigationPropertyMatchResult(
                        LitigationCaseOrderId: orderId,
                        OrderDate: orderDate,
                        OrderType: orderType,
                        PageNumber: page.PageNumber,
                        SourceLabel: target.SourceLabel,
                        AddressText: target.AddressText,
                        Strength: pageResult.Strength,
                        MatchedPinCode: pageResult.MatchedPinCode,
                        MatchedPlotNumbers: pageResult.MatchedPlotNumbers,
                        MatchedLocalities: pageResult.MatchedLocalities,
                        Excerpt: ExtractSnippet(page.Text, pageResult),
                        IsCompanyPremises: target.IsCompanyPremises,
                        RocChargeId: target.RocChargeId,
                        RocChargeNumber: target.RocChargeNumber,
                        ChargeHolder: target.ChargeHolder));
                }
            }
        }

        // Deduplicate: If the same target address matched multiple times on the same page,
        // preserve the highest strength and distinct page numbers across distinct target addresses.
        return matches
            .GroupBy(m => (m.LitigationCaseOrderId, m.SourceLabel, m.AddressText, m.PageNumber))
            .Select(g => g.OrderByDescending(m => m.Strength).First())
            .ToList();
    }

    private static bool DescribesImmovableProperty(RocChargeEvent ev)
    {
        if (!string.IsNullOrWhiteSpace(ev.PropertyType))
            return ev.PropertyType.Contains("immovable", StringComparison.OrdinalIgnoreCase);

        var text = ev.PropertyParticulars!.ToUpperInvariant();
        return ImmovableKeywords.Any(text.Contains);
    }

    private sealed record PageSegment(int PageNumber, string Text);

    /// <summary>An order's extracted text split at its page markers (page number + text).</summary>
    public static IReadOnlyList<(int PageNumber, string Text)> PagesOf(string extractedText) =>
        string.IsNullOrWhiteSpace(extractedText) ? [] : SegmentPages(extractedText).Select(p => (p.PageNumber, p.Text)).ToList();

    private static List<PageSegment> SegmentPages(string text)
    {
        var matches = PageMarkerRegex().Matches(text);
        if (matches.Count == 0)
        {
            return [new PageSegment(1, text)];
        }

        var segments = new List<PageSegment>(matches.Count);
        for (int i = 0; i < matches.Count; i++)
        {
            int pageNum = int.TryParse(matches[i].Groups[1].Value, out var p) ? p : (i + 1);
            int startIdx = matches[i].Index + matches[i].Length;
            int endIdx = (i + 1 < matches.Count) ? matches[i + 1].Index : text.Length;

            var pageContent = text[startIdx..endIdx].Trim();
            segments.Add(new PageSegment(pageNum, pageContent));
        }

        return segments;
    }

    private static List<string> SplitParagraphs(string pageText)
    {
        return ParagraphSplitRegex()
            .Split(pageText)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
    }

    private static string CleanExcerpt(string rawParagraph, int maxLength = 250)
    {
        var cleaned = WhitespaceNormalizeRegex().Replace(rawParagraph.Trim(), " ");
        if (cleaned.Length <= maxLength) return cleaned;
        return cleaned[..maxLength] + "...";
    }

    private static string ExtractSnippet(string text, AddressMatchResult result, int radius = 100)
    {
        string? anchor = result.MatchedPlotNumbers.FirstOrDefault()
            ?? result.MatchedLocalities.FirstOrDefault()
            ?? result.MatchedPinCode;

        if (string.IsNullOrEmpty(anchor))
            return CleanExcerpt(text);

        int idx = text.IndexOf(anchor, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return CleanExcerpt(text);

        int start = Math.Max(0, idx - radius);
        int length = Math.Min(text.Length - start, anchor.Length + (radius * 2));
        var snippet = text.Substring(start, length).Trim();
        var cleaned = WhitespaceNormalizeRegex().Replace(snippet, " ");

        return (start > 0 ? "..." : "") + cleaned + (start + length < text.Length ? "..." : "");
    }

    [GeneratedRegex(@"--- Page (\d+) \((?:native|OCR)\) ---", RegexOptions.IgnoreCase)]
    private static partial Regex PageMarkerRegex();

    [GeneratedRegex(@"(?:\r?\n\s*){2,}")]
    private static partial Regex ParagraphSplitRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceNormalizeRegex();
}
