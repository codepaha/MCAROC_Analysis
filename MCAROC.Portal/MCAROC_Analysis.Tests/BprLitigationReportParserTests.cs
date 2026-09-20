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
}
