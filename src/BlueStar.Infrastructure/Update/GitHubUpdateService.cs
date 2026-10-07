using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Update;

/// <summary>
/// Implements <see cref="IUpdateService"/> using GitHub Releases API.
/// Checks for new versions from Coronitaa/BlueStar releases, downloads packages, and applies updates with auto-restart.
/// </summary>
public class GitHubUpdateService : IUpdateService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GitHubUpdateService> _logger;
    private readonly ICacheService? _cacheService;
    private readonly IRequestCoordinator _coordinator;
    private readonly INetworkMetricsObserver? _metrics;
    private const string GitHubReleasesUrl = "https://api.github.com/repos/Coronitaa/BlueStar/releases/latest";

    private sealed record CachedUpdateCheck(UpdateInfo? Update, bool HasUpdate);

    private static string UpdatesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "BlueStar", "updates");

    private static string PendingUpdateMarkerPath => Path.Combine(UpdatesDirectory, "pending_update.json");

    /// <summary>
    /// Initializes a new instance of the <see cref="GitHubUpdateService"/> class.
    /// </summary>
    public GitHubUpdateService(
        HttpClient httpClient,
        ILogger<GitHubUpdateService> logger,
        ICacheService? cacheService = null,
        IRequestCoordinator? coordinator = null,
        INetworkMetricsObserver? metrics = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _cacheService = cacheService;
        _coordinator = coordinator ?? BlueStar.Infrastructure.Services.RequestCoordinator.Instance;
        _metrics = metrics;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, System.Text.StringBuilder? packageFullName);

    /// <inheritdoc />
    public bool IsPackaged => IsRunningAsPackaged();

    /// <summary>
    /// Detects whether the current process is running inside an MSIX package (such as the Microsoft Store build).
    /// </summary>
    public static bool IsRunningAsPackaged()
    {
        if (AppContext.BaseDirectory.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            int length = 0;
            int result = GetCurrentPackageFullName(ref length, null);
            // APPMODEL_ERROR_NO_PACKAGE = 15700 (0x3D54)
            // ERROR_INSUFFICIENT_BUFFER = 122 (0x7A) - indicates package identity exists
            return result == 0 || result == 122;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void OpenStoreForUpdates()
    {
        try
        {
            _logger.LogInformation("Opening Microsoft Store for BlueStar updates (Product ID: 9N9HHDBXT4PW)...");
            Process.Start(new ProcessStartInfo("ms-windows-store://pdp/?productid=9N9HHDBXT4PW")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to launch ms-windows-store protocol, falling back to web URL...");
            try
            {
                Process.Start(new ProcessStartInfo("https://apps.microsoft.com/detail/9N9HHDBXT4PW")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception webEx)
            {
                _logger.LogError(webEx, "Failed to open Microsoft Store web page.");
            }
        }
    }

    /// <summary>
    /// Checks if a downloaded update from a previous session is waiting to be installed.
    /// </summary>
    public static string? GetPendingUpdateFilePath()
    {
        if (IsRunningAsPackaged())
        {
            try { if (File.Exists(PendingUpdateMarkerPath)) File.Delete(PendingUpdateMarkerPath); } catch { }
            return null;
        }

        try
        {
            if (!File.Exists(PendingUpdateMarkerPath)) return null;

            var json = File.ReadAllText(PendingUpdateMarkerPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("filePath", out var pathProp))
            {
                var path = pathProp.GetString();
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    return path;
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Gets the normalized three-component version (Major.Minor.Build) of the running BlueStar instance.
    /// </summary>
    public static Version GetCurrentVersion()
    {
        var rawCurrent = Assembly.GetEntryAssembly()?.GetName().Version
                      ?? Assembly.GetExecutingAssembly().GetName().Version
                      ?? typeof(GitHubUpdateService).Assembly.GetName().Version;

        if (rawCurrent == null || rawCurrent == new Version(0, 0, 0, 0))
        {
            try
            {
                var mainModulePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(mainModulePath) && File.Exists(mainModulePath))
                {
                    var fvi = FileVersionInfo.GetVersionInfo(mainModulePath);
                    if (!string.IsNullOrEmpty(fvi.ProductVersion))
                    {
                        var cleanProd = fvi.ProductVersion.Split('+')[0].Trim().TrimStart('v');
                        if (Version.TryParse(cleanProd, out var pv))
                        {
                            rawCurrent = pv;
                        }
                    }
                }
            }
            catch { }
        }

        rawCurrent ??= new Version(1, 4, 3);

        return new Version(
            Math.Max(0, rawCurrent.Major),
            Math.Max(0, rawCurrent.Minor),
            Math.Max(0, rawCurrent.Build));
    }

    /// <summary>
    /// Checks whether a candidate version string represents a version strictly greater than currentVersion.
    /// </summary>
    public static bool IsNewerVersion(string? candidateVersionStr, Version currentVersion)
    {
        if (string.IsNullOrWhiteSpace(candidateVersionStr)) return false;
        var clean = candidateVersionStr.Trim().TrimStart('v').Trim();
        if (!Version.TryParse(clean, out var candidate)) return false;

        var normalizedCandidate = new Version(
            Math.Max(0, candidate.Major),
            Math.Max(0, candidate.Minor),
            Math.Max(0, candidate.Build));

        var normalizedCurrent = new Version(
            Math.Max(0, currentVersion.Major),
            Math.Max(0, currentVersion.Minor),
            Math.Max(0, currentVersion.Build));

        return normalizedCandidate > normalizedCurrent;
    }

    /// <inheritdoc />
    public async Task<UpdateInfo?> CheckForUpdatesAsync(CancellationToken ct)
    {
        var currentVersion = GetCurrentVersion();
        var cacheKey = $"github_update_check_v{currentVersion.Major}_{currentVersion.Minor}_{currentVersion.Build}";

        if (_cacheService != null)
        {
            try
            {
                var cached = await _cacheService.GetAsync<CachedUpdateCheck>(cacheKey, ct).ConfigureAwait(false);
                if (cached != null)
                {
                    _metrics?.OnCacheHit("GitHub", GitHubReleasesUrl);
                    if (cached.Update != null && IsNewerVersion(cached.Update.Version, currentVersion))
                    {
                        return cached.Update;
                    }
                    return null;
                }
            }
            catch { }
        }

        return await _coordinator.ExecuteAsync(cacheKey, async innerCt =>
        {
            if (_cacheService != null)
            {
                try
                {
                    var cached = await _cacheService.GetAsync<CachedUpdateCheck>(cacheKey, innerCt).ConfigureAwait(false);
                    if (cached != null)
                    {
                        _metrics?.OnCacheHit("GitHub", GitHubReleasesUrl);
                        if (cached.Update != null && IsNewerVersion(cached.Update.Version, currentVersion))
                        {
                            return cached.Update;
                        }
                        return null;
                    }
                }
                catch { }
            }

            try
            {
                _logger.LogInformation("Checking for updates from GitHub Releases...");
                _metrics?.OnProviderRequest("GitHub", GitHubReleasesUrl);

                GitHubRelease? release = null;
                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, GitHubReleasesUrl);
                    request.Headers.UserAgent.ParseAdd("BlueStar-Updater/1.4.4");
                    var response = await _httpClient.SendAsync(request, innerCt).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken: innerCt).ConfigureAwait(false);
                    }
                }
                catch { }

                if (release is null || string.IsNullOrWhiteSpace(release.TagName))
                {
                    try
                    {
                        const string fallbackUrl = "https://api.github.com/repos/Coronitaa/BlueStar/releases";
                        var request = new HttpRequestMessage(HttpMethod.Get, fallbackUrl);
                        request.Headers.UserAgent.ParseAdd("BlueStar-Updater/1.4.4");
                        var response = await _httpClient.SendAsync(request, innerCt).ConfigureAwait(false);
                        if (response.IsSuccessStatusCode)
                        {
                            var releases = await response.Content.ReadFromJsonAsync<List<GitHubRelease>>(cancellationToken: innerCt).ConfigureAwait(false);
                            release = releases?.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.TagName));
                        }
                    }
                    catch { }
                }

                if (release is null || string.IsNullOrWhiteSpace(release.TagName))
                {
                    if (_cacheService != null)
                    {
                        try { await _cacheService.SetAsync(cacheKey, new CachedUpdateCheck(null, false), TimeSpan.FromMinutes(2), innerCt).ConfigureAwait(false); } catch { }
                    }
                    return null;
                }

                var latestVersionStr = release.TagName.Trim().TrimStart('v').Trim();
                if (!Version.TryParse(latestVersionStr, out var latestVersion))
                    return null;

                var normalizedLatest = new Version(
                    Math.Max(0, latestVersion.Major),
                    Math.Max(0, latestVersion.Minor),
                    Math.Max(0, latestVersion.Build));

                if (normalizedLatest > currentVersion)
                {
                    var isInstalled = File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));
                    GitHubAsset? asset = null;
                    if (isInstalled)
                    {
                        asset = release.Assets?.FirstOrDefault(a => a.Name.EndsWith("-Setup-win-x64.exe", StringComparison.OrdinalIgnoreCase) || a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                             ?? release.Assets?.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        asset = release.Assets?.FirstOrDefault(a => a.Name.EndsWith("-Portable-win-x64.zip", StringComparison.OrdinalIgnoreCase) || a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                             ?? release.Assets?.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                    }

                    _logger.LogInformation("New update available: v{Version} (Current: v{CurrentVersion})", latestVersion, currentVersion);

                    var updateInfo = new UpdateInfo
                    {
                        Version = latestVersion.ToString(),
                        ReleaseNotes = release.Body ?? string.Empty,
                        DownloadUrl = asset?.BrowserDownloadUrl ?? string.Empty,
                        FileSize = asset?.Size ?? 0,
                        PublishedAt = DateTimeOffset.UtcNow
                    };

                    if (_cacheService != null)
                    {
                        try { await _cacheService.SetAsync(cacheKey, new CachedUpdateCheck(updateInfo, true), TimeSpan.FromHours(4), innerCt).ConfigureAwait(false); } catch { }
                    }

                    return updateInfo;
                }

                _logger.LogInformation("BlueStar is up to date (Version: v{CurrentVersion})", currentVersion);
                if (_cacheService != null)
                {
                    try { await _cacheService.SetAsync(cacheKey, new CachedUpdateCheck(null, false), TimeSpan.FromHours(4), innerCt).ConfigureAwait(false); } catch { }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check for updates");
                return null;
            }
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<string> DownloadUpdateAsync(UpdateInfo update, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (IsPackaged)
        {
            _logger.LogInformation("Application is packaged as Microsoft Store MSIX. Redirecting to Store rather than downloading standalone installer.");
            OpenStoreForUpdates();
            return string.Empty;
        }

        if (update is null) throw new ArgumentNullException(nameof(update));
        if (string.IsNullOrWhiteSpace(update.DownloadUrl))
            throw new ArgumentException("Update DownloadUrl cannot be empty.", nameof(update));

        Directory.CreateDirectory(UpdatesDirectory);
        var ext = Path.GetExtension(new Uri(update.DownloadUrl).AbsolutePath);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".exe";

        var destPath = Path.Combine(UpdatesDirectory, $"BlueStar-v{update.Version}{ext}");

        _logger.LogInformation("Downloading update v{Version} from {Url}", update.Version, update.DownloadUrl);

        using var response = await _httpClient.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? update.FileSize;
        using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[8192];
        long totalRead = 0;
        int bytesRead;

        var stopwatch = Stopwatch.StartNew();

        while ((bytesRead = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
            totalRead += bytesRead;

            if (totalBytes > 0 && progress is not null)
            {
                var elapsedSec = stopwatch.Elapsed.TotalSeconds;
                var bps = elapsedSec > 0 ? totalRead / elapsedSec : 0;
                var pct = (double)totalRead / totalBytes * 100.0;

                progress.Report(new DownloadProgress
                {
                    TotalBytes = totalBytes,
                    DownloadedBytes = totalRead,
                    Speed = bps,
                    Percentage = pct,
                    CurrentFile = Path.GetFileName(destPath)
                });
            }
        }

        // Save pending update marker so relaunch can apply if app closes
        try
        {
            var marker = JsonSerializer.Serialize(new
            {
                version = update.Version,
                filePath = destPath,
                downloadedAt = DateTimeOffset.UtcNow
            });
            File.WriteAllText(PendingUpdateMarkerPath, marker);
        }
        catch { }

        _logger.LogInformation("Update downloaded to {Path}", destPath);
        return destPath;
    }

    /// <inheritdoc />
    public Task ApplyUpdateAsync(string updateFilePath, CancellationToken ct)
    {
        if (IsPackaged)
        {
            _logger.LogInformation("Applying update in packaged mode: Opening Microsoft Store.");
            OpenStoreForUpdates();
            return Task.CompletedTask;
        }

        if (string.IsNullOrWhiteSpace(updateFilePath) || !File.Exists(updateFilePath))
            throw new FileNotFoundException("Update installer file not found.", updateFilePath);

        _logger.LogInformation("Applying update: {Path}", updateFilePath);

        // Remove marker
        try
        {
            if (File.Exists(PendingUpdateMarkerPath))
                File.Delete(PendingUpdateMarkerPath);
        }
        catch { }

        // Also invalidate update cache so next launch checks afresh or sees up-to-date state
        try
        {
            var curVer = GetCurrentVersion();
            var cKey = $"github_update_check_v{curVer.Major}_{curVer.Minor}_{curVer.Build}";
            _ = _cacheService?.RemoveAsync(cKey, CancellationToken.None);
            _ = _cacheService?.RemoveAsync("github_update_check_latest", CancellationToken.None);
        }
        catch { }

        var appDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var mainExe = Process.GetCurrentProcess().MainModule?.FileName ?? Path.Combine(appDir, "BlueStar.exe");
        var pid = Environment.ProcessId;

        var batScript = Path.Combine(Path.GetTempPath(), $"bluestar_update_{Guid.NewGuid():N}.bat");

        if (updateFilePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            // Installer or standalone updater script
            var batContent = $@"@echo off
timeout /t 1 /nobreak > nul
:WAIT_PID
tasklist /FI ""PID eq {pid}"" 2>NUL | find /I ""{pid}"" >NUL
if not errorlevel 1 (
    timeout /t 1 /nobreak > nul
    goto WAIT_PID
)
start /wait """" ""{updateFilePath}"" /SILENT /SUPPRESSMSGBOXES
start """" ""{mainExe}""
del ""%~f0"" & exit
";
            File.WriteAllText(batScript, batContent);
        }
        else
        {
            // Zip extraction script
            var batContent = $@"@echo off
timeout /t 1 /nobreak > nul
:WAIT_PID
tasklist /FI ""PID eq {pid}"" 2>NUL | find /I ""{pid}"" >NUL
if not errorlevel 1 (
    timeout /t 1 /nobreak > nul
    goto WAIT_PID
)
tar -xf ""{updateFilePath}"" -C ""{appDir}""
start """" ""{mainExe}""
del ""%~f0"" & exit
";
            File.WriteAllText(batScript, batContent);
        }

        var psi = new ProcessStartInfo
        {
            FileName = batScript,
            UseShellExecute = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        Process.Start(psi);
        Environment.Exit(0);
        return Task.CompletedTask;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}
