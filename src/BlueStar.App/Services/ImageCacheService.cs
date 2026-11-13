using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BlueStar.Core.Helpers;
using BlueStar.Core.Models;

namespace BlueStar.App.Services;

/// <summary>
/// High-performance multi-tier image caching engine for BlueStar.
/// Designed for:
/// 1. Zero API thrashing: Negative caching (tombstoning) prevents repeated requests for 404s/unreleased games;
///    local Steam librarycache lookup bypasses network entirely when assets exist locally.
/// 2. Speed: Instant L1 memory lookup (0ms) and fast async L2 disk hits with in-flight deduplication.
/// 3. Bounded disk footprint: Automatic LRU disk cache maintenance strictly capped at 60 MB.
/// 4. CPU/RAM efficiency: Constrained DecodePixelWidth (300px), thread-safe frozen bitmaps, bounded L1 cache,
///    and cancellation support to stop off-screen image decoding.
/// </summary>
public sealed class ImageCacheService
{
    private static readonly Lazy<ImageCacheService> _instance = new(() => new ImageCacheService());
    public static ImageCacheService Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, BitmapSource> _memoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<BitmapSource?>> _inFlightTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _negativeCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _memoryCacheOrder = new();

    private const int MaxMemoryCacheEntries = 120;
    private const long MaxDiskCacheSizeBytes = 60 * 1024 * 1024;    // 60 MB cap
    private const long TargetDiskCacheSizeBytes = 40 * 1024 * 1024; // 40 MB target after pruning
    private const int NegativeCacheTtlSeconds = 3600;               // 1 hour tombstone

    private readonly string _diskCacheFolder;
    private readonly HttpClient _httpClient;
    private readonly object _diskMaintenanceLock = new();
    private int _diskWriteCounter;

    private static readonly Lazy<string?> _cachedSteamPath = new(() =>
    {
        try
        {
            return ShortcutHelper.GetSteamPath();
        }
        catch
        {
            return null;
        }
    });

    private static readonly Regex SteamAppIdRegex = new(@"apps[\\/](?<appid>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private ImageCacheService()
    {
        _diskCacheFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BlueStar", "cache", "images");

        try
        {
            Directory.CreateDirectory(_diskCacheFolder);
        }
        catch { }

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 10,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 BlueStar/1.1");

        // Clean up disk cache on startup in background
        TryScheduleDiskPruning(force: true);
    }

    private void SetMemoryCache(string key, BitmapSource bmp)
    {
        if (_memoryCache.TryAdd(key, bmp))
        {
            _memoryCacheOrder.Enqueue(key);
        }
        else
        {
            _memoryCache[key] = bmp;
        }

        while (_memoryCache.Count > MaxMemoryCacheEntries && _memoryCacheOrder.TryDequeue(out var oldKey))
        {
            if (_memoryCache.Count <= MaxMemoryCacheEntries) break;
            _memoryCache.TryRemove(oldKey, out _);
        }
    }

    private bool IsNegativeCached(string key)
    {
        if (_negativeCache.TryGetValue(key, out var expiryTime))
        {
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() < expiryTime)
            {
                return true;
            }
            _negativeCache.TryRemove(key, out _);
        }
        return false;
    }

    private void MarkNegativeCache(string key)
    {
        _negativeCache[key] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + NegativeCacheTtlSeconds;
    }

    /// <summary>
    /// Synchronously retrieves a cached image from L1 Memory or L2 Disk cache if available.
    /// If not cached, initializes non-blocking background fetch.
    /// </summary>
    public ImageSource? GetOrLoadImage(string? url, uint appId = 0)
    {
        if (string.IsNullOrWhiteSpace(url) && appId == 0) return null;

        var key = BuildKey(url, appId);

        // 1. L1 Memory Cache (Instant, 0ms)
        if (_memoryCache.TryGetValue(key, out var memBitmap))
        {
            return memBitmap;
        }

        // 2. Negative Cache Check (Tombstone)
        if (IsNegativeCached(key))
        {
            return null;
        }

        // 3. L2 Persistent Disk Cache
        var diskPath = GetDiskCachePath(key);
        if (File.Exists(diskPath))
        {
            try
            {
                var fileBytes = File.ReadAllBytes(diskPath);
                if (fileBytes.Length > 200)
                {
                    var diskBitmap = CreateFrozenBitmap(fileBytes, 300);
                    if (diskBitmap != null)
                    {
                        SetMemoryCache(key, diskBitmap);
                        return diskBitmap;
                    }
                }
            }
            catch { }
        }

        // 4. Local Steam librarycache check
        var targetAppId = appId;
        if (targetAppId == 0 && !string.IsNullOrWhiteSpace(url))
        {
            var match = SteamAppIdRegex.Match(url);
            if (match.Success && uint.TryParse(match.Groups["appid"].Value, out var parsedId))
            {
                targetAppId = parsedId;
            }
        }

        if (targetAppId > 0)
        {
            var localSteamBitmap = TryLoadFromLocalSteamCache(targetAppId, key, diskPath);
            if (localSteamBitmap != null)
            {
                return localSteamBitmap;
            }
        }

        // 5. Fire async background fetch and return null without blocking UI
        _ = LoadImageAsync(url, appId);
        return null;
    }

    /// <summary>
    /// The in-memory bitmap for a URL, or null. Never touches the disk or the network.
    /// </summary>
    public BitmapSource? GetFromMemory(string? url, uint appId = 0)
    {
        if (string.IsNullOrWhiteSpace(url) && appId == 0) return null;
        return _memoryCache.TryGetValue(BuildKey(url, appId), out var bmp) ? bmp : null;
    }

    /// <summary>
    /// Starts loading images in the background (disk cache or network) so they are in memory by the time
    /// their cards are realized. Already cached or in-flight images cost nothing.
    /// </summary>
    public void Prefetch(IEnumerable<string?> urls)
    {
        foreach (var url in urls)
        {
            if (string.IsNullOrWhiteSpace(url) || GetFromMemory(url) != null) continue;
            _ = LoadImageAsync(url);
        }
    }

    /// <summary>
    /// Synchronously retrieves a cached image from L1 Memory or L2 Disk cache without making network requests.
    /// </summary>
    public ImageSource? GetFromMemoryOrDisk(string? url, uint appId = 0)
    {
        if (string.IsNullOrWhiteSpace(url) && appId == 0) return null;

        var key = BuildKey(url, appId);

        // 1. L1 Memory (Instant, 0ms)
        if (_memoryCache.TryGetValue(key, out var memBitmap))
        {
            return memBitmap;
        }

        // 2. L2 Persistent Disk Cache
        var diskPath = GetDiskCachePath(key);
        if (File.Exists(diskPath))
        {
            try
            {
                var fileBytes = File.ReadAllBytes(diskPath);
                if (fileBytes.Length > 200)
                {
                    var diskBitmap = CreateFrozenBitmap(fileBytes, 300);
                    if (diskBitmap != null)
                    {
                        SetMemoryCache(key, diskBitmap);
                        return diskBitmap;
                    }
                }
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// Asynchronously loads an image from network with fallback endpoints and caches to L1/L2.
    /// Supports cooperative cancellation.
    /// </summary>
    public async Task<BitmapSource?> LoadImageAsync(string? url, uint appId = 0, string? explicitKey = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url) && appId == 0) return null;

        var key = explicitKey ?? BuildKey(url, appId);

        if (_memoryCache.TryGetValue(key, out var cached))
            return cached;

        if (IsNegativeCached(key) || cancellationToken.IsCancellationRequested)
            return null;

        try
        {
            var task = _inFlightTasks.GetOrAdd(key, k => LoadImageCoreAsync(k, url, appId));
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<BitmapSource?> LoadImageCoreAsync(string key, string? url, uint appId)
    {
        try
        {
            if (IsNegativeCached(key)) return null;

            var diskPath = GetDiskCachePath(key);

            // 1. Check L2 Disk Cache
            if (File.Exists(diskPath))
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(diskPath).ConfigureAwait(false);
                    if (bytes.Length > 200)
                    {
                        var bmp = CreateFrozenBitmap(bytes, 300);
                        if (bmp != null)
                        {
                            SetMemoryCache(key, bmp);
                            return bmp;
                        }
                    }
                }
                catch { }
            }

            // 2. Check Local Steam librarycache if target AppId is present
            var targetAppId = appId;
            if (targetAppId == 0 && !string.IsNullOrWhiteSpace(url))
            {
                var match = SteamAppIdRegex.Match(url);
                if (match.Success && uint.TryParse(match.Groups["appid"].Value, out var parsedId))
                {
                    targetAppId = parsedId;
                }
            }

            if (targetAppId > 0)
            {
                var localSteamBitmap = TryLoadFromLocalSteamCache(targetAppId, key, diskPath);
                if (localSteamBitmap != null)
                {
                    return localSteamBitmap;
                }
            }

            // 3. Lean Candidate URLs: Direct URL + Max 2 Steam CDN endpoints (NO redundant mirrors)
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(url) && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(url);
            }

            if (targetAppId > 0)
            {
                candidates.Add($"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{targetAppId}/header.jpg");
                candidates.Add($"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{targetAppId}/capsule_616x353.jpg");
            }

            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate) || !seenUrls.Add(candidate))
                    continue;

                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3.5));
                    using var response = await _httpClient.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        continue;

                    var imageBytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                    if (imageBytes.Length < 300)
                        continue;

                    var bitmap = CreateFrozenBitmap(imageBytes, 300);
                    if (bitmap != null)
                    {
                        // Save to L2 disk cache asynchronously
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await File.WriteAllBytesAsync(diskPath, imageBytes).ConfigureAwait(false);
                                TryScheduleDiskPruning();
                            }
                            catch { }
                        });

                        SetMemoryCache(key, bitmap);
                        return bitmap;
                    }
                }
                catch { }
            }

            // If all candidates failed: mark key in negative cache to prevent request thrashing
            MarkNegativeCache(key);
        }
        catch { }
        finally
        {
            _inFlightTasks.TryRemove(key, out _);
        }

        return null;
    }

    private BitmapSource? TryLoadFromLocalSteamCache(uint appId, string key, string diskPath)
    {
        if (appId == 0) return null;

        var steamPath = _cachedSteamPath.Value;
        if (string.IsNullOrWhiteSpace(steamPath) || !Directory.Exists(steamPath)) return null;

        try
        {
            var libraryCache = Path.Combine(steamPath, "appcache", "librarycache");
            if (!Directory.Exists(libraryCache)) return null;

            var appIdStr = appId.ToString();

            string[] candidates = [
                Path.Combine(libraryCache, appIdStr, "header.jpg"),
                Path.Combine(libraryCache, $"{appIdStr}_header.jpg"),
                Path.Combine(libraryCache, appIdStr, "library_hero.jpg"),
                Path.Combine(libraryCache, appIdStr, "library_600x900.jpg"),
                Path.Combine(libraryCache, $"{appIdStr}_library_600x900.jpg"),
                Path.Combine(libraryCache, appIdStr, "capsule_231x87.jpg"),
                Path.Combine(libraryCache, $"{appIdStr}_capsule_231x87.jpg")
            ];

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    var bytes = File.ReadAllBytes(candidate);
                    if (bytes.Length > 200)
                    {
                        var bmp = CreateFrozenBitmap(bytes, 300);
                        if (bmp != null)
                        {
                            SetMemoryCache(key, bmp);
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await File.WriteAllBytesAsync(diskPath, bytes).ConfigureAwait(false);
                                    TryScheduleDiskPruning();
                                }
                                catch { }
                            });
                            return bmp;
                        }
                    }
                }
            }
        }
        catch { }

        return null;
    }

    private void TryScheduleDiskPruning(bool force = false)
    {
        if (!force && Interlocked.Increment(ref _diskWriteCounter) % 25 != 0) return;

        Task.Run(() =>
        {
            if (!Monitor.TryEnter(_diskMaintenanceLock)) return;

            try
            {
                if (!Directory.Exists(_diskCacheFolder)) return;

                var dirInfo = new DirectoryInfo(_diskCacheFolder);
                var files = dirInfo.GetFiles("*.bin");
                long totalSize = files.Sum(f => f.Length);

                if (totalSize <= MaxDiskCacheSizeBytes) return;

                // Sort by last access time ascending (oldest accessed first)
                var sorted = files.OrderBy(f => f.LastAccessTimeUtc).ToList();
                foreach (var f in sorted)
                {
                    if (totalSize <= TargetDiskCacheSizeBytes) break;
                    try
                    {
                        var len = f.Length;
                        f.Delete();
                        totalSize -= len;
                    }
                    catch { }
                }
            }
            catch { }
            finally
            {
                Monitor.Exit(_diskMaintenanceLock);
            }
        });
    }

    private static BitmapSource? CreateFrozenBitmap(byte[] bytes, int decodePixelWidth = 300)
    {
        if (bytes == null || bytes.Length < 100) return null;
        try
        {
            using var ms = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodePixelWidth > 0)
            {
                bitmap.DecodePixelWidth = decodePixelWidth;
            }
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            if (bitmap.CanFreeze) bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildKey(string? url, uint appId)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            var trimmed = url.Trim();

            if (IsCanonicalArtworkUrl(trimmed))
            {
                var match = SteamAppIdRegex.Match(trimmed);
                if (match.Success) return $"steam_app_{match.Groups["appid"].Value}";
                if (appId > 0) return $"steam_app_{appId}";
            }

            return trimmed;
        }

        if (appId > 0) return $"steam_app_{appId}";
        return "empty_image";
    }

    /// <summary>
    /// Evicts an image entry from memory, negative cache, and disk cache.
    /// </summary>
    public void Invalidate(string? url, uint appId = 0)
    {
        var keysToInvalidate = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(url))
        {
            keysToInvalidate.Add(BuildKey(url, appId));
            keysToInvalidate.Add(url.Trim());
            keysToInvalidate.Add(url.Trim().Replace('/', '\\'));
            keysToInvalidate.Add(url.Trim().Replace('\\', '/'));
        }

        if (appId > 0)
        {
            keysToInvalidate.Add($"steam_app_{appId}");
        }

        foreach (var key in keysToInvalidate)
        {
            _memoryCache.TryRemove(key, out _);
            _inFlightTasks.TryRemove(key, out _);
            _negativeCache.TryRemove(key, out _);

            try
            {
                var diskPath = GetDiskCachePath(key);
                if (File.Exists(diskPath))
                {
                    File.Delete(diskPath);
                }
            }
            catch { }
        }
    }

    private static bool IsCanonicalArtworkUrl(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fileName = url;
        var query = fileName.IndexOf('?');
        if (query >= 0) fileName = fileName[..query];

        var lastSlash = Math.Max(fileName.LastIndexOf('/'), fileName.LastIndexOf('\\'));
        if (lastSlash >= 0 && lastSlash < fileName.Length - 1) fileName = fileName[(lastSlash + 1)..];

        return fileName.StartsWith("header", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("capsule", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("library_", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("logo", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("page_bg", StringComparison.OrdinalIgnoreCase);
    }

    private string GetDiskCachePath(string key)
    {
        return Path.Combine(_diskCacheFolder, $"{GetHashForDisk(key)}.bin");
    }

    private static string GetHashForDisk(string key)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Clears cached images from memory and disk, preserving only the canonical banners and artwork
    /// of the specified installed game instances.
    /// </summary>
    public void ClearCacheExceptInstalled(IEnumerable<GameInstance> installedInstances)
    {
        var preservedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var inst in installedInstances)
        {
            if (inst == null) continue;

            if (inst.AppId > 0)
            {
                var key = $"steam_app_{inst.AppId}";
                preservedHashes.Add(GetHashForDisk(key));
            }

            if (!string.IsNullOrWhiteSpace(inst.HeaderImageUrl))
            {
                var key = BuildKey(inst.HeaderImageUrl, inst.AppId);
                preservedHashes.Add(GetHashForDisk(key));
            }

            if (inst.Metadata != null)
            {
                if (!string.IsNullOrWhiteSpace(inst.Metadata.HeaderImageUrl))
                {
                    preservedHashes.Add(GetHashForDisk(BuildKey(inst.Metadata.HeaderImageUrl, inst.AppId)));
                }
                if (!string.IsNullOrWhiteSpace(inst.Metadata.CapsuleImageUrl))
                {
                    preservedHashes.Add(GetHashForDisk(BuildKey(inst.Metadata.CapsuleImageUrl, inst.AppId)));
                }
            }
        }

        // Clear L1 memory cache keys not preserved
        var memoryKeysToRemove = _memoryCache.Keys
            .Where(k => !preservedHashes.Contains(GetHashForDisk(k)))
            .ToList();

        foreach (var k in memoryKeysToRemove)
        {
            _memoryCache.TryRemove(k, out _);
        }

        // Clear negative cache
        _negativeCache.Clear();

        // Delete disk cache files not preserved
        if (Directory.Exists(_diskCacheFolder))
        {
            try
            {
                var files = Directory.GetFiles(_diskCacheFolder, "*.bin");
                foreach (var file in files)
                {
                    var fileHash = Path.GetFileNameWithoutExtension(file);
                    if (!preservedHashes.Contains(fileHash))
                    {
                        try
                        {
                            File.Delete(file);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }
    }
}
