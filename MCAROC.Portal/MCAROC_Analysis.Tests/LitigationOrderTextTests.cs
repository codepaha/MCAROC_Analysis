using System.Text;
using MCAROC_Analysis.Services.LitigationData;

namespace MCAROC_Analysis.Tests;

/// <summary>Orders some forums serve as an HTML page under a ".pdf" name — the shapes below are the ones the Coastal
/// request's order links actually returned.</summary>
public class LitigationOrderTextTests
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Reads_a_DRT_proceedings_snippet_as_its_text()
    {
        var page = "\n\n\n \n\n<table width=\"60%\" >\n    <tr>\n        <td>\n<p>Counsel for D1 to D4 reported No rejoinder. Placed before Honble P.O. Call on 29-04-2019.</p>\n        </td>\n    </tr>\n</table>\n";

        Assert.Equal("Counsel for D1 to D4 reported No rejoinder. Placed before Honble P.O. Call on 29-04-2019.",
            LitigationOrderText.FromHtml(Utf8(page)));
    }

    [Fact]
    public void Keeps_line_breaks_decodes_entities_and_drops_scripts_and_styles()
    {
        var page = "<html><head><style>p{color:red}</style><script>alert('x')</script></head><body>" +
                   "<h3>Daily Order</h3><p>DISTRICT CONSUMER FORUM WARANGAL&nbsp;Complaint Case No. CC/2/2022</p>" +
                   "<p>COASTAL PROJECTS LTD Vs. THE NEW INDIA ASSURANCE CO LTD &amp; Others</p></body></html>";

        var text = LitigationOrderText.FromHtml(Utf8(page));

        Assert.Equal("Daily Order\nDISTRICT CONSUMER FORUM WARANGAL Complaint Case No. CC/2/2022\nCOASTAL PROJECTS LTD Vs. THE NEW INDIA ASSURANCE CO LTD & Others", text);
        Assert.DoesNotContain("alert", text);
        Assert.DoesNotContain("color", text);
        Assert.DoesNotContain("<", text);
    }

    [Theory]
    [InlineData("\uFEFF Orders is not uploaded for case number CRLRC/292/2016 ")] // Telangana High Court's notice
    [InlineData("Order has not been uploaded.")]
    [InlineData("No orders found")]
    [InlineData("")]
    [InlineData("   ")]
    public void A_not_uploaded_notice_or_an_empty_page_is_a_placeholder(string page) =>
        Assert.True(LitigationOrderText.IsPlaceholder(LitigationOrderText.FromHtml(Utf8(page))));

    [Theory]
    [InlineData("Counsel for D1 to D4 reported No rejoinder. Placed before Honble P.O. Call on 29-04-2019.")]
    [InlineData("The order is available with the registry; list on 12.06.2019.")]
    public void Real_order_text_is_never_a_placeholder(string text) =>
        Assert.False(LitigationOrderText.IsPlaceholder(text));

    [Fact]
    public void A_long_order_that_mentions_an_unuploaded_order_is_still_an_order()
    {
        var order = string.Concat(Enumerable.Repeat("The respondent shall file its reply within four weeks. ", 8)) +
                    "It is noted that the order of the trial court is not uploaded on the portal.";
        Assert.False(LitigationOrderText.IsPlaceholder(order));
    }

    [Fact]
    public void The_storage_services_error_document_is_recognised_and_an_order_page_is_not()
    {
        Assert.True(LitigationOrderText.IsStorageError(Utf8(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Error><Code>AccessDenied</Code><Message>Request has expired</Message></Error>")));
        Assert.False(LitigationOrderText.IsStorageError(Utf8("<table><tr><td><p>Counsel for D1 reported no rejoinder.</p></td></tr></table>")));
    }

    [Theory]
    // real order pages from the Coastal request — the shortest DRT proceedings note is 53 characters
    [InlineData("<table><tr><td><p>At request of applicants counsel, Call on 28-03-2019.</p></td></tr></table>")]
    [InlineData("<p>Notice serve to OL. Called absent. Set exparte. For arguments posted to 3.5.19.</p>")]
    public void A_real_order_page_is_order_text(string page) =>
        Assert.Equal(LitigationOrderPageKind.OrderText, LitigationOrderText.Classify(Utf8(page)).Kind);

    [Theory]
    [InlineData("﻿ Orders is not uploaded for case number WP/12540/2019")]
    [InlineData("File does not exist.")]
    public void A_not_uploaded_notice_is_not_published(string page) =>
        Assert.Equal(LitigationOrderPageKind.NotPublished, LitigationOrderText.Classify(Utf8(page)).Kind);

    [Theory]
    // an error returned with HTTP 200 is never an order
    [InlineData("{\"data\":\"\",\"error\":\"Permission denyyy!\"}")] // seen on a real Coastal order link
    [InlineData("{\"status\":false,\"message\":\"expired\"}")]
    [InlineData("[]")]
    [InlineData("<?xml version=\"1.0\"?><Error><Code>AccessDenied</Code><Message>Request has expired</Message></Error>")]
    [InlineData("<html><body>Expired</body></html>")]
    [InlineData("<html><body>Not Found</body></html>")]
    [InlineData("<html><body><h1>403 Forbidden</h1><p>You do not have permission to access this document on this server.</p></body></html>")]
    [InlineData("<html><body>Your session has expired. Please sign in again to download the order.</body></html>")]
    [InlineData("<p>Expired link: the order PDF header %PDF- appears later</p>")] // mentions the PDF header
    [InlineData("   ")]
    public void An_error_body_is_rejected(string page) =>
        Assert.Equal(LitigationOrderPageKind.Rejected, LitigationOrderText.Classify(Utf8(page)).Kind);

    [Fact]
    public void Extracted_text_carries_a_page_marker_so_chunking_and_citations_treat_it_as_page_one() =>
        Assert.StartsWith("--- Page 1 (native) ---\n", LitigationOrderText.AsExtractedText("Order text"));
}
