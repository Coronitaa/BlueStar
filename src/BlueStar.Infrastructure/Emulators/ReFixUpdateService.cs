using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Emulators;

/// <summary>
/// Service responsible for automatic background updates of the ReFix emulator suite from GitHub.
/// </summary>
public sealed class ReFixUpdateService : IReFixUpdateService
{
    private const string GitHubApiUrl = "https://api.github.com/repos/Coronitaa/ReFix/releases/latest";
    private const string DefaultVersion = "1.1";

    private readonly HttpClient _httpClient;
    private readonly IInstanceManager? _instanceManager;
    private readonly INotificationService? _notificationService;
    private readonly ILogger<ReFixUpdateService> _logger;

    public ReFixUpdateService(
        HttpClient httpClient,
        ILogger<ReFixUpdateService> logger,
        INotificationService? notificationService = null,
        IInstanceManager? instanceManager = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _notificationService = notificationService;
        _instanceManager = instanceManager;
    }

    /// <inheritdoc />
    public string GetCurrentInstalledVersion()
    {
        var deployPath = ReFixEmulator.GetReFixDeployPath();
        if (deployPath == null) return DefaultVersion;

        var versionFile = Path.Combine(deployPath, "refix_version.json");
        if (File.Exists(versionFile))
        {
            try
            {
                var json = File.ReadAllText(versionFile);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("version", out var v) && v.GetString() is string ver)
                {
                    return ver.Trim().TrimStart('v', 'V');
                }
            }
            catch { }
        }

        return DefaultVersion;
    }

    /// <inheritdoc />
    public async Task<ReFixVersionInfo?> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Checking for ReFix updates from GitHub ({Url})...", GitHubApiUrl);

            using var request = new HttpRequestMessage(HttpMethod.Get, GitHubApiUrl);
            request.Headers.UserAgent.ParseAdd("BlueStar-Launcher/0.1.0");

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("GitHub ReFix releases API returned {StatusCode}", response.StatusCode);
                return null;
            }

            var release = await response.Content.ReadFromJsonAsync<GitHubReleaseDto>(cancellationToken: ct).ConfigureAwait(false);
            if (release == null || string.IsNullOrWhiteSpace(release.TagName))
                return null;

            var remoteVersionStr = release.TagName.Trim().TrimStart('v', 'V');
            var localVersionStr = GetCurrentInstalledVersion();

            _logger.LogInformation("ReFix Version Check — Local: v{Local}, Remote: v{Remote}", localVersionStr, remoteVersionStr);

            if (IsNewerVersion(remoteVersionStr, localVersionStr))
            {
                var zipAsset = release.Assets?.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                var downloadUrl = zipAsset?.BrowserDownloadUrl ?? release.ZipballUrl;

                if (!string.IsNullOrWhiteSpace(downloadUrl))
                {
                    return new ReFixVersionInfo
                    {
                        Version = remoteVersionStr,
                        TagName = release.TagName,
                        ReleaseNotes = release.Body ?? string.Empty,
                        DownloadUrl = downloadUrl,
                        FileSize = zipAsset?.Size ?? 0,
                        PublishedAt = release.PublishedAt ?? DateTimeOffset.UtcNow
                    };
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check for ReFix updates");
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DownloadAndApplyUpdateAsync(
        ReFixVersionInfo update,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        _logger.LogInformation("Downloading ReFix update v{Version} from {Url}...", update.Version, update.DownloadUrl);

        var tempZipPath = Path.Combine(Path.GetTempPath(), "BlueStar_ReFix_Update", $"ReFix_v{update.Version}.zip");
        var tempExtractDir = Path.Combine(Path.GetTempPath(), "BlueStar_ReFix_Update", $"extract_{update.Version}");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tempZipPath)!);
            if (Directory.Exists(tempExtractDir)) Directory.Delete(tempExtractDir, recursive: true);
            Directory.CreateDirectory(tempExtractDir);

            // 1. Download zip asset
            using var response = await _httpClient.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? update.FileSize;
            await using (var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                    totalRead += bytesRead;

                    if (totalBytes > 0 && progress != null)
                    {
                        var pct = (double)totalRead / totalBytes * 100.0;
                        progress.Report(new DownloadProgress
                        {
                            TotalBytes = totalBytes,
                            DownloadedBytes = totalRead,
                            Percentage = pct,
                            CurrentFile = $"ReFix_v{update.Version}.zip"
                        });
                    }
                }
            }

            _logger.LogInformation("Downloaded ReFix update zip ({Bytes} bytes). Extracting...", new FileInfo(tempZipPath).Length);

            // 2. Extract ZIP
            ZipFile.ExtractToDirectory(tempZipPath, tempExtractDir, overwriteFiles: true);

            // Locate the extracted files root (handle cases where zip contains a subfolder like ReFix_deploy or Coronitaa-ReFix-*)
            var sourceDeployDir = FindDeployRoot(tempExtractDir);

            // 3. Find target ReFix_deploy directories to update
            var targetDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var currentDeploy = ReFixEmulator.GetReFixDeployPath();
            if (!string.IsNullOrEmpty(currentDeploy))
                targetDirs.Add(currentDeploy);

            // Also check standard paths in AppContext / Source
            var defaultToolsPath = Path.Combine(AppContext.BaseDirectory, "tools", "ReFix_deploy");
            targetDirs.Add(defaultToolsPath);

            var srcToolsPath = Path.Combine(Directory.GetCurrentDirectory(), "src", "BlueStar.App", "tools", "ReFix_deploy");
            if (Directory.Exists(Path.GetDirectoryName(srcToolsPath)))
                targetDirs.Add(srcToolsPath);

            foreach (var target in targetDirs)
            {
                _logger.LogInformation("Applying ReFix v{Version} update to directory: {Target}", update.Version, target);
                CopyDirectoryRecursive(sourceDeployDir, target);

                // Write refix_version.json
                var versionInfoPath = Path.Combine(target, "refix_version.json");
                var versionData = new
                {
                    version = update.Version,
                    tag = update.TagName,
                    updatedAt = DateTimeOffset.UtcNow,
                    downloadUrl = update.DownloadUrl
                };
                File.WriteAllText(versionInfoPath, JsonSerializer.Serialize(versionData, new JsonSerializerOptions { WriteIndented = true }));
            }

            _logger.LogInformation("ReFix update v{Version} successfully applied!", update.Version);

            _notificationService?.ShowSuccess(
                "ReFix Actualizado",
                $"El emulador ReFix se ha actualizado a la versión {update.Version} en segundo plano.",
                TimeSpan.FromSeconds(8));

            // Check if any instances are outdated and notify
            await CheckAndNotifyOutdatedInstancesAsync(ct).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download and apply ReFix update");
            return false;
        }
        finally
        {
            try { if (File.Exists(tempZipPath)) File.Delete(tempZipPath); } catch { }
            try { if (Directory.Exists(tempExtractDir)) Directory.Delete(tempExtractDir, recursive: true); } catch { }
        }
    }

    /// <inheritdoc />
    public async Task CheckAndPerformAutoUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            var update = await CheckForUpdatesAsync(ct).ConfigureAwait(false);
            if (update != null)
            {
                _logger.LogInformation("New ReFix update detected: v{Version}. Starting background download...", update.Version);
                _notificationService?.ShowInfo(
                    "Actualización de ReFix",
                    $"Nueva versión v{update.Version} encontrada. Descargando en segundo plano...",
                    TimeSpan.FromSeconds(5));

                var applied = await DownloadAndApplyUpdateAsync(update, null, ct).ConfigureAwait(false);
                if (applied)
                {
                    await CheckAndNotifyOutdatedInstancesAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during ReFix auto-update check");
        }
    }

    /// <inheritdoc />
    public async Task CheckAndNotifyOutdatedInstancesAsync(CancellationToken ct = default)
    {
        if (_instanceManager == null) return;

        try
        {
            var instances = await _instanceManager.GetAllAsync(ct).ConfigureAwait(false);
            var currentVersion = GetCurrentInstalledVersion();

            foreach (var instance in instances)
            {
                if (IsInstanceReFixOutdated(instance))
                {
                    var instVersion = instance.InstalledEmulatorVersion ?? "anterior";
                    _notificationService?.ShowWarning(
                        "Actualización de ReFix en instancia",
                        $"La instancia '{instance.Name}' tiene ReFix v{instVersion}. Nueva versión v{currentVersion} lista para instalar.",
                        TimeSpan.FromSeconds(10),
                        "Actualizar",
                        () =>
                        {
                            _logger.LogInformation("User clicked update ReFix notification for instance {Name}", instance.Name);
                        });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error scanning instances for outdated ReFix");
        }
    }

    /// <inheritdoc />
    public bool IsInstanceReFixOutdated(GameInstance instance)
    {
        if (instance == null || string.IsNullOrWhiteSpace(instance.InstallPath)) return false;

        // Check if emulator is installed on disk or enabled
        bool isInstalled = ReFixEmulator.IsEmulatorInstalled(instance.InstallPath) || instance.EmulatorEnabled;
        if (!isInstalled) return false;

        var currentVersion = GetCurrentInstalledVersion();
        var installedVersion = instance.InstalledEmulatorVersion;

        if (string.IsNullOrWhiteSpace(installedVersion))
        {
            return false;
        }

        return IsNewerVersion(currentVersion, installedVersion);
    }

    /// <summary>
    /// Compares two semver-like version strings using normalized component comparison.
    /// Returns true ONLY if candidate is strictly newer than baseline.
    /// </summary>
    public static bool IsNewerVersion(string? candidate, string? baseline)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        if (string.IsNullOrWhiteSpace(baseline)) return true;

        var vCand = NormalizeVersion(candidate);
        var vBase = NormalizeVersion(baseline);

        return vCand > vBase;
    }

    private static Version NormalizeVersion(string ver)
    {
        if (string.IsNullOrWhiteSpace(ver)) return new Version(0, 0, 0, 0);

        ver = ver.Trim().TrimStart('v', 'V');
        var parts = ver.Split(new[] { '.', '-', '+', '_' }, StringSplitOptions.RemoveEmptyEntries);

        int major = parts.Length > 0 && int.TryParse(parts[0], out var maj) ? maj : 0;
        int minor = parts.Length > 1 && int.TryParse(parts[1], out var min) ? min : 0;
        int build = parts.Length > 2 && int.TryParse(parts[2], out var bld) ? bld : 0;
        int rev = parts.Length > 3 && int.TryParse(parts[3], out var r) ? r : 0;

        return new Version(major, minor, build, rev);
    }

    private static string FindDeployRoot(string extractedFolder)
    {
        // 1. Direct match with bin/steam_api64.dll
        if (File.Exists(Path.Combine(extractedFolder, "bin", "steam_api64.dll")))
            return extractedFolder;

        // 2. Subfolder ReFix_deploy
        var subDeploy = Path.Combine(extractedFolder, "ReFix_deploy");
        if (Directory.Exists(subDeploy) && File.Exists(Path.Combine(subDeploy, "bin", "steam_api64.dll")))
            return subDeploy;

        // 3. Search child directories
        var match = Directory.GetFiles(extractedFolder, "steam_api64.dll", SearchOption.AllDirectories)
            .FirstOrDefault(f => Path.GetFileName(Path.GetDirectoryName(f) ?? "") == "bin");

        if (match != null)
        {
            var binDir = Path.GetDirectoryName(match);
            var parent = Path.GetDirectoryName(binDir);
            if (!string.IsNullOrEmpty(parent)) return parent;
        }

        return extractedFolder;
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var targetFile = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, targetFile, overwrite: true);
        }

        foreach (var directory in Directory.GetDirectories(sourceDir))
        {
            var targetSubDir = Path.Combine(targetDir, Path.GetFileName(directory));
            CopyDirectoryRecursive(directory, targetSubDir);
        }
    }

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; set; }

        [JsonPropertyName("zipball_url")]
        public string? ZipballUrl { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAssetDto>? Assets { get; set; }
    }

    private sealed class GitHubAssetDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }
}

