using System;
using System.Threading;
using System.Threading.Tasks;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Headless Steam Workshop downloader that fetches items via Steam Remote Storage API
/// and DepotDownloader, persisting raw downloads into a central cache.
/// </summary>
public interface IWorkshopDownloader
{
    /// <summary>
    /// Downloads a workshop item by AppId and PublishedFileId into the central cache (data/workshop_cache/<AppId>/<PublishedFileId>/).
    /// </summary>
    /// <returns>Path to the cached item directory, or null if download failed.</returns>
    Task<string?> DownloadItemToCacheAsync(
        uint appId,
        ulong publishedFileId,
        IProgress<double>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the cache folder path for a specific workshop item.
    /// </summary>
    string GetItemCachePath(uint appId, ulong publishedFileId);

    /// <summary>
    /// Checks if a workshop item is already downloaded and present in the central cache.
    /// </summary>
    bool IsItemCached(uint appId, ulong publishedFileId);
}
