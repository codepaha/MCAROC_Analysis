using System.Net;
using System.Net.Sockets;
using System.Text;
using MCAROC_Analysis.Services.LitigationModule.Sources.Drt;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class Pr457ConcurrentExitReviewTests
{
    [Fact]
    public async Task Overlapping_requests_keep_their_own_actual_proxy_exit()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var fast = new TcpListener(IPAddress.Loopback, 0);
        var slow = new TcpListener(IPAddress.Loopback, 0);
        fast.Start(); slow.Start();
        string Url(TcpListener l) => $"http://127.0.0.1:{((IPEndPoint)l.LocalEndpoint).Port}";
        var receivedSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Serve(TcpListener listener, bool pause)
        {
            using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
            var stream = connection.GetStream();
            var buffer = new byte[4096];
            await stream.ReadAsync(buffer, deadline.Token);
            if (pause)
            {
                receivedSlow.TrySetResult();
                await releaseSlow.Task.WaitAsync(deadline.Token);
            }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), deadline.Token);
        }
        var slowServer = Serve(slow, true);
        var fastServer = Serve(fast, false);
        try
        {
            var options = new DrtClientOptions { BaseUrl = "http://concurrent-court-review.invalid/", RequestsPerSecond = 0,
                MaxRetries = 1, ProxyPool = [Url(fast), Url(slow)], ProxyRotationFrequency = 1 };
            using var http = DrtClient.CreateHttpClient(options);
            var client = new DrtClient(http, Options.Create(options), NullLogger<DrtClient>.Instance);
            var firstTask = client.GetCaseByNumberAsync(1, 1, "11", 2025, deadline.Token);
            await receivedSlow.Task.WaitAsync(deadline.Token);
            var second = await client.GetCaseByNumberAsync(1, 1, "12", 2025, deadline.Token);
            await fastServer;
            releaseSlow.TrySetResult();
            var first = await firstTask;
            await slowServer;
            Assert.Equal(Url(fast), second.ExitAddress);
            Assert.Equal(Url(slow), first.ExitAddress);
        }
        finally { releaseSlow.TrySetResult(); fast.Stop(); slow.Stop(); }
    }
}
