using MCAROC_Analysis.Services.LitigationData;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class LitigationResilienceTests
{
    [Fact]
    public void The_breaker_trips_after_a_run_of_failed_calls_and_one_good_reply_starts_the_count_again()
    {
        var breaker = new LitigationOutageBreaker(3);
        breaker.Record(true); breaker.Record(true);
        Assert.False(breaker.Tripped);
        breaker.Record(false);
        breaker.Record(true); breaker.Record(true);
        Assert.False(breaker.Tripped);
        breaker.Record(true);
        Assert.True(breaker.Tripped);
    }

    [Fact]
    public void A_breaker_with_no_threshold_never_trips()
    {
        var breaker = new LitigationOutageBreaker(0);
        for (var i = 0; i < 100; i++) breaker.Record(true);
        Assert.False(breaker.Tripped);
    }

    [Theory]
    [InlineData("Error 429: Resource has been exhausted (RESOURCE_EXHAUSTED)")]
    [InlineData("503 Service Unavailable")]
    [InlineData("The model is overloaded. Please try again later.")]
    [InlineData("DEADLINE_EXCEEDED")]
    public void Quota_and_server_errors_are_worth_retrying(string message) =>
        Assert.True(VertexLitigationAiAnalysisClient.IsTransient(new Exception(message)));

    [Fact]
    public void Network_faults_are_worth_retrying() =>
        Assert.True(VertexLitigationAiAnalysisClient.IsTransient(new HttpRequestException("connection reset")));

    [Theory]
    [InlineData("400 Bad request: invalid argument")]
    [InlineData("403 Permission denied")]
    [InlineData("The prompt was blocked by safety settings")]
    public void Errors_that_would_fail_the_same_way_again_are_not(string message) =>
        Assert.False(VertexLitigationAiAnalysisClient.IsTransient(new Exception(message)));
}
