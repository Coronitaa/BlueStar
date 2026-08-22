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

                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    var downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                    var size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;

                    // Match Windows x64 and x86 builds (BepInEx_win_x64_*.zip or BepInEx_x64_*.zip)
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                        (name.Contains("x64", StringComparison.OrdinalIgnoreCase) || name.Contains("win_x64", StringComparison.OrdinalIgnoreCase)))
                    {
                        var cleanVer = tagName.TrimStart('v', 'V');
                        releases.Add(new BepInExRelease(
                            TagName: tagName,
                            Version: $"{cleanVer} (x64)",
                            DownloadUrl: downloadUrl,
                            Architecture: "x64",
                            SizeBytes: size,
                            PublishedAt: publishedAt
                        ));
                    }
                    else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                             (name.Contains("x86", StringComparison.OrdinalIgnoreCase) || name.Contains("win_x86", StringComparison.OrdinalIgnoreCase)))
                    {
                        var cleanVer = tagName.TrimStart('v', 'V');
                        releases.Add(new BepInExRelease(
                            TagName: tagName,
                            Version: $"{cleanVer} (x86)",
                            DownloadUrl: downloadUrl,
                            Architecture: "x86",
                            SizeBytes: size,
                            PublishedAt: publishedAt
                        ));
                    }
                }
            }

            _cachedReleases = releases.OrderByDescending(r => r.PublishedAt).ToList();
            _cacheExpiry = DateTimeOffset.UtcNow.Add(CacheDuration);

            return includePreReleases
                ? _cachedReleases
                : _cachedReleases.Where(r => !r.TagName.Contains("pre", StringComparison.OrdinalIgnoreCase) &&
                                             !r.TagName.Contains("be", StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch BepInEx releases from GitHub API");

            // Return hardcoded stable fallback releases if offline or rate limited
            return
            [
                new BepInExRelease("v5.4.23.2", "5.4.23.2 (x64)", "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_win_x64_5.4.23.2.zip", "x64", 6400000, DateTimeOffset.UtcNow),
                new BepInExRelease("v5.4.22", "5.4.22 (x64)", "https://github.com/BepInEx/BepInEx/releases/download/v5.4.22/BepInEx_x64_5.4.22.0.zip", "x64", 6200000, DateTimeOffset.UtcNow),
                new BepInExRelease("v6.0.0-pre.2", "6.0.0-pre.2 (x64)", "https://github.com/BepInEx/BepInEx/releases/download/v6.0.0-pre.2/BepInEx_UnityMono_x64_6.0.0-pre.2.zip", "x64", 8500000, DateTimeOffset.UtcNow)
            ];
        }
    }

    /// <inheritdoc />
    public bool IsInstalled(string gameInstallPath)
    {
        if (string.IsNullOrWhiteSpace(gameInstallPath) || !Directory.Exists(gameInstallPath))
            return false;

        var winhttp = Path.Combine(gameInstallPath, "winhttp.dll");
        var bepDir = Path.Combine(gameInstallPath, "BepInEx");
        return File.Exists(winhttp) || Directory.Exists(bepDir);
    }

    /// <inheritdoc />
    public Task<string?> GetInstalledVersionAsync(string gameInstallPath, CancellationToken ct = default)
    {
        if (!IsInstalled(gameInstallPath))
            return Task.FromResult<string?>(null);

        try
        {
            var candidateDlls = new[]
            {
                Path.Combine(gameInstallPath, "BepInEx", "core", "BepInEx.Core.dll"),
                Path.Combine(gameInstallPath, "BepInEx", "core", "BepInEx.dll"),
                Path.Combine(gameInstallPath, "BepInEx", "core", "BepInEx.Preloader.dll")
            };

            foreach (var dll in candidateDlls)
            {
                if (File.Exists(dll))
                {
                    var vi = FileVersionInfo.GetVersionInfo(dll);
                    if (!string.IsNullOrWhiteSpace(vi.ProductVersion))
                        return Task.FromResult<string?>($"v{vi.ProductVersion.TrimStart('v', 'V')}");
                    if (!string.IsNullOrWhiteSpace(vi.FileVersion))
                        return Task.FromResult<string?>($"v{vi.FileVersion.TrimStart('v', 'V')}");
                }
            }

            // Check changelog or doorstop
            var changelog = Path.Combine(gameInstallPath, "changelog.txt");
            if (File.Exists(changelog))
            {
                var lines = File.ReadLines(changelog).Take(5);
                foreach (var line in lines)
                {
                    if (line.Contains("BepInEx", StringComparison.OrdinalIgnoreCase) && line.Contains('.'))
                        return Task.FromResult<string?>(line.Trim());
                }
            }

            return Task.FromResult<string?>("v5.x");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read installed BepInEx version at {Path}", gameInstallPath);
            return Task.FromResult<string?>("Installed");
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
            _logger.LogInformation("Extracting BepInEx to {Target}", gameInstallPath);

            using (var zip = ZipFile.OpenRead(tempZip))
            {
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        // Directory entry
                        var dirPath = Path.Combine(gameInstallPath, entry.FullName);
                        Directory.CreateDirectory(dirPath);
                        continue;
                    }

                    var destPath = Path.Combine(gameInstallPath, entry.FullName);
                    var parentDir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(parentDir))
                        Directory.CreateDirectory(parentDir);

                    entry.ExtractToFile(destPath, overwrite: true);
                }
            }

            // Ensure BepInEx/plugins and BepInEx/config exist
            Directory.CreateDirectory(Path.Combine(gameInstallPath, "BepInEx", "plugins"));
            Directory.CreateDirectory(Path.Combine(gameInstallPath, "BepInEx", "config"));

            progress?.Report(100.0);
            _logger.LogInformation("BepInEx {Version} installed successfully", release.Version);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to install BepInEx to {Path}", gameInstallPath);
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

        try
        {
            // Remove loader hooks and config files
            var filesToDelete = new[]
            {
                Path.Combine(gameInstallPath, "winhttp.dll"),
                Path.Combine(gameInstallPath, "doorstop_config.ini"),
                Path.Combine(gameInstallPath, "doorstop_config.json"),
                Path.Combine(gameInstallPath, "changelog.txt"),
                Path.Combine(gameInstallPath, "run_bepinex.sh"),
                Path.Combine(gameInstallPath, "doorstop_version")
            };

            foreach (var f in filesToDelete)
            {
                if (File.Exists(f))
                {
                    try { File.Delete(f); } catch { }
                }
            }

            var bepDir = Path.Combine(gameInstallPath, "BepInEx");
            if (Directory.Exists(bepDir))
            {
                if (keepPluginsFolder)
                {
                    // Clean only core, patchers, and cache
                    var coreDir = Path.Combine(bepDir, "core");
                    if (Directory.Exists(coreDir)) Directory.Delete(coreDir, recursive: true);

                    var patchersDir = Path.Combine(bepDir, "patchers");
                    if (Directory.Exists(patchersDir)) Directory.Delete(patchersDir, recursive: true);
                }
                else
                {
                    Directory.Delete(bepDir, recursive: true);
                }
            }

            _logger.LogInformation("BepInEx uninstalled from {Path}", gameInstallPath);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to uninstall BepInEx from {Path}", gameInstallPath);
            return Task.FromResult(false);
        }
    }
}
