using System;
using System.Threading;
using System.Threading.Tasks;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Provides methods for caching arbitrary objects.
/// </summary>
public interface ICacheService
{
    /// <summary>
    /// Retrieves an object from the cache by its key.
    /// </summary>
    /// <typeparam name="T">The type of the object to retrieve.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the cached object, or null if it was not found.</returns>
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);

    /// <summary>
    /// Stores an object in the cache with the specified key.
    /// </summary>
    /// <typeparam name="T">The type of the object to store.</typeparam>
    /// <param name="key">The cache key.</param>
    /// <param name="value">The object to store.</param>
    /// <param name="expiry">An optional time-to-live for the cached item. If null, the item might not expire.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task SetAsync<T>(string key, T value, TimeSpan? expiry = null, CancellationToken ct = default);

    /// <summary>
    /// Removes an item from the cache by its key.
    /// </summary>
    /// <param name="key">The cache key.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task RemoveAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Clears all items from the cache.
    /// </summary>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task ClearAsync(CancellationToken ct = default);
}
