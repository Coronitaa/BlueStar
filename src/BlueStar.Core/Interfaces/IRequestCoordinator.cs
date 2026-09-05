using System;
using System.Threading;
using System.Threading.Tasks;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Coordinates concurrent asynchronous operations to eliminate duplicate in-flight executions for identical keys.
/// </summary>
public interface IRequestCoordinator
{
    /// <summary>
    /// Executes the factory operation or returns an existing in-flight Task for the specified key.
    /// If an operation for <paramref name="key"/> is already running, subsequent callers await the same task.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="key">Unique deduplication key.</param>
    /// <param name="factory">Operation factory invoked once if no in-flight request exists.</param>
    /// <param name="ct">Cancellation token for the caller.</param>
    /// <returns>The result of the operation.</returns>
    Task<T> ExecuteAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CancellationToken ct = default);
}
