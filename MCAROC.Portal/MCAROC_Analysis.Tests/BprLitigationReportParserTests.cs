using MCAROC_Analysis.Services.LitigationData;

namespace MCAROC_Analysis.Tests;

public sealed class BprLitigationReportParserTests
{
    [Fact]
    public void Parse_flattens_nested_cases_and_preserves_Csp_parties_and_orders()
    {
        const string json = """
        {
          "request_details":{"job_id":"job-1","report_date":"2026-09-20","keywords":["Test Company"]},
          "district_court":{"against":{"civil":{"disposed":{"data":[{
            "_id":"provider-1","csp_id":"csp-42","cnr_number":"TNKP070001332020","type":"district",
            "court":"Sub Judges, Kancheepuram","bench":"Not Found","case_no":"22/2020","case_type":"OS",
            "case_year":"2020","case_stage":"Trial","case_status":"DISPOSED","act":"Code of Civil Procedure O 7 R 1",
            "filing_date":"29-01-2020","last_hearing_date":"09-01-2025","decision_date":"09-01-2025",
            "state":"tamil nadu","district":"Kancheepuram","petitioners":[{"name":"M/S VGN"}],
            "respondents":[{"name":"S V Subramanian"}],"petitioner_advocates":["Y Thyagarajan"],
            "orders":[{"pdf_url":"https://example.test/order.pdf","order_date":"09-01-2025","order_type":"Judgment"}]
          }]}}}}
        }
        """;

        var report = BprLitigationReportParser.Parse(json);

        Assert.Equal("job-1", report.Request.JobId);
        Assert.Equal(["Test Company"], report.Request.Keywords);
        var item = Assert.Single(report.Cases);
        Assert.Equal("csp-42", item.CspId);
        Assert.Equal("TNKP070001332020", item.CnrNumber);
        Assert.Equal("district_court", item.CourtCategory);
        Assert.Equal("against", item.Direction);
        Assert.Equal("civil", item.CaseClassification);
        Assert.Contains("M/S VGN", item.PetitionersJson);
        var order = Assert.Single(item.Orders);
        Assert.Equal("Judgment", order.OrderType);
        Assert.Equal("https://example.test/order.pdf", order.PdfUrl);
    }

    [Fact]
    public void Parse_keeps_a_case_without_Cnr_or_orders()
    {
        const string json = """
        { "high_court": { "by": { "others": { "live": { "data": [
          { "case_no":"WP/1/2026", "csp_id":"csp-43", "court":"High Court", "case_status":"PENDING" }
        ] } } } } }
        """;

        var item = Assert.Single(BprLitigationReportParser.Parse(json).Cases);

        Assert.Null(item.CnrNumber);
        Assert.Equal("csp-43", item.CspId);
        Assert.Equal("by", item.Direction);
        Assert.Empty(item.Orders);
    }

    /// <summary>The tribunal sections of a real report (high_risk_court, then drt/nclt/nclat, then live/disposed) have no by/against level in their
    /// path, name the next hearing <c>next_hearing</c>, and say by or against on each case as <c>by_or_against</c>.</summary>
    [Fact]
    public void Parse_reads_next_hearing_and_by_or_against_from_a_tribunal_case_and_keeps_its_party_records()
    {
        const string json = """
        { "high_risk_court": { "drt": { "live": { "data": [{
            "_id":"62c8dfca44ad686ed8e3a252","csp_id":"csp-7","type":"drt","court":"Drt","bench":"debts recovery tribunal hyderabad(drt 1)",
            "case_no":"41","case_type":"SA-","case_year":"2021","case_status":"PENDING","filing_date":"2021-04-26","next_hearing":"2025-07-30",
            "by_or_against":"against","side":"other","state":"Telangana","district":"hyderabad",
            "petitioners":[{"name":"GURUSWAMY NANDA KUMAR","address":""}],"respondents":[{"name":"BANK OF MAHARASHTRA"},{"name":"M/S. COASTAL PROJECTS LIMITED"}],
            "orders":[{"pdf_url":"https://example.test/o.pdf","order_date":"2023-04-12","order_type":"order/judgement"}]
        }] } } } }
        """;

        var item = Assert.Single(BprLitigationReportParser.Parse(json).Cases);

        Assert.Equal("against", item.Direction);
        Assert.Equal("2025-07-30", item.NextHearingDate);
        Assert.Equal("high_risk_court", item.CourtCategory);
        Assert.Equal(["GURUSWAMY NANDA KUMAR"], LitigationPartyNames.Parse(item.PetitionersJson));
        Assert.Equal(["BANK OF MAHARASHTRA", "M/S. COASTAL PROJECTS LIMITED"], LitigationPartyNames.Parse(item.RespondentsJson));
    }

    [Theory]
    [InlineData("by", "by")]
    [InlineData("AGAINST", "against")]
    [InlineData(" Against ", "against")]
    [InlineData("other", null)]
    [InlineData("", null)]
    public void Parse_takes_only_by_or_against_from_the_case_field(string field, string? expected)
    {
        var json = "{ \"high_risk_court\": { \"drt\": { \"live\": { \"data\": [{ \"case_no\":\"1\", \"by_or_against\":\"" + field + "\" }] } } } }";

        Assert.Equal(expected, Assert.Single(BprLitigationReportParser.Parse(json).Cases).Direction);
    }

    [Fact]
    public void Parse_prefers_the_sections_by_against_path_and_the_next_hearing_date_field_when_both_are_present()
    {
        const string json = """
        { "district_court": { "by": { "civil": { "live": { "data": [{ "case_no":"1/2020", "by_or_against":"against",
            "next_hearing_date":"2026-01-01", "next_hearing":"2025-01-01" }] } } } } }
        """;

        var item = Assert.Single(BprLitigationReportParser.Parse(json).Cases);

        Assert.Equal("by", item.Direction);
        Assert.Equal("2026-01-01", item.NextHearingDate);
    }
}
