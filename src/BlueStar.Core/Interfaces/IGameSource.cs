using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Provides methods to search for and retrieve game information.
/// </summary>
public interface IGameSource
{
    /// <summary>
    /// Searches for games matching the specified query.
    /// </summary>
    /// <param name="query">The search term to use.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a read-only list of search results.</returns>
    Task<IReadOnlyList<SearchResult>> SearchGamesAsync(string query, CancellationToken ct);

    /// <summary>
    /// Retrieves detailed metadata for a specific game.
    /// </summary>
    /// <param name="appId">The application identifier of the game.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the metadata, or null if the game was not found.</returns>
    Task<GameMetadata?> GetGameDetailsAsync(uint appId, CancellationToken ct);

    /// <summary>
    /// Checks whether a specific game is available.
    /// </summary>
    /// <param name="appId">The application identifier of the game.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result is true if the game is available; otherwise, false.</returns>
    Task<bool> IsAvailableAsync(uint appId, CancellationToken ct);
}
