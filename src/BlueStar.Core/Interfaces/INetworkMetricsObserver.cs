using System;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Observes and records outgoing network request telemetry, tracking cache hits, in-flight reuse, and status without exposing credentials.
/// </summary>
public interface INetworkMetricsObserver
{
    /// <summary>
    /// Records an HTTP or service network event.
    /// </summary>
    void RecordEvent(string provider, string endpoint, string key, bool cacheHit, bool inFlightReused, int statusCode, long durationMs);

    /// <summary>
    /// Gets the total number of physical network requests executed.
    /// </summary>
    int TotalNetworkRequests { get; }

    /// <summary>
    /// Gets the total number of cache hits intercepted before network dispatch.
    /// </summary>
    int TotalCacheHits { get; }

    /// <summary>
    /// Gets the total number of cache misses requiring network retrieval.
    /// </summary>
    int TotalCacheMisses => TotalNetworkRequests;

    /// <summary>
    /// Gets the total number of in-flight deduplications (reused existing task).
    /// </summary>
    int TotalInFlightReused { get; }

    /// <summary>
    /// Gets the total number of negative-cache hits (preventing redundant 404 lookups).
    /// </summary>
    int TotalNegativeCacheHits { get; }

    /// <summary>
    /// Gets the total number of failed requests (HTTP 4xx/5xx or transport errors).
    /// </summary>
    int TotalFailures { get; }

    /// <summary>
    /// Gets the average physical request latency in milliseconds.
    /// </summary>
    double AverageLatencyMs { get; }

    /// <summary>
    /// Gets the number of requests dispatched for a specific provider.
    /// </summary>
    int GetRequestCountForProvider(string provider);

    /// <summary>
    /// Resets all counters for test isolation.
    /// </summary>
    void Reset();

    /// <summary>
    /// Convenience method to record a cache hit before network dispatch.
    /// </summary>
    void OnCacheHit(string provider, string endpoint)
    {
        RecordEvent(provider, endpoint, string.Empty, cacheHit: true, inFlightReused: false, statusCode: 200, durationMs: 0);
    }

    /// <summary>
    /// Convenience method to record a negative cache hit (e.g. cached 404).
    /// </summary>
    void OnNegativeCacheHit(string provider, string endpoint)
    {
        RecordEvent(provider, endpoint, string.Empty, cacheHit: true, inFlightReused: false, statusCode: 404, durationMs: 0);
    }

    /// <summary>
    /// Convenience method to record an outgoing network request dispatched to a provider.
    /// </summary>
    void OnProviderRequest(string provider, string endpoint)
    {
        RecordEvent(provider, endpoint, string.Empty, cacheHit: false, inFlightReused: false, statusCode: 200, durationMs: 0);
    }
}
