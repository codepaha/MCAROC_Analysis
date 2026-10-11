using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using MCAROC_Analysis.Services.LitigationModule.Sources.Drt;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class Pr457TransportReviewTests
{
    [Fact]
    public async Task Actual_distinct_proxies_have_distinct_recorded_exits()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var a = new TcpListener(IPAddress.Loopback, 0);
        var b = new TcpListener(IPAddress.Loopback, 0);
        a.Start(); b.Start();
        string Url(TcpListener l) => $"http://127.0.0.1:{((IPEndPoint)l.LocalEndpoint).Port}";
        async Task Serve(TcpListener listener)
        {
            using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
            var stream = connection.GetStream();
            var buffer = new byte[4096];
            await stream.ReadAsync(buffer, deadline.Token);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), deadline.Token);
        }
        var servers = Task.WhenAll(Serve(a), Serve(b));
        try
        {
            var options = new DrtClientOptions { BaseUrl = "http://court-review.invalid/", RequestsPerSecond = 0,
                MaxRetries = 1, ProxyPool = [Url(a), Url(b)], ProxyRotationFrequency = 1 };
            using var http = DrtClient.CreateHttpClient(options);
            var client = new DrtClient(http, Options.Create(options), NullLogger<DrtClient>.Instance);
            var first = await client.GetCaseByNumberAsync(1, 1, "11", 2025, deadline.Token);
            var second = await client.GetCaseByNumberAsync(1, 1, "11", 2025, deadline.Token);
            await servers;
            Assert.True(first.IsNotFoundCandidate && second.IsNotFoundCandidate);
            Assert.True(DrtCaseLookupResult.CanConfirmNotFound(first, second),
                $"Two real proxy servers handled the calls, but recorded exits were {first.ExitAddress} and {second.ExitAddress}.");
        }
        finally { a.Stop(); b.Stop(); }
    }

    [Fact]
    public async Task Retry_after_300_seconds_is_not_shortened_to_60_seconds()
    {
        var clock = new RecordingClock();
        using var http = new HttpClient();
        var client = new DrtClient(http, Options.Create(new DrtClientOptions()), NullLogger<DrtClient>.Instance, clock);
        using var cancel = new CancellationTokenSource();
        var method = typeof(DrtClient).GetMethod("BackoffAsync", BindingFlags.NonPublic | BindingFlags.Instance,
            null, [typeof(int), typeof(TimeSpan?), typeof(CancellationToken)], null)!;
        var pending = (Task)method.Invoke(client, [1, TimeSpan.FromSeconds(300), cancel.Token])!;
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(TimeSpan.FromSeconds(300), clock.Delay);
    }

    private sealed class RecordingClock : TimeProvider
    {
        public TimeSpan Delay { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Delay = dueTime;
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }
}
