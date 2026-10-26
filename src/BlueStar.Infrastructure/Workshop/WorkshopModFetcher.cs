using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Workshop;

/// <summary>
/// Keeps each Workshop item under workshop_cache/&lt;appId&gt;/&lt;id&gt;/content with an item.json marker
/// recording Steam's time_updated, and returns a <see cref="ModPackage"/> pointing at that content.
/// </summary>
public sealed class WorkshopModFetcher : IWorkshopModFetcher
{
    private const string ContentFolder = "content";
    private const string MarkerFile = "item.json";
    private const string WorkshopProvider = "workshop";

    private readonly IWorkshopService _workshop;
    private readonly ILogger<WorkshopModFetcher> _logger;
    private readonly string _cacheRoot;

    public WorkshopModFetcher(IWorkshopService workshop, ILogger<WorkshopModFetcher> logger, string? cacheRoot = null)
    {
        _workshop = workshop ?? throw new ArgumentNullException(nameof(workshop));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cacheRoot = cacheRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "data", "workshop_cache");
    }

    /// <inheritdoc />
    public async Task<WorkshopFetchResult> FetchAsync(
        uint appId, ulong publishedFileId, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var details = await _workshop.GetItemDetailsAsync(publishedFileId, ct).ConfigureAwait(false);
        if (details == null)
        {
            return new WorkshopFetchResult(null, Error: "The item does not exist or is private.");
        }

        if (appId != 0 && details.AppId != 0 && details.AppId != appId)
        {
            return new WorkshopFetchResult(null, details,
                $"This item belongs to another game (AppID {details.AppId}).");
        }

        var effectiveAppId = details.AppId != 0 ? details.AppId : appId;
        var itemDir = Path.Combine(_cacheRoot, effectiveAppId.ToString(), publishedFileId.ToString());
        var contentDir = Path.Combine(itemDir, ContentFolder);
        var markerPath = Path.Combine(itemDir, MarkerFile);

        if (IsCacheCurrent(markerPath, contentDir, details))
        {
            _logger.LogInformation("Workshop item {Id} is cached and up to date", publishedFileId);
            progress?.Report(100.0);
            return new WorkshopFetchResult(ToPackage(details, contentDir), details);
        }

        if (Directory.Exists(contentDir)) Directory.Delete(contentDir, recursive: true);

        var download = await _workshop.DownloadItemContentAsync(effectiveAppId, publishedFileId, contentDir, progress, ct)
            .ConfigureAwait(false);
        if (!download.Success)
        {
            if (Directory.Exists(contentDir)) Directory.Delete(contentDir, recursive: true);
            if (Directory.Exists(itemDir) && !Directory.EnumerateFileSystemEntries(itemDir).Any()) Directory.Delete(itemDir);
            return new WorkshopFetchResult(null, details, download.Error);
        }

        var marker = new CacheMarker(publishedFileId, effectiveAppId, details.Title, details.UpdatedAt?.ToUnixTimeSeconds());
        await File.WriteAllTextAsync(markerPath, JsonSerializer.Serialize(marker), ct).ConfigureAwait(false);
        return new WorkshopFetchResult(ToPackage(details, contentDir), details);
    }

    private static bool IsCacheCurrent(string markerPath, string contentDir, WorkshopItemInfo details)
    {
        if (!File.Exists(markerPath) || !Directory.Exists(contentDir) ||
            !Directory.EnumerateFiles(contentDir, "*", SearchOption.AllDirectories).Any())
        {
            return false;
        }

        try
        {
            var marker = JsonSerializer.Deserialize<CacheMarker>(File.ReadAllText(markerPath));
            return marker != null && marker.UpdatedAtUnix == details.UpdatedAt?.ToUnixTimeSeconds();
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ModPackage ToPackage(WorkshopItemInfo details, string contentDir) => new()
    {
        Path = contentDir,
        Name = details.Title,
        Layout = ModPackageLayout.SteamWorkshop,
        WorkshopItemId = details.PublishedFileId,
        Source = new ModSourceRef
        {
            Provider = WorkshopProvider,
            RemoteId = details.PublishedFileId.ToString(),
            Version = details.UpdatedAt?.ToString("yyyy-MM-dd"),
            FileId = details.UpdatedAt?.ToUnixTimeSeconds().ToString(),
            PageUrl = IWorkshopModFetcher.ItemPageUrl(details.PublishedFileId)
        }
    };

    private sealed record CacheMarker(ulong PublishedFileId, uint AppId, string Title, long? UpdatedAtUnix);
}
