using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading;
using BlueStar.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Thread-safe observer and recorder for network operations and cache telemetry.
/// Logs structured HTTP events while rigorously redacting secrets, query tokens, and credentials.
/// </summary>
public sealed class NetworkMetricsObserver : INetworkMetricsObserver
{
    private readonly ILogger<NetworkMetricsObserver>? _logger;
    private int _totalNetworkRequests;
    private int _totalCacheHits;
    private int _totalInFlightReused;
    private int _totalNegativeCacheHits;
    private int _totalFailures;
    private long _totalDurationMs;
    private readonly ConcurrentDictionary<string, int> _providerRequests = new(StringComparer.OrdinalIgnoreCase);

    // Regex to sanitize any token or api key appearing in URLs
    private static readonly Regex TokenSanitizerRegex = new(
        @"(key|token|auth|secret|api_key|password)=[^&]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public NetworkMetricsObserver(ILogger<NetworkMetricsObserver>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public int TotalNetworkRequests => Volatile.Read(ref _totalNetworkRequests);

    /// <inheritdoc />
    public int TotalCacheHits => Volatile.Read(ref _totalCacheHits);

    /// <inheritdoc />
    public int TotalCacheMisses => TotalNetworkRequests;

    /// <inheritdoc />
    public int TotalInFlightReused => Volatile.Read(ref _totalInFlightReused);

    /// <inheritdoc />
    public int TotalNegativeCacheHits => Volatile.Read(ref _totalNegativeCacheHits);

    /// <inheritdoc />
    public int TotalFailures => Volatile.Read(ref _totalFailures);

    /// <inheritdoc />
    public double AverageLatencyMs
    {
        get
        {
            var reqs = Volatile.Read(ref _totalNetworkRequests);
            var dur = Volatile.Read(ref _totalDurationMs);
            return reqs > 0 ? (double)dur / reqs : 0.0;
        }
    }

    /// <inheritdoc />
    public int GetRequestCountForProvider(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return 0;
        return _providerRequests.TryGetValue(provider, out var count) ? count : 0;
    }

    /// <inheritdoc />
    public void RecordEvent(string provider, string endpoint, string key, bool cacheHit, bool inFlightReused, int statusCode, long durationMs)
    {
        if (cacheHit)
        {
            Interlocked.Increment(ref _totalCacheHits);
            if (statusCode == 404)
            {
                Interlocked.Increment(ref _totalNegativeCacheHits);
            }
        }
        else if (inFlightReused)
        {
            Interlocked.Increment(ref _totalInFlightReused);
        }
        else
        {
            Interlocked.Increment(ref _totalNetworkRequests);
            if (durationMs > 0)
            {
                Interlocked.Add(ref _totalDurationMs, durationMs);
            }
            if (statusCode >= 400 || statusCode == 0)
            {
                Interlocked.Increment(ref _totalFailures);
            }
            _providerRequests.AddOrUpdate(provider, 1, (_, current) => current + 1);
        }

        var sanitizedEndpoint = SanitizeEndpoint(endpoint);
        var sanitizedKey = SanitizeEndpoint(key);

        if (cacheHit)
        {
            _logger?.LogInformation("[HTTP] Provider={Provider} Endpoint={Endpoint} Key={Key} Cache=HIT Status={Status} Request=SKIPPED",
                provider, sanitizedEndpoint, sanitizedKey, statusCode);
        }
        else if (inFlightReused)
        {
            _logger?.LogInformation("[HTTP] Provider={Provider} Endpoint={Endpoint} Key={Key} InFlight=REUSED Request=JOINED",
                provider, sanitizedEndpoint, sanitizedKey);
        }
        else
        {
            _logger?.LogInformation("[HTTP] Provider={Provider} Endpoint={Endpoint} Key={Key} Cache=MISS InFlight=NEW Status={Status} Duration={Duration}ms",
                provider, sanitizedEndpoint, sanitizedKey, statusCode, durationMs);
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        Interlocked.Exchange(ref _totalNetworkRequests, 0);
        Interlocked.Exchange(ref _totalCacheHits, 0);
        Interlocked.Exchange(ref _totalInFlightReused, 0);
        Interlocked.Exchange(ref _totalNegativeCacheHits, 0);
        Interlocked.Exchange(ref _totalFailures, 0);
        Interlocked.Exchange(ref _totalDurationMs, 0);
        _providerRequests.Clear();
    }

    public void OnCacheHit(string provider, string endpoint)
    {
        RecordEvent(provider, endpoint, string.Empty, cacheHit: true, inFlightReused: false, statusCode: 200, durationMs: 0);
    }

    public void OnProviderRequest(string provider, string endpoint)
    {
        RecordEvent(provider, endpoint, string.Empty, cacheHit: false, inFlightReused: false, statusCode: 200, durationMs: 0);
    }

    private static string SanitizeEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return string.Empty;
        return TokenSanitizerRegex.Replace(endpoint, "$1=[REDACTED]");
    }
}
