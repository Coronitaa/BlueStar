using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Provides methods to retrieve game metadata and DLC information.
/// </summary>
public interface IMetadataProvider
{
    /// <summary>
    /// Retrieves metadata for a specific game.
    /// </summary>
    /// <param name="appId">The application identifier of the game.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the metadata, or null if not found.</returns>
    Task<GameMetadata?> GetMetadataAsync(uint appId, CancellationToken ct);

    /// <summary>
    /// Retrieves a list of DLCs associated with a specific game.
    /// </summary>
    /// <param name="appId">The application identifier of the base game.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a read-only list of DLC information.</returns>
    Task<IReadOnlyList<DlcInfo>> GetDlcListAsync(uint appId, CancellationToken ct);

    /// <summary>
    /// Enriches a search result with Steam metadata (DLC count, OS compatibility, banner, version).
    /// </summary>
    /// <param name="result">The search result to enrich in-place.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    Task EnrichSearchResultAsync(SearchResult result, CancellationToken ct = default);

    /// <summary>
    /// Searches the Steam Store for games matching the specified query.
    /// </summary>
    /// <param name="query">Search term or AppId.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A list of search result items with AppId, name and thumbnails.</returns>
    Task<IReadOnlyList<SteamStoreSearchItem>> SearchStoreAsync(string query, CancellationToken ct = default);
}
