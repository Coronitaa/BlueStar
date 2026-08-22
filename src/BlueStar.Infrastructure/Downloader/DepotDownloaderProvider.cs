using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Downloader;

/// <summary>
/// Implements <see cref="IDownloadProvider"/> by wrapping the DepotDownloaderMod CLI tool.
/// Supports anonymous downloads using local .manifest files and depot keys extracted from the DepotBox ZIP.
/// Includes robust speed tracking, resilient pause/resume, and automatic corrupted cache recovery.
/// </summary>
public partial class DepotDownloaderProvider : IDownloadProvider
{
    private readonly string _executablePath;
    private readonly AppSettingsService? _appSettings;
    private readonly ILogger<DepotDownloaderProvider> _logger;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeDownloads = new();

    // Matches percentage e.g. " 12.34%" or " 12,34%"
    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*%", RegexOptions.Compiled)]
    private static partial Regex PercentageRegex();

    // Matches speed in various units: " 15.20 MB/s", " 15,20 MiB/s", " 450 KB/s", " 1.2 GB/s", " 100 Mbps", " 500000 B/s"
    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*([KMGT]?i?B|bps|Kbps|Mbps|Gbps)/s?", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SpeedRegex();

    public DepotDownloaderProvider(
        string executablePath,
        ILogger<DepotDownloaderProvider> logger,
        AppSettingsService? appSettings = null)
    {
        _executablePath = executablePath;
        _logger = logger;
        _appSettings = appSettings;
    }

    /// <inheritdoc />
    public async Task DownloadAsync(GameInstance instance, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (instance is null) throw new ArgumentNullException(nameof(instance));
        if (string.IsNullOrWhiteSpace(instance.InstallPath))
            throw new InvalidOperationException("GameInstance InstallPath is not set.");

        if (instance.Depots.Count == 0)
            throw new InvalidOperationException("No depots selected for download.");

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _activeDownloads[instance.Id] = linkedCts;

        // Prepare install directory
        Directory.CreateDirectory(instance.InstallPath);

        // Prepare working directory and persistent instance manifests directory
        var workingDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BlueStar", "DepotWork", instance.Id.ToString());
        Directory.CreateDirectory(workingDir);

        var instanceManifestDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "instances", instance.Id.ToString(), "manifests");
        Directory.CreateDirectory(instanceManifestDir);

        try
        {
            _logger.LogInformation("Starting download for {GameName} (AppId: {AppId}) with {DepotCount} depots",
                instance.Name, instance.AppId, instance.Depots.Count);

            // Step 1: Clean and extract all .manifest files from the source ZIP into persistent storage and working directory
            await EnsureValidManifestsAsync(instance, instanceManifestDir, workingDir, linkedCts.Token).ConfigureAwait(false);

            // Step 2: Write the depot keys file — DepotDownloaderMod uses "depotId;key" format
            var keyFilePath = Path.Combine(workingDir, "depotkeys.txt");
            await WriteDepotKeysFile(instance.Depots, keyFilePath, linkedCts.Token).ConfigureAwait(false);

            long totalBytesAllDepots = instance.Depots.Sum(d => d.SizeBytes);
            long downloadedBytesAcc = 0;

            for (int i = 0; i < instance.Depots.Count; i++)
            {
                linkedCts.Token.ThrowIfCancellationRequested();

                var depot = instance.Depots[i];
                _logger.LogInformation("Downloading depot {DepotId} (Manifest {ManifestId})", depot.DepotId, depot.ManifestId);

                // Find the manifest file with validation
                var manifestFile = FindValidManifestFile(workingDir, instanceManifestDir, depot.DepotId, depot.ManifestId);
                if (manifestFile is null)
                {
                    // Attempt fresh extraction from ZIP as fallback
                    await EnsureValidManifestsAsync(instance, instanceManifestDir, workingDir, linkedCts.Token).ConfigureAwait(false);
                    manifestFile = FindValidManifestFile(workingDir, instanceManifestDir, depot.DepotId, depot.ManifestId);

                    if (manifestFile is null)
                    {
                        throw new FileNotFoundException(
                            $"Manifest file not found or corrupted for depot {depot.DepotId} (Manifest {depot.ManifestId}). " +
                            $"Cannot download depot without its manifest. Please re-import or refresh the instance from its ZIP archive.");
                    }
                }

                var args = BuildArguments(instance.AppId, depot, instance.InstallPath, manifestFile, keyFilePath);

                try
                {
                    await RunDepotDownloaderProcessAsync(
                        args,
                        workingDir,
                        depot,
                        totalBytesAllDepots,
                        downloadedBytesAcc,
                        progress,
                        linkedCts.Token).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("-532462766") || ex.Message.Contains("exited with code -"))
                {
                    _logger.LogWarning("DepotDownloader crashed with unhandled exception ({Message}). Cleaning corrupted cache and retrying depot once...", ex.Message);

                    // Recovery: clean 0-byte or corrupted temporary staging files
                    CleanCorruptedStagingFiles(instance.InstallPath);
                    await EnsureValidManifestsAsync(instance, instanceManifestDir, workingDir, linkedCts.Token).ConfigureAwait(false);

                    // Refresh manifest path
                    manifestFile = FindValidManifestFile(workingDir, instanceManifestDir, depot.DepotId, depot.ManifestId)
                        ?? throw new FileNotFoundException($"Cannot locate manifest for depot {depot.DepotId} after cleanup.");

                    args = BuildArguments(instance.AppId, depot, instance.InstallPath, manifestFile, keyFilePath);

                    // Retry once
                    await RunDepotDownloaderProcessAsync(
                        args,
                        workingDir,
                        depot,
                        totalBytesAllDepots,
                        downloadedBytesAcc,
                        progress,
                        linkedCts.Token).ConfigureAwait(false);
                }

                downloadedBytesAcc += depot.SizeBytes;
            }

            progress?.Report(new DownloadProgress
            {
                TotalBytes = totalBytesAllDepots,
                DownloadedBytes = totalBytesAllDepots,
                Speed = 0,
                Percentage = 100.0,
                CurrentFile = "Download completed"
            });

            _logger.LogInformation("Completed download for {GameName}", instance.Name);

            // Step 3: Clean up / delete downloaded depots, temporary working files, staging directories, and source archives
            if (_appSettings is null || _appSettings.DeleteDepotsAfterInstall)
            {
                CleanUpDownloadedDepots(instance, workingDir);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Download canceled for instance {InstanceId}", instance.Id);
            progress?.Report(new DownloadProgress
            {
                CurrentFile = "Download canceled"
            });
            throw;
        }
        finally
        {
            _activeDownloads.TryRemove(instance.Id, out _);
        }
    }

    /// <summary>
    /// Ensures manifest files are valid and extracts them from source ZIP if missing or corrupted.
    /// </summary>
    private async Task EnsureValidManifestsAsync(GameInstance instance, string instanceManifestDir, string workingDir, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(instance.SourceArchivePath) && File.Exists(instance.SourceArchivePath))
        {
            await ExtractManifestsFromZip(instance.SourceArchivePath, instanceManifestDir, ct).ConfigureAwait(false);
            await ExtractManifestsFromZip(instance.SourceArchivePath, workingDir, ct).ConfigureAwait(false);
        }
        else
        {
            if (Directory.Exists(instanceManifestDir))
            {
                foreach (var mFile in Directory.GetFiles(instanceManifestDir, "*.manifest"))
                {
                    if (new FileInfo(mFile).Length > 32)
                    {
                        var dest = Path.Combine(workingDir, Path.GetFileName(mFile));
                        if (!File.Exists(dest) || new FileInfo(dest).Length <= 32)
                        {
                            File.Copy(mFile, dest, overwrite: true);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Cleans 0-byte or corrupted chunk files in the staging directory.
    /// </summary>
    private void CleanCorruptedStagingFiles(string installPath)
    {
        try
        {
            var stagingDir = Path.Combine(installPath, ".DepotDownloader");
            if (Directory.Exists(stagingDir))
            {
                foreach (var file in Directory.GetFiles(stagingDir, "*.*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (info.Length == 0 || file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(file);
                        }
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while cleaning staging directory");
        }
    }

    /// <summary>
    /// Deletes temporary depot working directory, .DepotDownloader staging folder, and source ZIP archive after installation completes.
    /// </summary>
    private void CleanUpDownloadedDepots(GameInstance instance, string workingDir)
    {
        _logger.LogInformation("Cleaning up downloaded depots and temporary working files for {GameName}", instance.Name);

        try
        {
            if (Directory.Exists(workingDir))
            {
                Directory.Delete(workingDir, recursive: true);
                _logger.LogInformation("Deleted depot working directory: {Dir}", workingDir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete depot working directory {Dir}", workingDir);
        }

        try
        {
            var stagingDir = Path.Combine(instance.InstallPath, ".DepotDownloader");
            if (Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, recursive: true);
                _logger.LogInformation("Deleted .DepotDownloader staging directory: {Dir}", stagingDir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete .DepotDownloader staging directory in {InstallPath}", instance.InstallPath);
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(instance.SourceArchivePath) && File.Exists(instance.SourceArchivePath))
            {
                File.Delete(instance.SourceArchivePath);
                _logger.LogInformation("Deleted source depot archive: {Path}", instance.SourceArchivePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete source archive {Path}", instance.SourceArchivePath);
        }
    }

    /// <inheritdoc />
    public async Task<bool> ValidateAsync(GameInstance instance, CancellationToken ct)
    {
        if (instance is null) throw new ArgumentNullException(nameof(instance));
        if (!Directory.Exists(instance.InstallPath))
            return false;

        _logger.LogInformation("Validating installation files for {GameName} in {Path}", instance.Name, instance.InstallPath);

        var workingDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BlueStar", "DepotWork", instance.Id.ToString());
        Directory.CreateDirectory(workingDir);

        var instanceManifestDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlueStar", "instances", instance.Id.ToString(), "manifests");

        var keyFilePath = Path.Combine(workingDir, "depotkeys.txt");
        await WriteDepotKeysFile(instance.Depots, keyFilePath, ct).ConfigureAwait(false);

        foreach (var depot in instance.Depots)
        {
            ct.ThrowIfCancellationRequested();

            var manifestFile = FindValidManifestFile(workingDir, instanceManifestDir, depot.DepotId, depot.ManifestId);
            if (manifestFile is null) continue;

            var args = BuildArguments(instance.AppId, depot, instance.InstallPath, manifestFile, keyFilePath) + " -validate";
            var exitCode = await RunProcessAsync(args, workingDir, null, ct).ConfigureAwait(false);
            if (exitCode != 0)
            {
                _logger.LogWarning("Depot {DepotId} failed validation with exit code {ExitCode}", depot.DepotId, exitCode);
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public Task CancelAsync(Guid instanceId)
    {
        if (_activeDownloads.TryGetValue(instanceId, out var cts))
        {
            _logger.LogInformation("Canceling active download for instance {InstanceId}", instanceId);
            cts.Cancel();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds DepotDownloaderMod CLI arguments.
    /// Format: -app &lt;AppId&gt; -depot &lt;DepotId&gt; -manifest &lt;ManifestId&gt; -manifestfile &lt;path&gt; -depotkeys &lt;keyfile&gt; -dir &lt;installPath&gt;
    /// </summary>
    private static string BuildArguments(uint mainAppId, DepotInfo depot, string installPath,
        string manifestFilePath, string keyFilePath)
    {
        var args = $"-app {mainAppId} -depot {depot.DepotId} -manifest {depot.ManifestId}";
        args += $" -manifestfile \"{manifestFilePath}\"";
        args += $" -depotkeys \"{keyFilePath}\"";
        args += $" -dir \"{installPath}\"";
        args += " -validate";
        return args;
    }

    /// <summary>Extracts .manifest files from the source ZIP into the working directory.</summary>
    private async Task ExtractManifestsFromZip(string zipPath, string targetDir, CancellationToken ct)
    {
        _logger.LogInformation("Extracting manifests from {Zip} to {Dir}", zipPath, targetDir);
        using var zipStream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!entry.Name.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase)) continue;

            var dest = Path.Combine(targetDir, entry.Name);
            using var entryStream = entry.Open();
            using var fileStream = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            await entryStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
            _logger.LogDebug("Extracted manifest: {Name}", entry.Name);
        }
    }

    /// <summary>Writes depot keys to a file in "depotId;hexKey" format per line.</summary>
    private static async Task WriteDepotKeysFile(IReadOnlyList<DepotInfo> depots, string keyFilePath, CancellationToken ct)
    {
        var lines = depots
            .Where(d => !string.IsNullOrWhiteSpace(d.DepotKey))
            .Select(d => $"{d.DepotId};{d.DepotKey}")
            .Distinct();

        await File.WriteAllLinesAsync(keyFilePath, lines, ct).ConfigureAwait(false);
    }

    /// <summary>Finds a valid non-empty manifest file in the working directory or persistent instance storage.</summary>
    private static string? FindValidManifestFile(string workingDir, string instanceManifestDir, uint depotId, ulong manifestId)
    {
        var exactName = $"{depotId}_{manifestId}.manifest";

        // 1. Check working directory
        if (Directory.Exists(workingDir))
        {
            var exactPath = Path.Combine(workingDir, exactName);
            if (File.Exists(exactPath) && new FileInfo(exactPath).Length > 32) return exactPath;

            var candidates = Directory.GetFiles(workingDir, $"{depotId}_*.manifest");
            foreach (var candidate in candidates)
            {
                if (new FileInfo(candidate).Length > 32) return candidate;
            }
        }

        // 2. Check persistent instance manifests directory
        if (Directory.Exists(instanceManifestDir))
        {
            var persistentExact = Path.Combine(instanceManifestDir, exactName);
            if (File.Exists(persistentExact) && new FileInfo(persistentExact).Length > 32)
            {
                var copyDest = Path.Combine(workingDir, exactName);
                File.Copy(persistentExact, copyDest, overwrite: true);
                return copyDest;
            }

            var candidates = Directory.GetFiles(instanceManifestDir, $"{depotId}_*.manifest");
            foreach (var candidate in candidates)
            {
                if (new FileInfo(candidate).Length > 32)
                {
                    var copyDest = Path.Combine(workingDir, Path.GetFileName(candidate));
                    File.Copy(candidate, copyDest, overwrite: true);
                    return copyDest;
                }
            }
        }

        return null;
    }

    private async Task RunDepotDownloaderProcessAsync(
        string args,
        string workingDir,
        DepotInfo depot,
        long totalBytesAllDepots,
        long previousDepotsBytes,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        var (execPath, argsPrefix) = ResolveExecutable();
        var psi = new ProcessStartInfo
        {
            FileName = execPath,
            Arguments = argsPrefix + args,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        _logger.LogDebug("Running: {Exe} {Args}", execPath, argsPrefix + args);

        using var process = new Process { StartInfo = psi };

        double lastDepotPct = 0;
        double currentSpeedBps = 0;
        long lastSampleTimeTicks = Stopwatch.GetTimestamp();
        long lastSampleBytes = previousDepotsBytes;

        process.OutputDataReceived += (sender, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            _logger.LogDebug("[DepotDownloader] {Output}", e.Data);

            bool hasNewPct = false;
            var matchPct = PercentageRegex().Match(e.Data);
            if (matchPct.Success)
            {
                var valStr = matchPct.Groups[1].Value.Replace(',', '.');
                if (double.TryParse(valStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var pct))
                {
                    lastDepotPct = pct;
                    hasNewPct = true;
                }
            }

            var matchSpeed = SpeedRegex().Match(e.Data);
            if (matchSpeed.Success)
            {
                var valStr = matchSpeed.Groups[1].Value.Replace(',', '.');
                if (double.TryParse(valStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var speedVal))
                {
                    var unit = matchSpeed.Groups[2].Value.ToUpperInvariant();
                    currentSpeedBps = unit switch
                    {
                        "B" => speedVal,
                        "KB" or "KIB" => speedVal * 1024.0,
                        "MB" or "MIB" => speedVal * 1024.0 * 1024.0,
                        "GB" or "GIB" => speedVal * 1024.0 * 1024.0 * 1024.0,
                        "KBPS" => (speedVal * 1000.0) / 8.0,
                        "MBPS" => (speedVal * 1000.0 * 1000.0) / 8.0,
                        "GBPS" => (speedVal * 1000.0 * 1000.0 * 1000.0) / 8.0,
                        _ => speedVal * 1024.0 * 1024.0 // default MB/s
                    };
                }
            }

            if (totalBytesAllDepots > 0 && hasNewPct)
            {
                long currentDepotDownloadedBytes = (long)(depot.SizeBytes * (lastDepotPct / 100.0));
                long totalDownloaded = previousDepotsBytes + currentDepotDownloadedBytes;
                double overallPct = (double)totalDownloaded / totalBytesAllDepots * 100.0;

                // Time-delta speed calculation fallback if speed was not parsed directly from CLI output
                long nowTicks = Stopwatch.GetTimestamp();
                double elapsedSecs = (double)(nowTicks - lastSampleTimeTicks) / Stopwatch.Frequency;
                if (elapsedSecs >= 0.5)
                {
                    long deltaBytes = totalDownloaded - lastSampleBytes;
                    if (deltaBytes > 0 && currentSpeedBps <= 0)
                    {
                        currentSpeedBps = deltaBytes / elapsedSecs;
                    }
                    lastSampleBytes = totalDownloaded;
                    lastSampleTimeTicks = nowTicks;
                }

                progress?.Report(new DownloadProgress
                {
                    TotalBytes = totalBytesAllDepots,
                    DownloadedBytes = totalDownloaded,
                    Speed = currentSpeedBps,
                    Percentage = Math.Min(100.0, overallPct),
                    CurrentFile = $"Depot {depot.DepotId} ({overallPct:F1}%)"
                });
            }
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                _logger.LogDebug("[DepotDownloader ERR] {Output}", e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"DepotDownloader exited with code {process.ExitCode} for depot {depot.DepotId}. Args: {args}");
            }
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
                catch { }
            }
            throw;
        }
    }

    private async Task<int> RunProcessAsync(string args, string workingDir, Action<string>? onOutputLine, CancellationToken ct)
    {
        var (execPath, argsPrefix) = ResolveExecutable();
        var psi = new ProcessStartInfo
        {
            FileName = execPath,
            Arguments = argsPrefix + args,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        if (onOutputLine is not null)
        {
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) onOutputLine(e.Data); };
        }

        process.Start();
        if (onOutputLine is not null) process.BeginOutputReadLine();

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode;
    }

    private (string fileName, string argsPrefix) ResolveExecutable()
    {
        var cwd = Directory.GetCurrentDirectory();
        var appBase = AppContext.BaseDirectory;

        var candidates = new[]
        {
            _executablePath,
            Path.Combine(appBase, "tools", "DepotDownloaderMod.exe"),
            Path.Combine(appBase, "tools", "DepotDownloader.exe"),
            Path.Combine(appBase, "DepotDownloaderMod.exe"),
            Path.Combine(appBase, "DepotDownloader.exe"),
            Path.Combine(cwd, "tools", "DepotDownloaderMod.exe"),
            Path.Combine(cwd, "tools", "DepotDownloader.exe"),
            Path.Combine(cwd, "src", "BlueStar.App", "tools", "DepotDownloaderMod.exe"),
            Path.Combine(cwd, "src", "BlueStar.App", "tools", "DepotDownloader.exe"),
        };

        foreach (var path in candidates)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                _logger.LogDebug("Using DepotDownloader executable: {Path}", path);
                return (path, string.Empty);
            }
        }

        var dllCandidates = new[]
        {
            Path.Combine(appBase, "tools", "DepotDownloaderMod.dll"),
            Path.Combine(appBase, "tools", "DepotDownloader.dll"),
            Path.Combine(cwd, "tools", "DepotDownloaderMod.dll"),
            Path.Combine(cwd, "src", "BlueStar.App", "tools", "DepotDownloaderMod.dll"),
        };

        foreach (var dll in dllCandidates)
        {
            if (File.Exists(dll))
            {
                _logger.LogDebug("Using DepotDownloaderMod DLL: {Path}", dll);
                return ("dotnet", $"\"{dll}\" ");
            }
        }

        throw new FileNotFoundException(
            $"DepotDownloaderMod executable not found. Please place 'DepotDownloaderMod.exe' in the 'tools' folder next to the app. Searched in: {appBase}, {cwd}");
    }
}
