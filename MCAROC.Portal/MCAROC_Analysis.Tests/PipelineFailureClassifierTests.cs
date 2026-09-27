using MCAROC_Analysis.Services.Pipeline;

namespace MCAROC_Analysis.Tests;

/// <summary>#292 (plan §6.1): pure lookup from a stage's stable reason code to its failure class. Table
/// tests, plus the fail-closed default for anything unrecognised.</summary>
public sealed class PipelineFailureClassifierTests
{
    [Theory]
    [InlineData("FETCH_FAILED")]
    [InlineData("INGEST_FAILED")]
    [InlineData("ANALYSIS_FAILED")]
    [InlineData("FILINGS_FAILED")]
    [InlineData("FILINGS_FETCH_FAILED")]
    [InlineData("DOSSIER_RENDER_FAILED")]
    [InlineData("REFRESH_TIMEOUT")]
    [InlineData("MCA_MAINTENANCE")]
    [InlineData("TOOL_UNAVAILABLE")]
    [InlineData("LITIGATION_SEARCH_FAILED")]
    [InlineData("LITIGATION_ANALYSIS_FAILED")]
    [InlineData("LITIGATION_IMPORT_FAILED")]
    public void Transient_codes_are_auto_retryable(string code)
    {
        Assert.Equal(PipelineFailureClass.Transient, PipelineFailureClassifier.Classify(code));
        Assert.True(PipelineFailureClassifier.IsAutoRetryable(code));
    }

    [Theory]
    [InlineData("IDENTITY_MISMATCH", PipelineFailureClass.NeedsHumanData)]
    [InlineData("IDENTITY_FALSE_ACCEPT", PipelineFailureClass.NeedsHumanData)]
    [InlineData("CALC_GATE_HOLD", PipelineFailureClass.NeedsHumanData)]
    [InlineData("MANUAL_REVIEW_REQUIRED", PipelineFailureClass.NeedsHumanData)]
    [InlineData("DUPLICATE_REQUEST", PipelineFailureClass.NeedsHumanData)]
    [InlineData("STAGE_STALLED", PipelineFailureClass.NeedsHumanData)]
    [InlineData("ORDER_DOWNLOAD_STALLED", PipelineFailureClass.NeedsHumanData)]
    [InlineData("RETRIES_EXHAUSTED", PipelineFailureClass.NeedsHumanData)]
    [InlineData("UNLOCK_APPROVAL_REQUIRED", PipelineFailureClass.ApprovalGated)]
    [InlineData("UNLOCK_EXPIRED", PipelineFailureClass.ApprovalGated)]
    [InlineData("IDENTITY_AMBIGUOUS", PipelineFailureClass.AmbiguousIdentity)]
    [InlineData("IDENTITY_NOT_FOUND", PipelineFailureClass.AmbiguousIdentity)]
    [InlineData("AUTH_REJECTED", PipelineFailureClass.NeedsHumanConfig)]
    [InlineData("INTEGRATION_NOT_CONFIGURED", PipelineFailureClass.NeedsHumanConfig)]
    [InlineData("LITIGATION_NOT_CONFIGURED", PipelineFailureClass.NeedsHumanConfig)]
    [InlineData("CONTRACT_CHANGED", PipelineFailureClass.NeedsDeveloper)]
    public void Non_transient_codes_classify_correctly_and_are_never_auto_retryable(string code, PipelineFailureClass expected)
    {
        Assert.Equal(expected, PipelineFailureClassifier.Classify(code));
        Assert.False(PipelineFailureClassifier.IsAutoRetryable(code));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SOME_FUTURE_CODE_NOBODY_HAS_CLASSIFIED_YET")]
    public void Unrecognised_or_missing_codes_fail_closed_to_needs_human_data(string? code)
    {
        Assert.Equal(PipelineFailureClass.NeedsHumanData, PipelineFailureClassifier.Classify(code));
        Assert.False(PipelineFailureClassifier.IsAutoRetryable(code));
    }
}
