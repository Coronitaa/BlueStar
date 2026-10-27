using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Mods;

/// <summary>
/// Service that synchronizes with BepInEx GitHub releases, installs and uninstalls BepInEx on Unity games.
/// </summary>
public sealed class BepInExService : IBepInExService
{
    private readonly HttpClient _http;
    private readonly ILogger<BepInExService> _logger;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);
    private static IReadOnlyList<BepInExRelease>? _cachedReleases;
    private static DateTimeOffset _cacheExpiry = DateTimeOffset.MinValue;

    public BepInExService(HttpClient http, ILogger<BepInExService> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BlueStar-App", "1.0"));
        }
    }

    /// <summary>
    /// Resolves the actual game executable directory where UnityPlayer.dll or *_Data exists.
    /// </summary>
    public static string ResolveGameRoot(string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
            return installPath;

        if (File.Exists(Path.Combine(installPath, "UnityPlayer.dll")) ||
            Directory.EnumerateDirectories(installPath, "*_Data").Any())
        {
            return installPath;
        }

        try
        {
            foreach (var subDir in Directory.EnumerateDirectories(installPath))
            {
                if (File.Exists(Path.Combine(subDir, "UnityPlayer.dll")) ||
                    Directory.EnumerateDirectories(subDir, "*_Data").Any())
                {
                    return subDir;
                }
            }
        }
        catch
        {
            // Ignore access errors and return installPath
        }

        return installPath;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BepInExRelease>> GetAvailableReleasesAsync(bool includePreReleases = false, CancellationToken ct = default)
    {
        if (_cachedReleases != null && DateTimeOffset.UtcNow < _cacheExpiry)
        {
            return includePreReleases
                ? _cachedReleases
                : _cachedReleases.Where(r => !r.TagName.Contains("pre", StringComparison.OrdinalIgnoreCase) &&
                                             !r.TagName.Contains("be", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        try
        {
            var url = "https://api.github.com/repos/BepInEx/BepInEx/releases?per_page=30";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var releases = new List<BepInExRelease>();

            foreach (var releaseElement in doc.RootElement.EnumerateArray())
            {
                var tagName = releaseElement.GetProperty("tag_name").GetString() ?? "";
                var isDraft = releaseElement.TryGetProperty("draft", out var draft) && draft.GetBoolean();
                if (isDraft) continue;

                var publishedAt = releaseElement.TryGetProperty("published_at", out var pub) && pub.TryGetDateTimeOffset(out var dto)
                    ? dto
                    : DateTimeOffset.UtcNow;

                if (!releaseElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                    continue;

                var architecturesAdded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    var downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                    var size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;

                    if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var nameLower = name.ToLowerInvariant();

                    // Discard Linux, macOS, Unix and non-Windows assets
                    if (nameLower.Contains("linux") ||
                        nameLower.Contains("macos") ||
                        nameLower.Contains("osx") ||
                        nameLower.Contains("unix") ||
                        nameLower.Contains("nixos") ||
                        nameLower.Contains("darwin") ||
                        nameLower.Contains("freebsd"))
                    {
                        continue;
                    }

                    var isX64 = nameLower.Contains("x64");
                    var isX86 = nameLower.Contains("x86");
                    if (!isX64 && !isX86) continue;

                    var arch = isX64 ? "x64" : "x86";
                    // Only add the first asset per architecture for each release
                    if (!architecturesAdded.Add(arch))
                        continue;

                    var cleanVer = CleanVersionString(tagName) ?? tagName.TrimStart('v', 'V');
                    releases.Add(new BepInExRelease(
                        TagName: tagName,
                        Version: cleanVer,
                        DownloadUrl: downloadUrl,
                        Architecture: arch,
                        SizeBytes: size,
                        PublishedAt: publishedAt
                    ));
                }
            }

            // Prefer x64 releases and sort by date descending
            _cachedReleases = releases
                .Where(r => r.Architecture == "x64")
                .OrderByDescending(r => r.PublishedAt)
                .ToList();

            if (_cachedReleases.Count == 0 && releases.Count > 0)
            {
                _cachedReleases = releases.OrderByDescending(r => r.PublishedAt).ToList();
            }

            _cacheExpiry = DateTimeOffset.UtcNow.Add(CacheDuration);

            return includePreReleases
                ? _cachedReleases
                : _cachedReleases.Where(r => !r.TagName.Contains("pre", StringComparison.OrdinalIgnoreCase) &&
                                             !r.TagName.Contains("be", StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch BepInEx releases from GitHub API");

            return
            [
                new BepInExRelease("v5.4.23.2", "5.4.23.2", "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_win_x64_5.4.23.2.zip", "x64", 6400000, DateTimeOffset.UtcNow),
                new BepInExRelease("v5.4.22", "5.4.22", "https://github.com/BepInEx/BepInEx/releases/download/v5.4.22/BepInEx_x64_5.4.22.0.zip", "x64", 6200000, DateTimeOffset.UtcNow),
                new BepInExRelease("v6.0.0-pre.2", "6.0.0-pre.2", "https://github.com/BepInEx/BepInEx/releases/download/v6.0.0-pre.2/BepInEx_UnityMono_x64_6.0.0-pre.2.zip", "x64", 8500000, DateTimeOffset.UtcNow)
            ];
        }
    }

    /// <inheritdoc />
    public bool IsInstalled(string gameInstallPath)
    {
        if (string.IsNullOrWhiteSpace(gameInstallPath) || !Directory.Exists(gameInstallPath))
            return false;

        var root = ResolveGameRoot(gameInstallPath);
        var winhttp = Path.Combine(root, "winhttp.dll");
        var bepDll = Path.Combine(root, "BepInEx", "core", "BepInEx.dll");
        var bepCoreDll = Path.Combine(root, "BepInEx", "core", "BepInEx.Core.dll");
        var bepPreloaderDll = Path.Combine(root, "BepInEx", "core", "BepInEx.Preloader.dll");

        // The true presence of BepInEx is determined by the loader DLL (winhttp.dll) or the core engine DLLs.
        // A leftover BepInEx/ folder (e.g. plugins preserved on uninstall) does NOT mark BepInEx as installed.
        return File.Exists(winhttp) || File.Exists(bepDll) || File.Exists(bepCoreDll) || File.Exists(bepPreloaderDll);
    }

    /// <inheritdoc />
    public static string? CleanVersionString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        while (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            s = s[1..].TrimStart();
        }
        var plusIdx = s.IndexOf('+');
        if (plusIdx >= 0) s = s[..plusIdx].Trim();
        var parenIdx = s.IndexOf('(');
        if (parenIdx >= 0) s = s[..parenIdx].Trim();
        return s;
    }

    public Task<string?> GetInstalledVersionAsync(string gameInstallPath, CancellationToken ct = default)
    {
        if (!IsInstalled(gameInstallPath))
            return Task.FromResult<string?>(null);

        var root = ResolveGameRoot(gameInstallPath);
        try
        {
            var candidateDlls = new[]
            {
                Path.Combine(root, "BepInEx", "core", "BepInEx.Core.dll"),
                Path.Combine(root, "BepInEx", "core", "BepInEx.dll"),
                Path.Combine(root, "BepInEx", "core", "BepInEx.Preloader.dll")
            };

            foreach (var dll in candidateDlls)
            {
                if (File.Exists(dll))
                {
                    var vi = FileVersionInfo.GetVersionInfo(dll);
                    var cleanProd = CleanVersionString(vi.ProductVersion);
                    if (!string.IsNullOrWhiteSpace(cleanProd))
                        return Task.FromResult<string?>(cleanProd);
                    var cleanFile = CleanVersionString(vi.FileVersion);
                    if (!string.IsNullOrWhiteSpace(cleanFile))
                        return Task.FromResult<string?>(cleanFile);
                }
            }

            // Check changelog or doorstop
            var changelog = Path.Combine(root, "changelog.txt");
            if (File.Exists(changelog))
            {
                var lines = File.ReadLines(changelog).Take(5);
                foreach (var line in lines)
                {
                    if (line.Contains("BepInEx", StringComparison.OrdinalIgnoreCase) && line.Contains('.'))
                    {
                        var cleaned = CleanVersionString(line);
                        if (!string.IsNullOrWhiteSpace(cleaned))
                            return Task.FromResult<string?>(cleaned);
                    }
                }
            }

            return Task.FromResult<string?>("5.4.23.2");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read installed BepInEx version at {Path}", gameInstallPath);
            return Task.FromResult<string?>("5.4.23.2");
        }
    }

    /// <inheritdoc />
    public async Task<bool> InstallAsync(string gameInstallPath, BepInExRelease release, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gameInstallPath) || !Directory.Exists(gameInstallPath))
        {
            _logger.LogError("Cannot install BepInEx: Target path does not exist {Path}", gameInstallPath);
            return false;
        }

        var gameRoot = ResolveGameRoot(gameInstallPath);
        var tempZip = Path.Combine(Path.GetTempPath(), $"bepinex_{Guid.NewGuid():N}.zip");
        try
        {
            _logger.LogInformation("Downloading BepInEx {Version} from {Url}", release.Version, release.DownloadUrl);
            progress?.Report(10.0);

            using (var resp = await _http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                var totalBytes = resp.Content.Headers.ContentLength ?? 1;

                using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None);

                var buffer = new byte[81920];
                long totalRead = 0;
                int read;

                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                    totalRead += read;
                    if (totalBytes > 0)
                    {
                        var pct = 10.0 + (totalRead / (double)totalBytes * 60.0);
                        progress?.Report(Math.Min(70.0, pct));
                    }
                }
            }

            progress?.Report(75.0);
            _logger.LogInformation("Extracting BepInEx to {Target}", gameRoot);

            using (var zip = ZipFile.OpenRead(tempZip))
            {
                // Detect if the archive has a single root wrapper directory
                string? commonPrefix = null;
                var firstEntry = zip.Entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.Name));
                if (firstEntry != null)
                {
                    var slashIdx = firstEntry.FullName.IndexOfAny(['/', '\\']);
                    if (slashIdx > 0)
                    {
                        var potentialRoot = firstEntry.FullName.Substring(0, slashIdx + 1);
                        if (!potentialRoot.StartsWith("BepInEx", StringComparison.OrdinalIgnoreCase) &&
                            zip.Entries.All(e => e.FullName.StartsWith(potentialRoot, StringComparison.OrdinalIgnoreCase)))
                        {
                            commonPrefix = potentialRoot;
                        }
                    }
                }

                foreach (var entry in zip.Entries)
                {
                    var relPath = entry.FullName;
                    if (!string.IsNullOrEmpty(commonPrefix) && relPath.StartsWith(commonPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        relPath = relPath.Substring(commonPrefix.Length);
                    }

                    if (string.IsNullOrWhiteSpace(relPath)) continue;

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        // Directory entry
                        var dirPath = Path.Combine(gameRoot, relPath);
                        Directory.CreateDirectory(dirPath);
                        continue;
                    }

                    var destPath = Path.Combine(gameRoot, relPath);
                    var parentDir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(parentDir))
                        Directory.CreateDirectory(parentDir);

                    entry.ExtractToFile(destPath, overwrite: true);
                }
            }

            // Ensure BepInEx/plugins and BepInEx/config exist
            Directory.CreateDirectory(Path.Combine(gameRoot, "BepInEx", "plugins"));
            Directory.CreateDirectory(Path.Combine(gameRoot, "BepInEx", "config"));

            progress?.Report(100.0);
            _logger.LogInformation("BepInEx {Version} installed successfully", release.Version);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to install BepInEx to {Path}", gameRoot);
            return false;
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    /// <inheritdoc />
    public Task<bool> UninstallAsync(string gameInstallPath, bool keepPluginsFolder = true, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gameInstallPath) || !Directory.Exists(gameInstallPath))
            return Task.FromResult(false);

        var gameRoot = ResolveGameRoot(gameInstallPath);
        try
        {
            // Remove loader hooks and config files
            var filesToDelete = new[]
            {
                Path.Combine(gameRoot, "winhttp.dll"),
                Path.Combine(gameRoot, "doorstop_config.ini"),
                Path.Combine(gameRoot, "doorstop_config.json"),
                Path.Combine(gameRoot, "changelog.txt"),
                Path.Combine(gameRoot, "run_bepinex.sh"),
                Path.Combine(gameRoot, "doorstop_version")
            };

            foreach (var f in filesToDelete)
            {
                if (File.Exists(f))
                {
                    try { File.Delete(f); } catch { }
                }
            }

            var bepDir = Path.Combine(gameRoot, "BepInEx");
            if (Directory.Exists(bepDir))
            {
                if (keepPluginsFolder)
                {
                    // Clean only core, patchers, and cache
                    var coreDir = Path.Combine(bepDir, "core");
                    if (Directory.Exists(coreDir)) Directory.Delete(coreDir, recursive: true);

                    var patchersDir = Path.Combine(bepDir, "patchers");
                    if (Directory.Exists(patchersDir)) Directory.Delete(patchersDir, recursive: true);

                    var cacheDir = Path.Combine(bepDir, "cache");
                    if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
                }
                else
                {
                    Directory.Delete(bepDir, recursive: true);
                }
            }

            _logger.LogInformation("BepInEx uninstalled from {Path}", gameRoot);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to uninstall BepInEx from {Path}", gameRoot);
            return Task.FromResult(false);
        }
    }
}
