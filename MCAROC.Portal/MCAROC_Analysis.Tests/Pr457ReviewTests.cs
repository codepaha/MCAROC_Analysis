using System.Net;
using MCAROC_Analysis.Services.LitigationModule.Sources.Drt;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class Pr457ReviewTests
{
    private static DrtClient Client(HttpMessageHandler handler, int retries = 1) => new(new HttpClient(handler),
        Options.Create(new DrtClientOptions { RequestsPerSecond = 0, MaxRetries = retries }), NullLogger<DrtClient>.Instance);

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Http_failure_is_not_evidence_of_missing_case(HttpStatusCode status)
    {
        var client = Client(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status)
            { Content = new StringContent("gateway failure") })));
        var result = await client.GetCaseByNumberAsync(1, 1, "11", 2025);
        Assert.NotEqual(DrtLookupStatus.NotFoundCandidate, result.Status);
    }

    [Fact]
    public async Task Retry_resends_the_form_body_using_real_HttpClient_pipeline()
    {
        var bodies = new List<string>();
        var client = Client(new Handler(async (req, ct) =>
        {
            bodies.Add(await req.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(bodies.Count == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent("[{\"caseType\":1,\"caseTypeName\":\"OA\"}]") };
        }), 2);
        var types = await client.GetCaseTypesAsync(1);
        Assert.Single(types);
        Assert.Equal(2, bodies.Count);
        Assert.Equal(bodies[0], bodies[1]);
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated_instead_of_not_found()
    {
        using var cts = new CancellationTokenSource();
        var client = Client(new Handler((_, ct) =>
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetCaseByNumberAsync(1, 1, "11", 2025, cts.Token));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}

