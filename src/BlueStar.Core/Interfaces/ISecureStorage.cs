using System.Threading;
using System.Threading.Tasks;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Provides methods to securely store and retrieve sensitive information.
/// </summary>
public interface ISecureStorage
{
    /// <summary>
    /// Retrieves a securely stored value by its key.
    /// </summary>
    /// <param name="key">The key identifying the securely stored value.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the value, or null if it was not found.</returns>
    Task<string?> GetAsync(string key, CancellationToken ct);

    /// <summary>
    /// Stores a value securely under the specified key.
    /// </summary>
    /// <param name="key">The key under which to store the value.</param>
    /// <param name="value">The value to store securely.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task SetAsync(string key, string value, CancellationToken ct);

    /// <summary>
    /// Deletes a securely stored value by its key.
    /// </summary>
    /// <param name="key">The key identifying the value to delete.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result is true if the value was deleted successfully; otherwise, false.</returns>
    Task<bool> DeleteAsync(string key, CancellationToken ct);
}
