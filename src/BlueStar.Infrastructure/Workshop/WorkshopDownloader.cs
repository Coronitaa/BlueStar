using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Workshop;

/// <summary>
/// Headless Steam Workshop downloader that manages the central cache (data/workshop_cache/<AppId>/<PublishedFileId>/).
/// </summary>
public sealed class WorkshopDownloader : IWorkshopDownloader
{
    private readonly IWorkshopService _workshopService;
    private readonly string _cacheRoot;
    private readonly ILogger<WorkshopDownloader> _logger;

    public WorkshopDownloader(
        IWorkshopService workshopService,
        ILogger<WorkshopDownloader> logger,
        string? cacheRoot = null)
    {
        _workshopService = workshopService ?? throw new ArgumentNullException(nameof(workshopService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _cacheRoot = cacheRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "data", "workshop_cache");

        Directory.CreateDirectory(_cacheRoot);
    }

    /// <inheritdoc />
    public string GetItemCachePath(uint appId, ulong publishedFileId)
    {
        return Path.Combine(_cacheRoot, appId.ToString(), publishedFileId.ToString());
    }

    /// <inheritdoc />
    public bool IsItemCached(uint appId, ulong publishedFileId)
    {
        var path = GetItemCachePath(appId, publishedFileId);
        if (!Directory.Exists(path)) return false;

        var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);
        return files.Length > 0;
    }

    /// <inheritdoc />
    public async Task<string?> DownloadItemToCacheAsync(
        uint appId,
        ulong publishedFileId,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (publishedFileId == 0) return null;

        var cachePath = GetItemCachePath(appId, publishedFileId);
        if (IsItemCached(appId, publishedFileId))
        {
            _logger.LogInformation("Workshop item {Id} found in cache: {Path}", publishedFileId, cachePath);
            progress?.Report(100.0);
            return cachePath;
        }

        Directory.CreateDirectory(cachePath);
        try
        {
            _logger.LogInformation("Downloading Workshop item {Id} for App {AppId} to cache {Path}",
                publishedFileId, appId, cachePath);

            bool success = await _workshopService.DownloadAndInstallItemAsync(
                appId, publishedFileId, cachePath, progress, ct).ConfigureAwait(false);

            if (success && IsItemCached(appId, publishedFileId))
            {
                _logger.LogInformation("Workshop item {Id} cached successfully at {Path}", publishedFileId, cachePath);
                return cachePath;
            }

            // Cleanup failed partial download
            if (Directory.Exists(cachePath))
            {
                try { Directory.Delete(cachePath, recursive: true); } catch { }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download workshop item {Id} to cache", publishedFileId);
            return null;
        }
    }
}
