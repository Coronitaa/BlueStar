using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace BlueStar.Core.Helpers;

/// <summary>
/// Concurrency coordinator that deduplicates simultaneous in-flight operations for the same key.
/// Prevents cache stampede / thundering herd by ensuring only one asynchronous worker runs per key,
/// with all concurrent callers awaiting the same result.
/// </summary>
public sealed class SingleFlight
{
    private readonly ConcurrentDictionary<string, Task<object?>> _inFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// Executes the factory for <paramref name="key"/> only once among concurrent callers, sharing the result with all.
    /// </summary>
    public async Task<T> ExecuteAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);

        while (true)
        {
            if (_inFlight.TryGetValue(key, out var activeTask))
            {
                var existingResult = await activeTask.WaitAsync(ct).ConfigureAwait(false);
                return (T)existingResult!;
            }

            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_inFlight.TryAdd(key, tcs.Task))
            {
                try
                {
                    var result = await factory(ct).ConfigureAwait(false);
                    tcs.TrySetResult(result);
                    return result;
                }
                catch (OperationCanceledException oce)
                {
                    tcs.TrySetCanceled(oce.CancellationToken);
                    throw;
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                    throw;
                }
                finally
                {
                    _inFlight.TryRemove(key, out _);
                }
            }
        }
    }
}
