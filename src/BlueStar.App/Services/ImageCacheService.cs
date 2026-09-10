using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BlueStar.App.Services;

/// <summary>
/// High-performance multi-tier Image Cache service for BlueStar.
/// Provides:
/// 1. L1 In-Memory Cache: Thread-safe frozen BitmapSource objects shared across all UI elements.
/// 2. L2 Persistent Disk Cache: Saved locally under %LOCALAPPDATA%/BlueStar/cache/images.
/// 3. Resilient Multi-CDN Fallback: Automatically retries alternative Steam CDN endpoints if primary fails.
/// </summary>
public sealed class ImageCacheService
{
    private static readonly Lazy<ImageCacheService> _instance = new(() => new ImageCacheService());
    public static ImageCacheService Instance => _instance.Value;

    private readonly ConcurrentDictionary<string, BitmapSource> _memoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<BitmapSource?>> _inFlightTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _diskCacheFolder;
    private readonly HttpClient _httpClient;

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
            MaxConnectionsPerServer = 20,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 BlueStar/1.1");
    }

    /// <summary>
    /// Synchronously retrieves a cached image from L1 Memory or L2 Disk cache if available.
    /// If not cached, initializes asynchronous stream download and hooks cache persistence.
    /// </summary>
    public ImageSource? GetOrLoadImage(string? url, uint appId = 0)
    {
        if (string.IsNullOrWhiteSpace(url) && appId == 0) return null;

        var key = BuildKey(url, appId);

        // 1. Check L1 Memory Cache (Instant, 0ms, 0 allocations, shared instance)
        if (_memoryCache.TryGetValue(key, out var memBitmap))
        {
            return memBitmap;
        }

        // 2. Check L2 Persistent Disk Cache
        var diskPath = GetDiskCachePath(key);
        if (File.Exists(diskPath))
        {
            try
            {
                var fileBytes = File.ReadAllBytes(diskPath);
                if (fileBytes.Length > 200)
                {
                    var diskBitmap = CreateFrozenBitmap(fileBytes);
                    if (diskBitmap != null)
                    {
                        _memoryCache[key] = diskBitmap;
                        return diskBitmap;
                    }
                }
            }
            catch { }
        }

        // 3. Not in cache yet: Create native non-blocking async BitmapImage
        try
        {
            var primaryUrl = !string.IsNullOrWhiteSpace(url)
                ? url
                : $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg";

            if (Uri.TryCreate(primaryUrl, UriKind.Absolute, out var uri))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = uri;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.None;

                bmp.DownloadCompleted += (s, e) =>
                {
                    try
                    {
                        if (bmp.CanFreeze) bmp.Freeze();
                        _memoryCache[key] = bmp;

                        // Save to disk cache asynchronously
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var encoder = new JpegBitmapEncoder();
                                encoder.Frames.Add(BitmapFrame.Create(bmp));
                                using var ms = new MemoryStream();
                                encoder.Save(ms);
                                await File.WriteAllBytesAsync(diskPath, ms.ToArray()).ConfigureAwait(false);
                            }
                            catch { }
                        });
                    }
                    catch { }
                };

                bmp.EndInit();
                return bmp;
            }
        }
        catch { }

        return null;
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
                    var diskBitmap = CreateFrozenBitmap(fileBytes);
                    if (diskBitmap != null)
                    {
                        _memoryCache[key] = diskBitmap;
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
    /// </summary>
    public async Task<BitmapSource?> LoadImageAsync(string? url, uint appId = 0, string? explicitKey = null)
    {
        if (string.IsNullOrWhiteSpace(url) && appId == 0) return null;

        var key = explicitKey ?? BuildKey(url, appId);

        if (_memoryCache.TryGetValue(key, out var cached))
            return cached;

        return await _inFlightTasks.GetOrAdd(key, k => LoadImageCoreAsync(k, url, appId)).ConfigureAwait(false);
    }

    private async Task<BitmapSource?> LoadImageCoreAsync(string key, string? url, uint appId)
    {
        try
        {
            var diskPath = GetDiskCachePath(key);
            if (File.Exists(diskPath))
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(diskPath).ConfigureAwait(false);
                    if (bytes.Length > 200)
                    {
                        var bmp = CreateFrozenBitmap(bytes);
                        if (bmp != null)
                        {
                            _memoryCache[key] = bmp;
                            return bmp;
                        }
                    }
                }
                catch { }
            }

            // Extract appId if not passed explicitly
            var targetAppId = appId;
            if (targetAppId == 0 && !string.IsNullOrWhiteSpace(url))
            {
                var match = SteamAppIdRegex.Match(url);
                if (match.Success && uint.TryParse(match.Groups["appid"].Value, out var parsedId))
                {
                    targetAppId = parsedId;
                }
            }

            // Build candidate URLs with multi-CDN fallbacks
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(url))
            {
                candidates.Add(url);
            }

            if (targetAppId > 0)
            {
                candidates.Add($"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{targetAppId}/header.jpg");
                candidates.Add($"https://cdn.akamai.steamstatic.com/steam/apps/{targetAppId}/header.jpg");
                candidates.Add($"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{targetAppId}/capsule_616x353.jpg");
                candidates.Add($"https://cdn.cloudflare.steamstatic.com/steam/apps/{targetAppId}/header.jpg");
                candidates.Add($"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{targetAppId}/capsule_467x181.jpg");
                candidates.Add($"https://cdn.akamai.steamstatic.com/steam/apps/{targetAppId}/capsule_616x353.jpg");
                candidates.Add($"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{targetAppId}/library_600x900.jpg");
            }

            // Try each candidate URL until valid image bytes are retrieved
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate) || !seenUrls.Add(candidate))
                    continue;

                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                    using var response = await _httpClient.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        continue;

                    var imageBytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
                    if (imageBytes.Length < 300)
                        continue;

                    // Verify it is a valid image buffer
                    var bitmap = CreateFrozenBitmap(imageBytes);
                    if (bitmap != null)
                    {
                        // Save to L2 disk cache asynchronously
                        try
                        {
                            await File.WriteAllBytesAsync(diskPath, imageBytes).ConfigureAwait(false);
                        }
                        catch { }

                        _memoryCache[key] = bitmap;
                        return bitmap;
                    }
                }
                catch { }
            }
        }
        catch { }
        finally
        {
            _inFlightTasks.TryRemove(key, out _);
        }

        return null;
    }

    private static BitmapSource? CreateFrozenBitmap(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 100) return null;
        try
        {
            var ms = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            ms.Close();
            ms.Dispose();
            if (bitmap.CanFreeze) bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the cache key for an image.
    /// <para>
    /// Collapsing every URL that mentions an app id to a single "steam_app_{id}" key is only
    /// correct for the ONE canonical artwork per game (its header / capsule / library art), which
    /// is what the sharing was for. Screenshots and videos live under the same
    /// <c>.../apps/{appid}/...</c> path, so the old rule handed every screenshot of a game the
    /// cached header image — the whole strip rendered as the same banner over and over. Anything
    /// that is not the canonical artwork is therefore keyed by its own URL.
    /// </para>
    /// </summary>
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

            // Distinct asset (screenshot, movie thumbnail, custom art): key it by itself.
            return trimmed;
        }

        if (appId > 0) return $"steam_app_{appId}";
        return "empty_image";
    }

    /// <summary>
    /// Returns true for the one image per app that every view is meant to share: the store header,
    /// the capsule, or the library artwork.
    /// </summary>
    private static bool IsCanonicalArtworkUrl(string url)
    {
        var fileName = url;

        var query = fileName.IndexOf('?');
        if (query >= 0) fileName = fileName[..query];

        var lastSlash = fileName.LastIndexOf('/');
        if (lastSlash >= 0 && lastSlash < fileName.Length - 1) fileName = fileName[(lastSlash + 1)..];

        return fileName.StartsWith("header", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("capsule", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("library_", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("logo", StringComparison.OrdinalIgnoreCase)
            || fileName.StartsWith("page_bg", StringComparison.OrdinalIgnoreCase);
    }

    private string GetDiskCachePath(string key)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return Path.Combine(_diskCacheFolder, $"{hash}.bin");
    }
}
