using System.Net;
using System.Net.Sockets;
using System.Text;
using MCAROC_Analysis.Services.LitigationModule.Sources.Drt;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class Pr457ProxyReviewTests
{
    [Fact]
    public async Task Same_direct_transport_cannot_be_confirmed_as_two_independent_exits()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var directConnections = 0;
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
                Interlocked.Increment(ref directConnections);
                var stream = connection.GetStream();
                var buffer = new byte[4096];
                await stream.ReadAsync(buffer, deadline.Token);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), deadline.Token);
            }
        });
        try
        {
            using var http = new HttpClient();
            var options = new DrtClientOptions
            {
                BaseUrl = $"http://127.0.0.1:{port}/", RequestsPerSecond = 0, MaxRetries = 1,
                ProxyPool = ["http://127.0.0.1:1", "http://127.0.0.1:2"], ProxyRotationFrequency = 1
            };
            var client = new DrtClient(http, Options.Create(options), NullLogger<DrtClient>.Instance);
            var first = await client.GetCaseByNumberAsync(1, 1, "11", 2025, deadline.Token);
            var second = await client.GetCaseByNumberAsync(1, 1, "11", 2025, deadline.Token);
            await server;
            Assert.Equal(2, directConnections);
            Assert.False(DrtCaseLookupResult.CanConfirmNotFound(first, second),
                $"Both calls went directly to the same loopback server but were labelled {first.ExitAddress} and {second.ExitAddress}.");
        }
        finally { listener.Stop(); }
    }
}

