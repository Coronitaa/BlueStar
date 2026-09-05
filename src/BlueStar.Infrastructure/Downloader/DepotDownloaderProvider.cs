using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Storage;
using DepotDownloader;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Downloader;

/// <summary>
/// Implements <see cref="IDownloadProvider"/> using the in-process DepotDownloadEngine.
/// Replaces the previous Process-based wrapper with direct library calls,
/// providing real-time progress, proper error propagation, and resume support.
/// </summary>
public partial class DepotDownloaderProvider : IDownloadProvider
{
    private readonly AppSettingsService? _appSettings;
    private readonly IManifestCacheService? _manifestCache;
    private readonly IDepotKeyRepository? _keyRepository;
    private readonly ILogger<DepotDownloaderProvider> _logger;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeDownloads = new();

    public DepotDownloaderProvider(
        ILogger<DepotDownloaderProvider> logger,
        AppSettingsService? appSettings = null,
        IManifestCacheService? manifestCache = null,
        IDepotKeyRepository? keyRepository = null)
    {
        _logger = logger;
        _appSettings = appSettings;
        _manifestCache = manifestCache;
        _keyRepository = keyRepository;
    }

    // Overload preserved for backward compatibility with DI registrations that pass executablePath
    public DepotDownloaderProvider(
        string executablePath,
        ILogger<DepotDownloaderProvider> logger,
        AppSettingsService? appSettings = null,
        IManifestCacheService? manifestCache = null,
        IDepotKeyRepository? keyRepository = null) : this(logger, appSettings, manifestCache, keyRepository)
    {
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

        // State file for resume support
        var stateFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BlueStar", "DepotWork", instance.Id.ToString(), "download_state.json");

        try
        {
            _logger.LogInformation("Starting download for {GameName} (AppId: {AppId}) with {DepotCount} depots",
                instance.Name, instance.AppId, instance.Depots.Count);

            // Step 1: Ensure manifests are extracted and valid
            await EnsureValidManifestsAsync(instance, instanceManifestDir, workingDir, linkedCts.Token).ConfigureAwait(false);

            // Step 2: Write the depot keys file (resolving missing keys from repository if needed)
            var depotsWithKeys = new List<DepotInfo>();
            foreach (var depot in instance.Depots)
            {
                var key = depot.DepotKey;
                if (string.IsNullOrWhiteSpace(key) && _keyRepository != null)
                {
                    key = await _keyRepository.GetKeyAsync(depot.DepotId, linkedCts.Token).ConfigureAwait(false);
                }
                depotsWithKeys.Add(depot with { DepotKey = key });
            }

            var keyFilePath = Path.Combine(workingDir, "depotkeys.txt");
            await WriteDepotKeysFile(depotsWithKeys, keyFilePath, linkedCts.Token).ConfigureAwait(false);

            // Step 3: Build the request with manifest file paths
            var depotItems = depotsWithKeys.Select(depot =>
            {
                var manifestFile = FindValidManifestFile(workingDir, instanceManifestDir, depot.DepotId, depot.ManifestId, _manifestCache);
                return new DepotDownloadItem
                {
                    DepotId = depot.DepotId,
                    ManifestId = depot.ManifestId,
                    SizeBytes = depot.SizeBytes,
                    Name = depot.Name,
                    DepotKey = depot.DepotKey,
                    ManifestFilePath = manifestFile,
                };
            }).ToList();


            var request = new DepotDownloadRequest
            {
                InstanceId = instance.Id,
                AppId = instance.AppId,
                InstallPath = instance.InstallPath,
                Depots = depotItems,
                MaxConnections = 8,
                DepotKeysFilePath = keyFilePath,
                WorkingDirectory = workingDir,
                StateFilePath = stateFilePath,
            };

            // Step 4: Create engine and download via direct library call
            using var engine = new DepotDownloadEngine(
                _logger as ILogger<DepotDownloadEngine>
                ?? LoggerFactory.Create(b => { }).CreateLogger<DepotDownloadEngine>());

            // Map DownloadProgressInfo → DownloadProgress for BlueStar UI
            var mappedProgress = progress != null
                ? new Progress<DownloadProgressInfo>(info =>
                {
                    progress.Report(new DownloadProgress
                    {
                        TotalBytes = info.TotalBytes,
                        DownloadedBytes = info.DownloadedBytes,
                        Percentage = info.Percentage,
                        Speed = info.DownloadBytesPerSec,
                        WriteBytesPerSec = info.WriteBytesPerSec,
                        EstimatedTimeRemaining = info.EstimatedTimeRemaining,
                        CurrentFile = info.CurrentFile,
                        ActiveConnections = info.ActiveConnections,
                        TotalChunks = info.TotalChunks,
                        CompletedChunks = info.CompletedChunks,
                        Phase = info.Phase.ToString(),
                        CurrentDepotId = info.DepotId,
                        CurrentDepotIndex = info.CurrentDepotIndex,
                        TotalDepots = info.TotalDepots,
                    });
                })
                : null;

            await engine.DownloadAsync(request, mappedProgress, linkedCts.Token).ConfigureAwait(false);

            progress?.Report(new DownloadProgress
            {
                TotalBytes = instance.Depots.Sum(d => d.SizeBytes),
                DownloadedBytes = instance.Depots.Sum(d => d.SizeBytes),
                Speed = 0,
                Percentage = 100.0,
                CurrentFile = "Download completed",
                Phase = "Completed",
            });

            _logger.LogInformation("Completed download for {GameName}", instance.Name);

            // Step 5: Clean up
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
                CurrentFile = "Download canceled",
                Phase = "Paused",
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

        var depotItems = instance.Depots.Select(depot =>
        {
            var manifestFile = FindValidManifestFile(workingDir, instanceManifestDir, depot.DepotId, depot.ManifestId);
            return new DepotDownloadItem
            {
                DepotId = depot.DepotId,
                ManifestId = depot.ManifestId,
                SizeBytes = depot.SizeBytes,
                DepotKey = depot.DepotKey,
                ManifestFilePath = manifestFile,
            };
        }).ToList();

        var request = new DepotDownloadRequest
        {
            InstanceId = instance.Id,
            AppId = instance.AppId,
            InstallPath = instance.InstallPath,
            Depots = depotItems,
            DepotKeysFilePath = keyFilePath,
            WorkingDirectory = workingDir,
            ValidateExisting = true,
        };

        using var engine = new DepotDownloadEngine(
            _logger as ILogger<DepotDownloadEngine>
            ?? LoggerFactory.Create(b => { }).CreateLogger<DepotDownloadEngine>());

        return await engine.ValidateAsync(request, null, ct).ConfigureAwait(false);
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

    /// <summary>Extracts .manifest files from the source ZIP into the target directory.</summary>
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

    /// <summary>Finds a valid non-empty manifest file in the working directory, persistent instance storage, or global manifest cache.</summary>
    private static string? FindValidManifestFile(string workingDir, string instanceManifestDir, uint depotId, ulong manifestId, IManifestCacheService? manifestCache = null)
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

        // 3. Check persistent global manifest cache
        if (manifestCache != null && manifestCache.HasManifest(depotId, manifestId))
        {
            var cached = manifestCache.GetManifestPath(depotId, manifestId);
            if (!string.IsNullOrWhiteSpace(cached) && File.Exists(cached))
            {
                var copyDest = Path.Combine(workingDir, exactName);
                File.Copy(cached, copyDest, overwrite: true);
                return copyDest;
            }
        }

        return null;
    }
}

