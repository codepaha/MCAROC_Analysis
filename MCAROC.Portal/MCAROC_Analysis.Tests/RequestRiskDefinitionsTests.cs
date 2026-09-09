using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Dashboard;

namespace MCAROC_Analysis.Tests;

/// <summary>Tests RequestRiskDefinitions.IsProcessing/IsAttentionRequired by .Compile()-ing the Expression
/// and evaluating it against plain in-memory RequestSummaryRow objects — no database needed, since these
/// are Expression&lt;Func&lt;...&gt;&gt; specifically so they're composable in EF *and* testable this way.</summary>
public class RequestRiskDefinitionsTests
{
    private static readonly Func<RequestSummaryRow, bool> IsProcessing = RequestRiskDefinitions.IsProcessing.Compile();
    private static readonly Func<RequestSummaryRow, bool> IsAttentionRequired = RequestRiskDefinitions.IsAttentionRequired.Compile();

    private static RequestSummaryRow Row(
        RequestStatus status = RequestStatus.DataExtracted,
        bool manualReview = false,
        ReviewPriority? priority = null,
        FilingBatchStatus? filingBatchStatus = null) => new()
    {
        Request = new McaRequest { RequestStatus = status, IsManualReviewRequired = manualReview },
        LatestReviewPriority = priority,
        LatestFilingBatchStatus = filingBatchStatus
    };

    [Theory]
    [InlineData(RequestStatus.Created)]
    [InlineData(RequestStatus.DocumentsUploaded)]
    [InlineData(RequestStatus.Validating)]
    [InlineData(RequestStatus.ExtractionInProgress)]
    [InlineData(RequestStatus.DataExtracted)]
    [InlineData(RequestStatus.AiAnalysisInProgress)]
    public void IsProcessing_TrueForEveryProcessingRequestStatus(RequestStatus status)
    {
        Assert.True(IsProcessing(Row(status: status)));
    }

    [Theory]
    [InlineData(RequestStatus.AnalysisCompleted)]
    [InlineData(RequestStatus.ValidationFailed)]
    [InlineData(RequestStatus.ExtractionFailed)]
    [InlineData(RequestStatus.AiAnalysisFailed)]
    [InlineData(RequestStatus.Cancelled)]
    public void IsProcessing_FalseForTerminalRequestStatus_WithNoActiveFilingBatch(RequestStatus status)
    {
        Assert.False(IsProcessing(Row(status: status)));
    }

    [Fact]
    public void IsProcessing_TrueWhenRequestStatusIsTerminalButFilingBatchIsStillActive()
    {
        // The core gap this row exists to close: RequestStatus alone would miss a request whose Excel/
        // analysis pipeline finished but whose optional PDF archive is still being processed.
        var row = Row(status: RequestStatus.AnalysisCompleted, filingBatchStatus: FilingBatchStatus.Processing);

        Assert.True(IsProcessing(row));
    }

    [Fact]
    public void IsProcessing_FalseWhenFilingBatchIsCompletedOrFailed()
    {
        Assert.False(IsProcessing(Row(status: RequestStatus.AnalysisCompleted, filingBatchStatus: FilingBatchStatus.Completed)));
        Assert.False(IsProcessing(Row(status: RequestStatus.AnalysisCompleted, filingBatchStatus: FilingBatchStatus.Failed)));
    }

    [Fact]
    public void IsAttentionRequired_TrueForManualReviewAlone()
    {
        Assert.True(IsAttentionRequired(Row(manualReview: true)));
    }

    [Theory]
    [InlineData(RequestStatus.ExtractionFailed)]
    [InlineData(RequestStatus.ValidationFailed)]
    [InlineData(RequestStatus.AiAnalysisFailed)]
    public void IsAttentionRequired_TrueForEachFailedStatusAlone(RequestStatus status)
    {
        Assert.True(IsAttentionRequired(Row(status: status)));
    }

    [Fact]
    public void IsAttentionRequired_FalseForCancelledAlone()
    {
        // Documented assumption: a deliberate cancellation isn't a thing to review, unlike a failure.
        Assert.False(IsAttentionRequired(Row(status: RequestStatus.Cancelled)));
    }

    [Fact]
    public void IsAttentionRequired_TrueForHighPriorityAlone()
    {
        Assert.True(IsAttentionRequired(Row(priority: ReviewPriority.High)));
    }

    [Fact]
    public void IsAttentionRequired_FalseForMediumOrLowPriorityAlone()
    {
        Assert.False(IsAttentionRequired(Row(priority: ReviewPriority.Medium)));
        Assert.False(IsAttentionRequired(Row(priority: ReviewPriority.Low)));
    }

    [Fact]
    public void IsAttentionRequired_TrueForFailedFilingBatchAlone()
    {
        Assert.True(IsAttentionRequired(Row(filingBatchStatus: FilingBatchStatus.Failed)));
    }

    [Fact]
    public void IsAttentionRequired_FalseWhenNothingApplies()
    {
        Assert.False(IsAttentionRequired(Row()));
    }
}
