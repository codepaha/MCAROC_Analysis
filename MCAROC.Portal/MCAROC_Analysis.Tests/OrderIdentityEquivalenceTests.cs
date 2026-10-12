using System.Text;
using MCAROC_Analysis.Services.LitigationModule.Orders;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class OrderIdentityEquivalenceTests
{
    [Fact]
    public void Two_distinct_orders_the_same_day_for_one_case_both_survive()
    {
        // Two distinct orders pronounced on the same date for the same verified case
        var date = new DateOnly(2026, 5, 15);
        const long caseId = 1001;

        var pdfBytes1 = Encoding.UTF8.GetBytes("%PDF-1.4 Daily listing order text");
        var pdfBytes2 = Encoding.UTF8.GetBytes("%PDF-1.4 Interim relief granted injunction order");

        var hash1 = OrderContentHasher.ComputeBytesHash(pdfBytes1);
        var hash2 = OrderContentHasher.ComputeBytesHash(pdfBytes2);

        var order1 = new OrderComparisonItem(
            OrderId: 10,
            Source: "Portal",
            OrderKey: "key-1",
            CaseId: caseId,
            IsCaseVerified: true,
            OrderDate: date,
            ContentHash: hash1,
            HasExtractedText: true);

        var order2 = new OrderComparisonItem(
            OrderId: 11,
            Source: "Portal",
            OrderKey: "key-2",
            CaseId: caseId,
            IsCaseVerified: true,
            OrderDate: date,
            ContentHash: hash2,
            HasExtractedText: true);

        var result = OrderEquivalence.Evaluate(order1, order2);

        // Neither collapses; both survive as separate distinct orders
        Assert.Equal(OrderEquivalenceOutcome.Distinct, result.Outcome);
        Assert.False(result.IsEquivalent);
        Assert.False(result.IsPossibleDuplicate);
        Assert.Null(result.SurvivorOrderId);
        Assert.Null(result.CollapsedOrderId);
    }

    [Fact]
    public void The_same_DRT_file_reached_through_pdf1_and_qrpdfview_collapses()
    {
        // Base64 encoding for "drtcourt/delhi/2026/order_555.pdf***123#1#benchA"
        var innerPath1 = "drtcourt/delhi/2026/order_555.pdf***123#1#benchA";
        var b64Path1 = Convert.ToBase64String(Encoding.UTF8.GetBytes(innerPath1));
        var url1 = $"https://cis.drt.gov.in/order/pdf1.php?file={b64Path1}";

        // Base64 encoding for "drtcourt/delhi/2026/order_555.pdf***999#2#benchB"
        var innerPath2 = "drtcourt/delhi/2026/order_555.pdf***999#2#benchB";
        var b64Path2 = Convert.ToBase64String(Encoding.UTF8.GetBytes(innerPath2));
        var url2 = $"https://cis.drt.gov.in/order/qrpdfview.php?file={b64Path2}";

        var date = new DateOnly(2026, 3, 20);

        var key1 = OrderKeyBuilder.ForDrt(
            tribunalId: 1,
            caseTypeId: 10,
            caseNo: "555",
            caseYear: 2026,
            kind: "daily",
            orderDate: date,
            url: url1);

        var key2 = OrderKeyBuilder.ForDrt(
            tribunalId: 1,
            caseTypeId: 10,
            caseNo: "555",
            caseYear: 2026,
            kind: "daily",
            orderDate: date,
            url: url2);

        // Both URLs resolve to the identical OrderKey
        Assert.Equal(key1, key2);
        Assert.Contains("delhi/2026/order_555.pdf", key1);
    }

    [Fact]
    public void Identical_content_from_vendor_and_portal_collapses_only_after_the_hash_check()
    {
        var date = new DateOnly(2026, 4, 10);
        const long caseId = 2001;

        var vendorKey = OrderKeyBuilder.ForBpr("https://vendor.example/orders/doc100.pdf", date, "Daily Order");
        var portalKey = OrderKeyBuilder.ForDrt(1, 5, "100", 2026, "daily", date, "https://cis.drt.gov.in/order/pdf1.php?file=ZHJ0Y291cnQvb3JkZXJzL2RvYzEwMC5wZGY=");

        // Step A: Before content hash is known (hash is not yet computed/available)
        var vendorOrderBeforeHash = new OrderComparisonItem(
            OrderId: 101,
            Source: "Bpr",
            OrderKey: vendorKey,
            CaseId: caseId,
            IsCaseVerified: true,
            OrderDate: date,
            ContentHash: null,
            HasExtractedText: false);

        var portalOrderBeforeHash = new OrderComparisonItem(
            OrderId: 102,
            Source: "Drt",
            OrderKey: portalKey,
            CaseId: caseId,
            IsCaseVerified: true,
            OrderDate: date,
            ContentHash: null,
            HasExtractedText: false);

        var beforeCheck = OrderEquivalence.Evaluate(vendorOrderBeforeHash, portalOrderBeforeHash);

        // Before hash check: flagged as possible duplicate candidate, but NOT collapsed
        Assert.Equal(OrderEquivalenceOutcome.PossibleDuplicate, beforeCheck.Outcome);
        Assert.True(beforeCheck.IsPossibleDuplicate);
        Assert.False(beforeCheck.IsEquivalent);
        Assert.Null(beforeCheck.SurvivorOrderId);
        Assert.Null(beforeCheck.CollapsedOrderId);

        // Step B: After content hash is computed and confirmed identical
        var identicalPdfBytes = Encoding.UTF8.GetBytes("%PDF-1.4 Confirmed final court judgment order body");
        var contentHash = OrderContentHasher.ComputeBytesHash(identicalPdfBytes);

        var vendorOrderAfterHash = vendorOrderBeforeHash with { ContentHash = contentHash, HasExtractedText = false };
        var portalOrderAfterHash = portalOrderBeforeHash with { ContentHash = contentHash, HasExtractedText = true };

        var afterCheck = OrderEquivalence.Evaluate(vendorOrderAfterHash, portalOrderAfterHash);

        // Collapses only after the hash check; the order with extracted text survives
        Assert.Equal(OrderEquivalenceOutcome.Equivalent, afterCheck.Outcome);
        Assert.True(afterCheck.IsEquivalent);
        Assert.Equal(102, afterCheck.SurvivorOrderId); // Portal order has extracted text
        Assert.Equal(101, afterCheck.CollapsedOrderId);
    }

    [Fact]
    public void Same_date_and_similar_name_but_different_content_stays_two_orders()
    {
        var date = new DateOnly(2026, 7, 20);
        const long caseId = 3001;

        // Two orders with matching date, verified case, and similar path names
        var pdfBytes1 = Encoding.UTF8.GetBytes("%PDF-1.4 Morning proceedings order sheet");
        var pdfBytes2 = Encoding.UTF8.GetBytes("%PDF-1.4 Afternoon pronouncement final decree");

        var hash1 = OrderContentHasher.ComputeBytesHash(pdfBytes1);
        var hash2 = OrderContentHasher.ComputeBytesHash(pdfBytes2);

        var order1 = new OrderComparisonItem(
            OrderId: 201,
            Source: "Bpr",
            OrderKey: "https://court.gov.in/orders/case_3001_order_part1.pdf|2026-07-20|Order",
            CaseId: caseId,
            IsCaseVerified: true,
            OrderDate: date,
            ContentHash: hash1,
            HasExtractedText: true);

        var order2 = new OrderComparisonItem(
            OrderId: 202,
            Source: "Drt",
            OrderKey: "https://court.gov.in/orders/case_3001_order_part2.pdf|2026-07-20|Order",
            CaseId: caseId,
            IsCaseVerified: true,
            OrderDate: date,
            ContentHash: hash2,
            HasExtractedText: true);

        var result = OrderEquivalence.Evaluate(order1, order2);

        // Different content -> stays two separate orders
        Assert.Equal(OrderEquivalenceOutcome.Distinct, result.Outcome);
        Assert.False(result.IsEquivalent);
        Assert.Contains("hashes differ", result.Reason);
    }

    [Fact]
    public void Changed_content_under_one_identity_creates_a_new_version()
    {
        var content1 = Encoding.UTF8.GetBytes("%PDF-1.4 Original order with typo");
        var content2 = Encoding.UTF8.GetBytes("%PDF-1.4 Revised and signed order correcting typographical error");

        var hash1 = OrderContentHasher.ComputeBytesHash(content1);
        var hash2 = OrderContentHasher.ComputeBytesHash(content2);

        // 1. Initial download
        var initial = OrderVersionRule.Evaluate(null, 1, hash1);
        Assert.Equal(OrderVersionDecisionType.Initial, initial.Decision);
        Assert.Equal(1, initial.VersionNumber);
        Assert.False(initial.PreviousMarkedSuperseded);

        // 2. Redownload of same content is identical
        var unchanged = OrderVersionRule.Evaluate(hash1, 1, hash1);
        Assert.Equal(OrderVersionDecisionType.Identical, unchanged.Decision);
        Assert.Equal(1, unchanged.VersionNumber);
        Assert.False(unchanged.PreviousMarkedSuperseded);

        // 3. Changed content under the same order identity creates a new version and marks previous superseded
        var next = OrderVersionRule.Evaluate(hash1, 1, hash2);
        Assert.Equal(OrderVersionDecisionType.Superseded, next.Decision);
        Assert.Equal(2, next.VersionNumber);
        Assert.True(next.PreviousMarkedSuperseded);
    }

    [Fact]
    public void Html_orders_with_different_markup_but_identical_text_collapse()
    {
        var html1 = "<html><body><p>Case called out. <b>Adjourned</b> to 25/11/2026.</p></body></html>";
        var html2 = "<div>Case called out.&nbsp;Adjourned to 25/11/2026.<br/></div>";

        var hash1 = OrderContentHasher.ComputeHtmlTextHash(html1);
        var hash2 = OrderContentHasher.ComputeHtmlTextHash(html2);

        Assert.Equal(hash1, hash2);

        var order1 = new OrderComparisonItem(
            OrderId: 301,
            Source: "Consumer",
            OrderKey: OrderKeyBuilder.ForConsumer(999001, "room-1", 1),
            CaseId: 5001,
            IsCaseVerified: true,
            OrderDate: new DateOnly(2026, 11, 1),
            ContentHash: hash1,
            HasExtractedText: true);

        var order2 = new OrderComparisonItem(
            OrderId: 302,
            Source: "Consumer",
            OrderKey: OrderKeyBuilder.ForConsumer(999001, "room-2", 1),
            CaseId: 5001,
            IsCaseVerified: true,
            OrderDate: new DateOnly(2026, 11, 1),
            ContentHash: hash2,
            HasExtractedText: false);

        var result = OrderEquivalence.Evaluate(order1, order2);

        Assert.Equal(OrderEquivalenceOutcome.Equivalent, result.Outcome);
        Assert.Equal(301, result.SurvivorOrderId);
        Assert.Equal(302, result.CollapsedOrderId);
    }

    [Fact]
    public void Unverified_cases_are_refused_equivalence()
    {
        var date = new DateOnly(2026, 6, 1);
        var bytes = Encoding.UTF8.GetBytes("Identical text across unverified case candidates");
        var hash = OrderContentHasher.ComputeBytesHash(bytes);

        var order1 = new OrderComparisonItem(
            OrderId: 401,
            Source: "Bpr",
            OrderKey: "key-a",
            CaseId: 6001,
            IsCaseVerified: false, // NOT verified
            OrderDate: date,
            ContentHash: hash,
            HasExtractedText: true);

        var order2 = new OrderComparisonItem(
            OrderId: 402,
            Source: "Drt",
            OrderKey: "key-b",
            CaseId: 6001,
            IsCaseVerified: true,
            OrderDate: date,
            ContentHash: hash,
            HasExtractedText: true);

        var result = OrderEquivalence.Evaluate(order1, order2);

        Assert.Equal(OrderEquivalenceOutcome.Distinct, result.Outcome);
        Assert.False(result.IsEquivalent);
        Assert.Contains("unverified", result.Reason);
    }
}

