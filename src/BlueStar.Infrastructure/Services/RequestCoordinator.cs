using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Thread-safe coordinator that deduplicates concurrent asynchronous operations in flight for identical keys.
/// Uses Lazy wrapper to guarantee exactly-once task instantiation under concurrent race conditions,
/// and supports ref-counted cancellation so underlying network calls abort only when all subscribers cancel.
/// </summary>
public sealed class RequestCoordinator : IRequestCoordinator
{
    /// <summary>
    /// Default shared singleton coordinator for fallback scenarios without DI.
    /// </summary>
    public static RequestCoordinator Instance { get; } = new();

    private sealed class InFlightEntry
    {
        public Task Task { get; private set; } = null!;
        public CancellationTokenSource Cts { get; } = new();
        private readonly object _lock = new();
        private int _subscribers = 1;
        private bool _completed;

        public void SetTask(Task task) => Task = task;

        /// <summary>Joins this request; false once it finished or every earlier caller cancelled it.</summary>
        public bool TryAddSubscriber()
        {
            lock (_lock)
            {
                if (_completed || Cts.IsCancellationRequested)
                    return false;
                _subscribers++;
                return true;
            }
        }

        public void RemoveSubscriber()
        {
            lock (_lock)
            {
                _subscribers--;
                if (_subscribers <= 0 && !_completed)
                {
                    try { Cts.Cancel(); } catch { }
                }
            }
        }

        public void MarkCompleted()
        {
            lock (_lock)
            {
                _completed = true;
            }
        }
    }

    private readonly ConcurrentDictionary<string, Lazy<InFlightEntry>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly INetworkMetricsObserver? _metrics;
    private readonly ILogger<RequestCoordinator>? _logger;

    public RequestCoordinator(
        INetworkMetricsObserver? metrics = null,
        ILogger<RequestCoordinator>? logger = null)
    {
        _metrics = metrics;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<T> ExecuteAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);

        ct.ThrowIfCancellationRequested();

        InFlightEntry entry;
        while (true)
        {
            Lazy<InFlightEntry>? mine = null;
            mine = new Lazy<InFlightEntry>(() =>
            {
                var entryInstance = new InFlightEntry();
                entryInstance.SetTask(RunWithCleanupAsync(key, factory, entryInstance.Cts.Token, entryInstance, mine!));
                return entryInstance;
            });

            var lazy = _inFlight.GetOrAdd(key, mine);
            entry = lazy.Value;
            if (ReferenceEquals(lazy, mine))
                break;
            if (entry.TryAddSubscriber())
            {
                _logger?.LogDebug("[RequestCoordinator] Reusing in-flight task for key: {Key}", key);
                _metrics?.RecordEvent("Coordinator", key, key, cacheHit: false, inFlightReused: true, statusCode: 200, durationMs: 0);
                break;
            }

            // Cancelled by its earlier callers but not finished yet: joining it would hand this caller their
            // cancellation. Drop it (only if it is still the one mapped) and start a fresh request.
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<InFlightEntry>>(key, lazy));
        }

        if (entry.Task is Task<T> typedTask)
        {
            int removed = 0;
            void OnCallerExitOrCancel()
            {
                if (Interlocked.Exchange(ref removed, 1) == 0)
                {
                    entry.RemoveSubscriber();
                }
            }

            CancellationTokenRegistration reg = default;
            if (ct.CanBeCanceled)
            {
                reg = ct.Register(OnCallerExitOrCancel);
            }

            try
            {
                return await typedTask.WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                reg.Dispose();
                OnCallerExitOrCancel();
            }
        }

        entry.RemoveSubscriber();
        throw new InvalidOperationException($"In-flight request key '{key}' was created with a conflicting return type.");
    }

    private async Task<T> RunWithCleanupAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken innerCt,
        InFlightEntry entry,
        Lazy<InFlightEntry> self)
    {
        try
        {
            return await factory(innerCt).ConfigureAwait(false);
        }
        finally
        {
            entry.MarkCompleted();
            // Remove only this entry: a newer request for the same key may already be mapped.
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<InFlightEntry>>(key, self));
        }
    }
}
