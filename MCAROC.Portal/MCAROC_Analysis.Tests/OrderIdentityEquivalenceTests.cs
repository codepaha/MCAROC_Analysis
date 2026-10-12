using System.Text;
using MCAROC_Analysis.Services.LitigationModule.Orders;
using MCAROC_Analysis.Services.LitigationModule.Sources.Orders;
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

    [Fact]
    public void When_Consumer_identity_fields_are_missing_builder_and_locator_reject_incomplete_identity()
    {
        // Missing hearing ID
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForConsumer(12345, null, 1));
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForConsumer(12345, "   ", 1));

        // Missing orderTypeId
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForConsumer(12345, "hearing-1", null));

        // Non-positive filing reference
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForConsumer(0, "hearing-1", 1));
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForConsumer(-5, "hearing-1", 1));

        // Valid fields succeed
        var key = OrderKeyBuilder.ForConsumer(12345, "hearing-1", 1);
        Assert.Equal("12345|hearing-1|1", key);

        // Locator validations
        var validLocator = new OrderSourceLocator(
            Source: "Consumer",
            FilingReferenceNumber: 12345,
            CourtRoomHearingId: "hearing-1",
            OrderTypeId: 2);
        Assert.Equal("12345|hearing-1|2", OrderKeyBuilder.ForLocator(validLocator));

        var missingHearingLoc = new OrderSourceLocator(
            Source: "Consumer",
            FilingReferenceNumber: 12345,
            CourtRoomHearingId: null,
            OrderTypeId: 2);
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForLocator(missingHearingLoc));

        var missingTypeLoc = new OrderSourceLocator(
            Source: "Consumer",
            FilingReferenceNumber: 12345,
            CourtRoomHearingId: "hearing-1",
            OrderTypeId: null);
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForLocator(missingTypeLoc));

        var missingRefLoc = new OrderSourceLocator(
            Source: "Consumer",
            FilingReferenceNumber: null,
            CourtRoomHearingId: "hearing-1",
            OrderTypeId: 1);
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForLocator(missingRefLoc));
    }

    [Fact]
    public void When_Bpr_locator_has_order_type_it_is_retained_in_key_preventing_cross_type_collisions()
    {
        const string pdfUrl = "https://vendor.example/orders/doc1.pdf";
        var date = new DateOnly(2025, 4, 15);

        // Two distinct orders from BPR on the same day for the same document URL but differing order types
        var dailyOrderLocator = new OrderSourceLocator(
            Source: "Bpr",
            PdfUrl: pdfUrl,
            DateOfHearing: date,
            OrderType: "Daily Order");

        var finalOrderLocator = new OrderSourceLocator(
            Source: "Bpr",
            PdfUrl: pdfUrl,
            DateOfHearing: date,
            OrderType: "Final Judgment");

        var dailyKey = OrderKeyBuilder.ForLocator(dailyOrderLocator);
        var finalKey = OrderKeyBuilder.ForLocator(finalOrderLocator);

        // Keys do NOT collide; order type is retained
        Assert.NotEqual(dailyKey, finalKey);
        Assert.Equal($"{pdfUrl}|2025-04-15|Daily Order", dailyKey);
        Assert.Equal($"{pdfUrl}|2025-04-15|Final Judgment", finalKey);

        // Missing OrderType or OrderDate or PdfUrl rejects to avoid colliding keys
        var missingTypeLocator = new OrderSourceLocator(
            Source: "Bpr",
            PdfUrl: pdfUrl,
            DateOfHearing: date,
            OrderType: null);
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForLocator(missingTypeLocator));

        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForBpr(null, "2025-04-15", "Daily Order"));
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForBpr(pdfUrl, (string?)null, "Daily Order"));
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForBpr(pdfUrl, "2025-04-15", null));
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForBpr(pdfUrl, "2025-04-15", "   "));
    }

    [Fact]
    public void When_Drt_identity_fields_are_missing_builder_and_locator_reject_incomplete_identity()
    {
        const string validUrl = "https://cis.drt.gov.in/order/pdf1.php?file=ZHJ0Y291cnQvZGVsaGkvMjAyNi9vcmRlcl81NTUucGRm";
        var date = new DateOnly(2026, 3, 20);

        // Valid fields succeed
        var validKey = OrderKeyBuilder.ForDrt(1, 10, "555", 2026, "daily", date, validUrl);
        Assert.Contains("delhi/2026/order_555.pdf", validKey);

        // Missing or non-positive tribunalId
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForDrt(0, 10, "555", 2026, "daily", date, validUrl));
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForDrt(-1, 10, "555", 2026, "daily", date, validUrl));

        // Missing or non-positive caseTypeId
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForDrt(1, null, "555", 2026, "daily", date, validUrl));
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForDrt(1, 0, "555", 2026, "daily", date, validUrl));

        // Missing or whitespace caseNo
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForDrt(1, 10, null, 2026, "daily", date, validUrl));
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForDrt(1, 10, "   ", 2026, "daily", date, validUrl));

        // Missing or non-positive caseYear
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForDrt(1, 10, "555", null, "daily", date, validUrl));
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForDrt(1, 10, "555", 0, "daily", date, validUrl));

        // Missing or whitespace kind
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForDrt(1, 10, "555", 2026, null, date, validUrl));
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForDrt(1, 10, "555", 2026, "   ", date, validUrl));

        // Missing orderDate
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForDrt(1, 10, "555", 2026, "daily", null, validUrl));

        // Missing or whitespace url
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForDrt(1, 10, "555", 2026, "daily", date, null));
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForDrt(1, 10, "555", 2026, "daily", date, "   "));

        // Locator builds full composite DRT OrderKey
        var validLocator = new OrderSourceLocator(
            Source: "Drt",
            TribunalId: 1,
            CaseTypeId: 10,
            CaseNo: "555",
            CaseYear: 2026,
            OrderKind: "daily",
            DateOfHearing: date,
            OrderUrl: validUrl);

        var compositeKey = OrderKeyBuilder.ForLocator(validLocator);
        Assert.Equal("1|10|555|2026|daily|2026-03-20|delhi/2026/order_555.pdf", compositeKey);

        // Path-only helper remains separate from composite identity
        Assert.Equal("delhi/2026/order_555.pdf", OrderKeyBuilder.ForDrtFilePath(validUrl));

        // Two distinct orders sharing a file path across different cases do NOT collide
        var otherCaseLocator = new OrderSourceLocator(
            Source: "Drt",
            TribunalId: 1,
            CaseTypeId: 10,
            CaseNo: "777",
            CaseYear: 2026,
            OrderKind: "daily",
            DateOfHearing: date,
            OrderUrl: validUrl);

        var otherCompositeKey = OrderKeyBuilder.ForLocator(otherCaseLocator);
        Assert.NotEqual(compositeKey, otherCompositeKey);
        Assert.Contains("777", otherCompositeKey);

        // Missing required DRT identity fields on locator rejects
        var missingTribunalLocator = new OrderSourceLocator(
            Source: "Drt",
            TribunalId: null,
            CaseTypeId: 10,
            CaseNo: "555",
            CaseYear: 2026,
            OrderKind: "daily",
            DateOfHearing: date,
            OrderUrl: validUrl);
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForLocator(missingTribunalLocator));

        var missingCaseNoLocator = new OrderSourceLocator(
            Source: "Drt",
            TribunalId: 1,
            CaseTypeId: 10,
            CaseNo: null,
            CaseYear: 2026,
            OrderKind: "daily",
            DateOfHearing: date,
            OrderUrl: validUrl);
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForLocator(missingCaseNoLocator));

        var missingDateLocator = new OrderSourceLocator(
            Source: "Drt",
            TribunalId: 1,
            CaseTypeId: 10,
            CaseNo: "555",
            CaseYear: 2026,
            OrderKind: "daily",
            DateOfHearing: null,
            OrderUrl: validUrl);
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForLocator(missingDateLocator));

        var missingUrlLocator = new OrderSourceLocator(
            Source: "Drt",
            TribunalId: 1,
            CaseTypeId: 10,
            CaseNo: "555",
            CaseYear: 2026,
            OrderKind: "daily",
            DateOfHearing: date,
            OrderUrl: null,
            PdfUrl: null);
        Assert.Throws<ArgumentException>(() => OrderKeyBuilder.ForLocator(missingUrlLocator));
    }

    [Fact]
    public void When_both_OrderDate_and_DateOfHearing_are_supplied_with_different_values_ForLocator_prefers_OrderDate_for_DRT()
    {
        const string validUrl = "https://cis.drt.gov.in/order/pdf1.php?file=ZHJ0Y291cnQvZGVsaGkvMjAyNi9vcmRlcl81NTUucGRm";
        var hearingDate = new DateOnly(2026, 3, 20);
        const string explicitOrderDate = "2026-03-25";

        var locator = new OrderSourceLocator(
            Source: "Drt",
            TribunalId: 1,
            CaseTypeId: 10,
            CaseNo: "555",
            CaseYear: 2026,
            OrderKind: "daily",
            DateOfHearing: hearingDate,
            OrderDate: explicitOrderDate,
            OrderUrl: validUrl);

        var key = OrderKeyBuilder.ForLocator(locator);

        // Key must use explicit OrderDate (2026-03-25), not DateOfHearing (2026-03-20)
        Assert.Contains("2026-03-25", key);
        Assert.DoesNotContain("2026-03-20", key);
        Assert.Equal("1|10|555|2026|daily|2026-03-25|delhi/2026/order_555.pdf", key);
    }
}

