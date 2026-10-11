using System.Net;
using System.Net.Sockets;
using System.Text;
using MCAROC_Analysis.Services.LitigationModule.Sources.Drt;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class Pr457RouteSelectionReviewTests
{
    [Fact]
    public async Task Saved_exit_matches_actual_route_when_transport_selection_is_delayed()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var used = new TcpListener(IPAddress.Loopback, 0);
        var unused = new TcpListener(IPAddress.Loopback, 0);
        used.Start(); unused.Start();
        string Url(TcpListener l) => $"http://127.0.0.1:{((IPEndPoint)l.LocalEndpoint).Port}";
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var connection = await used.AcceptTcpClientAsync(deadline.Token);
                var stream = connection.GetStream();
                var buffer = new byte[4096];
                await stream.ReadAsync(buffer, deadline.Token);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), deadline.Token);
            }
        });
        try
        {
            var options = new DrtClientOptions { BaseUrl = "http://route-selection-review.invalid/", RequestsPerSecond = 0,
                MaxRetries = 1, ProxyPool = [Url(used), Url(unused)], ProxyRotationFrequency = 1 };
            using var http = new HttpClient(new PauseFirstHandler(arrived, release, DrtClient.CreateHandler(options)));
            var client = new DrtClient(http, Options.Create(options), NullLogger<DrtClient>.Instance);
            var firstTask = client.GetCaseByNumberAsync(1, 1, "11", 2025, deadline.Token);
            await arrived.Task.WaitAsync(deadline.Token);
            var second = await client.GetCaseByNumberAsync(1, 1, "12", 2025, deadline.Token);
            release.TrySetResult();
            var first = await firstTask;
            await server;
            Assert.True(first.IsNotFoundCandidate && second.IsNotFoundCandidate);
            Assert.False(DrtCaseLookupResult.CanConfirmNotFound(first, second),
                $"Both requests reached {Url(used)} but were labelled {first.ExitAddress} and {second.ExitAddress}.");
        }
        finally { release.TrySetResult(); used.Stop(); unused.Stop(); }
    }

    private sealed class PauseFirstHandler(TaskCompletionSource arrived, TaskCompletionSource release, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private int _calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                arrived.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
