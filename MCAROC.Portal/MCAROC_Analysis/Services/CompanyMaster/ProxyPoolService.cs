using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MCAROC_Analysis.Services.CompanyMaster;

public record ProxyNode(string Endpoint, string? Username, string? Password, string SanitizedAlias)
{
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public long LastLatencyMs { get; set; }
    public bool IsHealthy => FailureCount < 5;
}

public interface IProxyPoolService
{
    IReadOnlyList<ProxyNode> GetAllNodes();
    ProxyNode? GetNextHealthyNode();
    void RecordOutcome(ProxyNode node, bool succeeded, long latencyMs);
    Task<bool> TestNodeAsync(ProxyNode node, CancellationToken cancellationToken = default);
    string Sanitize(string? rawProxy);
}

public sealed class ProxyPoolService : IProxyPoolService
{
    private readonly List<ProxyNode> _nodes = new();
    private readonly object _lock = new();
    private int _currentIndex;
    private readonly ILogger<ProxyPoolService> _logger;

    public ProxyPoolService(IConfiguration configuration, ILogger<ProxyPoolService> logger)
    {
        _logger = logger;
        LoadConfiguredProxies(configuration);
    }

    private void LoadConfiguredProxies(IConfiguration configuration)
    {
        // 1. Check explicit file path from config or fallback
        string? proxyFile = configuration["CompanyMasterSync:ProxyListFile"];
        if (string.IsNullOrWhiteSpace(proxyFile) || !File.Exists(proxyFile))
        {
            const string fallbackPath = @"E:\Downloads\Gujarat_SRO_Record_Finder-20260627T032348Z-3-001\Gujarat_SRO_Record_Finder\iproyal-proxies.txt";
            if (File.Exists(fallbackPath))
            {
                proxyFile = fallbackPath;
            }
        }

        if (proxyFile != null && File.Exists(proxyFile))
        {
            try
            {
                foreach (var line in File.ReadAllLines(proxyFile))
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#')) continue;

                    var node = ParseProxyString(trimmed);
                    if (node != null)
                    {
                        _nodes.Add(node);
                    }
                }
                _logger.LogInformation("Loaded {Count} proxy nodes from {File}", _nodes.Count, proxyFile);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load proxies from file {File}", proxyFile);
            }
        }

        // 2. Also check environment variable for dedicated proxy
        string? envProxy = Environment.GetEnvironmentVariable("COMPANY_MASTER_PROXY_SECRET");
        if (!string.IsNullOrWhiteSpace(envProxy))
        {
            var node = ParseProxyString(envProxy);
            if (node != null && !_nodes.Exists(n => n.SanitizedAlias == node.SanitizedAlias))
            {
                _nodes.Add(node);
            }
        }
    }

    public static ProxyNode? ParseProxyString(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            // Format 1: http(s)://user:pass@host:port or http(s)://host:port
            if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(raw);
                string? user = null;
                string? pass = null;
                if (!string.IsNullOrWhiteSpace(uri.UserInfo))
                {
                    var userParts = uri.UserInfo.Split(':');
                    user = userParts[0];
                    if (userParts.Length > 1) pass = userParts[1];
                }
                string endpoint = $"{uri.Scheme}://{uri.Host}:{uri.Port}";
                string alias = user != null ? $"{user}@{uri.Host}:{uri.Port}" : $"{uri.Host}:{uri.Port}";
                return new ProxyNode(endpoint, user, pass, alias);
            }

            // Format 2: host:port:user:pass
            var parts = raw.Split(':');
            if (parts.Length == 4)
            {
                string host = parts[0];
                string port = parts[1];
                string user = parts[2];
                string pass = parts[3];
                string endpoint = $"http://{host}:{port}";
                string alias = $"{user}@{host}:{port}";
                return new ProxyNode(endpoint, user, pass, alias);
            }

            // Format 3: host:port
            if (parts.Length == 2)
            {
                string endpoint = $"http://{parts[0]}:{parts[1]}";
                return new ProxyNode(endpoint, null, null, $"{parts[0]}:{parts[1]}");
            }
        }
        catch
        {
            // invalid string
        }
        return null;
    }

    public string Sanitize(string? rawProxy)
    {
        if (string.IsNullOrWhiteSpace(rawProxy)) return "Direct";
        var node = ParseProxyString(rawProxy);
        return node?.SanitizedAlias ?? "RedactedProxy";
    }

    public IReadOnlyList<ProxyNode> GetAllNodes()
    {
        lock (_lock)
        {
            return _nodes.ToArray();
        }
    }

    public ProxyNode? GetNextHealthyNode()
    {
        lock (_lock)
        {
            if (_nodes.Count == 0) return null;

            for (int i = 0; i < _nodes.Count; i++)
            {
                int index = (_currentIndex + i) % _nodes.Count;
                var candidate = _nodes[index];
                if (candidate.IsHealthy)
                {
                    _currentIndex = (index + 1) % _nodes.Count;
                    return candidate;
                }
            }

            // If all exceeded failure threshold, return the least failing node
            _currentIndex = (_currentIndex + 1) % _nodes.Count;
            return _nodes[_currentIndex];
        }
    }

    public void RecordOutcome(ProxyNode node, bool succeeded, long latencyMs)
    {
        lock (_lock)
        {
            node.LastLatencyMs = latencyMs;
            if (succeeded)
            {
                node.SuccessCount++;
                if (node.FailureCount > 0) node.FailureCount--;
            }
            else
            {
                node.FailureCount++;
            }
        }
    }

    public async Task<bool> TestNodeAsync(ProxyNode node, CancellationToken cancellationToken = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy(node.Endpoint)
                {
                    Credentials = node.Username != null ? new NetworkCredential(node.Username, node.Password) : null
                },
                ConnectTimeout = TimeSpan.FromSeconds(10)
            };

            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            var response = await client.GetAsync("https://mcacdm.nic.in/company-master-details", cancellationToken);
            sw.Stop();
            bool ok = response.IsSuccessStatusCode;
            RecordOutcome(node, ok, sw.ElapsedMilliseconds);
            return ok;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogDebug(ex, "Proxy test failed for {Alias}", node.SanitizedAlias);
            RecordOutcome(node, false, sw.ElapsedMilliseconds);
            return false;
        }
    }
}
