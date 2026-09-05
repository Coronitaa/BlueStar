using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Provides methods to interact with the DepotBox API.
/// </summary>
public interface IDepotBoxApiClient
{
    /// <summary>
    /// Searches for games matching the specified query.
    /// </summary>
    /// <param name="query">The search term to use.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a read-only list of search results.</returns>
    Task<IReadOnlyList<SearchResult>> SearchGamesAsync(string query, CancellationToken ct);

    /// <summary>
    /// Retrieves metadata for a specific game.
    /// </summary>
    /// <param name="appId">The application identifier of the game.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <param name="forceRefresh">Whether to bypass and overwrite the local cache.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the game metadata, or null if it was not found.</returns>
    Task<GameMetadata?> GetGameAsync(uint appId, CancellationToken ct = default, bool forceRefresh = false);

    /// <summary>
    /// Retrieves a list of manifests for a specific game.
    /// </summary>
    /// <param name="appId">The application identifier of the game.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <param name="forceRefresh">Whether to bypass and overwrite the local cache.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains a read-only list of manifests.</returns>
    Task<IReadOnlyList<ManifestInfo>> GetManifestsAsync(uint appId, CancellationToken ct = default, bool forceRefresh = false);

    /// <summary>
    /// Checks whether a specific game is available.
    /// </summary>
    /// <param name="appId">The application identifier of the game.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <param name="forceRefresh">Whether to bypass and overwrite the local cache.</param>
    /// <returns>A task that represents the asynchronous operation. The task result is true if the game is available; otherwise, false.</returns>
    Task<bool> CheckAvailabilityAsync(uint appId, CancellationToken ct = default, bool forceRefresh = false);

    /// <summary>
    /// Checks the availability of multiple games in a single request.
    /// </summary>
    /// <param name="appIds">A collection of application identifiers to check.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <param name="forceRefresh">Whether to bypass and overwrite the local cache.</param>
    /// <returns>A task that represents the asynchronous operation. The task result is a dictionary mapping application identifiers to their availability status.</returns>
    Task<IDictionary<uint, bool>> BatchCheckAvailabilityAsync(IEnumerable<uint> appIds, CancellationToken ct = default, bool forceRefresh = false);

    /// <summary>
    /// Downloads an archive for a specific game synchronously.
    /// </summary>
    /// <param name="appId">The application identifier of the game.</param>
    /// <param name="targetPath">The path where the archive should be saved.</param>
    /// <param name="progress">An optional provider for progress updates.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result is the file path of the downloaded archive.</returns>
    Task<string> DownloadArchiveAsync(uint appId, string targetPath, IProgress<DownloadProgress>? progress, CancellationToken ct);

    /// <summary>
    /// Initiates an asynchronous download for a game archive.
    /// </summary>
    /// <param name="appId">The application identifier of the game.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result is a download token to track the progress.</returns>
    Task<string> StartAsyncDownloadAsync(uint appId, CancellationToken ct);

    /// <summary>
    /// Checks the status of an ongoing asynchronous download.
    /// </summary>
    /// <param name="downloadToken">The download token returned by <see cref="StartAsyncDownloadAsync"/>.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result contains the current download progress.</returns>
    Task<DownloadProgress> CheckDownloadStatusAsync(string downloadToken, CancellationToken ct);

    /// <summary>
    /// Retrieves a completed archive that was downloaded asynchronously.
    /// </summary>
    /// <param name="downloadToken">The download token representing the completed download.</param>
    /// <param name="targetPath">The path where the archive should be saved.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation. The task result is the file path of the downloaded archive.</returns>
    Task<string> DownloadCompletedArchiveAsync(string downloadToken, string targetPath, CancellationToken ct);

    /// <summary>
    /// Retrieves a list of game fixes (emulators, bypasses, hypervisors) from DepotBox API.
    /// </summary>
    /// <param name="query">Optional search query by game name or AppId.</param>
    /// <param name="tags">Optional comma-separated tags filter (e.g., "online,bypass,hypervisor").</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>A read-only list of available game fixes matching the criteria.</returns>
    Task<IReadOnlyList<GameFixInfo>> GetGameFixesAsync(string? query = null, string? tags = null, CancellationToken ct = default);

    /// <summary>
    /// Downloads a game fix archive by its ID or download filename.
    /// </summary>
    /// <param name="fixIdOrFilename">The fix ID or clean filename (e.g. "007_First_Light_bypass.zip").</param>
    /// <param name="targetPath">The local path where the fix ZIP archive should be saved.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="downloadName">Optional explicit download filename from metadata.</param>
    /// <param name="ct">A token to monitor for cancellation requests.</param>
    /// <returns>The path to the downloaded archive file.</returns>
    Task<string> DownloadGameFixAsync(string fixIdOrFilename, string targetPath, IProgress<DownloadProgress>? progress = null, string? downloadName = null, CancellationToken ct = default);

    /// <summary>
    /// Invalidates all cached entries (manifests, availability, game details) for the given AppId.
    /// </summary>
    /// <param name="appId">The application identifier to invalidate in cache.</param>
    void InvalidateAppCache(uint appId);
}
