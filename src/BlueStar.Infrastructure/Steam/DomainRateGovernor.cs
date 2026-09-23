using System;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace BlueStar.Infrastructure.Steam;

/// <summary>
/// Circuit breaker operational state.
/// </summary>
public enum CircuitState
{
    /// <summary>Normal operation: requests pass through freely according to rate limits.</summary>
    Closed,

    /// <summary>Tripped: requests fail fast without hitting the remote server.</summary>
    Open,

    /// <summary>Trial: a single probe request is allowed to test service recovery.</summary>
    HalfOpen
}

/// <summary>
/// Domain-isolated rate governor and circuit breaker ensuring separate quotas for
/// store.steampowered.com and api.steampowered.com, with exponential backoff and jitter.
/// </summary>
public sealed class DomainRateGovernor
{
    public const string StoreDomain = "store.steampowered.com";
    public const string ApiDomain = "api.steampowered.com";

    private static readonly ConcurrentDictionary<string, DomainGovernorBucket> Buckets = new(StringComparer.OrdinalIgnoreCase);

    public static DomainGovernorBucket Store => GetBucket(StoreDomain, TimeSpan.FromMilliseconds(1500));
    public static DomainGovernorBucket Api => GetBucket(ApiDomain, TimeSpan.FromMilliseconds(500));

    public static DomainGovernorBucket GetBucket(string domain, TimeSpan? minInterval = null)
    {
        return Buckets.GetOrAdd(domain, d => new DomainGovernorBucket(d, minInterval ?? TimeSpan.FromSeconds(1)));
    }
}

/// <summary>
/// Rate limiting and circuit breaker bucket for a specific remote authority/domain.
/// </summary>
public sealed class DomainGovernorBucket
{
    private readonly string _domain;
    private readonly TimeSpan _minInterval;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _circuitLock = new();

    private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;
    private DateTimeOffset _openUntil = DateTimeOffset.MinValue;
    private int _consecutiveFailures;
    private int _consecutiveSuccesses;
    private int _pendingInteractive;
    private bool _halfOpenProbeInFlight;

    public string Domain => _domain;

    public CircuitState State
    {
        get
        {
            lock (_circuitLock)
            {
                var now = DateTimeOffset.UtcNow;
                if (_openUntil > now)
                    return CircuitState.Open;

                if (_consecutiveFailures >= 3 && _consecutiveSuccesses == 0)
                    return CircuitState.HalfOpen;

                return CircuitState.Closed;
            }
        }
    }

    public DateTimeOffset CooldownUntil
    {
        get
        {
            lock (_circuitLock) return _openUntil;
        }
    }

    public DomainGovernorBucket(string domain, TimeSpan minInterval)
    {
        _domain = domain;
        _minInterval = minInterval;
    }

    /// <summary>
    /// Acquires a slot to make a request to this domain.
    /// Returns null if the circuit is Open and the request should fail-fast or use local cache.
    /// </summary>
    public async Task<IDisposable?> AcquireAsync(SteamRequestPriority priority, CancellationToken ct = default)
    {
        var state = State;
        if (state == CircuitState.Open)
        {
            return null; // Fast-fail
        }

        if (state == CircuitState.HalfOpen)
        {
            lock (_circuitLock)
            {
                if (_halfOpenProbeInFlight) return null; // Only one probe allowed in HalfOpen
                _halfOpenProbeInFlight = true;
            }
        }

        if (priority == SteamRequestPriority.Interactive)
        {
            Interlocked.Increment(ref _pendingInteractive);
        }

        try
        {
            // Background yields to interactive requests
            if (priority == SteamRequestPriority.Background)
            {
                while (Volatile.Read(ref _pendingInteractive) > 0)
                {
                    if (State == CircuitState.Open) return null;
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
            }

            await _gate.WaitAsync(ct).ConfigureAwait(false);

            if (State == CircuitState.Open)
            {
                _gate.Release();
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            var wait = _nextAllowed - now;
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }
                catch
                {
                    _gate.Release();
                    throw;
                }
            }

            // Apply randomized jitter (+/- 15%) to avoid resonance
            var jitterMs = RandomNumberGenerator.GetInt32(-150, 151);
            var nextInterval = _minInterval + TimeSpan.FromMilliseconds(jitterMs);
            if (nextInterval < TimeSpan.FromMilliseconds(100)) nextInterval = TimeSpan.FromMilliseconds(100);

            _nextAllowed = DateTimeOffset.UtcNow + nextInterval;

            return new BucketLease(this, priority);
        }
        catch
        {
            if (priority == SteamRequestPriority.Interactive)
            {
                Interlocked.Decrement(ref _pendingInteractive);
            }
            throw;
        }
    }

    /// <summary>
    /// Reports an HTTP block (e.g. 429 Too Many Requests or 403 Forbidden).
    /// Trips the circuit breaker with exponential backoff and jitter.
    /// </summary>
    public void ReportFailure(HttpStatusCode status)
    {
        lock (_circuitLock)
        {
            _halfOpenProbeInFlight = false;
            _consecutiveSuccesses = 0;
            _consecutiveFailures++;

            var baseCooldown = TimeSpan.FromMinutes(2);
            var multiplier = Math.Pow(2, Math.Min(_consecutiveFailures - 1, 4)); // max 32 mins
            var cooldown = TimeSpan.FromTicks((long)(baseCooldown.Ticks * multiplier));

            // Jitter +/- 20%
            var jitterFactor = 0.8 + (RandomNumberGenerator.GetInt32(0, 41) / 100.0);
            cooldown = TimeSpan.FromTicks((long)(cooldown.Ticks * jitterFactor));

            _openUntil = DateTimeOffset.UtcNow + cooldown;
        }
    }

    /// <summary>
    /// Reports a successful request, resetting the circuit breaker to Closed.
    /// </summary>
    public void ReportSuccess()
    {
        lock (_circuitLock)
        {
            _consecutiveFailures = 0;
            _consecutiveSuccesses++;
            _openUntil = DateTimeOffset.MinValue;
            _halfOpenProbeInFlight = false;
        }
    }

    private void Release(SteamRequestPriority priority)
    {
        if (priority == SteamRequestPriority.Interactive)
        {
            Interlocked.Decrement(ref _pendingInteractive);
        }
        _gate.Release();
    }

    private sealed class BucketLease(DomainGovernorBucket bucket, SteamRequestPriority priority) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            bucket.Release(priority);
        }
    }
}
