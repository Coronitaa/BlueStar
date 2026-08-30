using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Service responsible for deploying, stacking, and uninstalling game-specific fixes and emulators
/// from DepotBox API while maintaining full coexistence with ReFix, Goldberg, and DLC unlockers.
/// </summary>
public sealed class GameFixDeployService : IGameFixDeployService
{
    private readonly IDepotBoxApiClient _apiClient;
    private readonly IInstanceManager _instanceManager;
    private readonly IDlcInstaller _dlcInstaller;
    private readonly IEngineDetector? _engineDetector;
    private readonly ILogger<GameFixDeployService> _logger;

    private static readonly EnumerationOptions SafeEnumOptions = new()
    {
        MaxRecursionDepth = 6,
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false
    };

    public GameFixDeployService(
        IDepotBoxApiClient apiClient,
        IInstanceManager instanceManager,
        IDlcInstaller dlcInstaller,
        ILogger<GameFixDeployService> logger,
        IEngineDetector? engineDetector = null)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _instanceManager = instanceManager ?? throw new ArgumentNullException(nameof(instanceManager));
        _dlcInstaller = dlcInstaller ?? throw new ArgumentNullException(nameof(dlcInstaller));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _engineDetector = engineDetector;
    }

    /// <inheritdoc />
    public async Task<bool> DeployFixAsync(
        GameInstance instance,
        GameFixInfo fix,
        IProgress<DeployProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(fix);

        if (string.IsNullOrWhiteSpace(instance.InstallPath) || !Directory.Exists(instance.InstallPath))
        {
            _logger.LogError("Cannot deploy game fix: Game is not installed at {Path}", instance.InstallPath);
            progress?.Report(new DeployProgress
            {
                Percentage = 0,
                Message = "Game is not installed in the specified path. Please download or install it first.",
                CurrentStep = "Validation_Error"
            });
            return false;
        }

        _logger.LogInformation("Starting deployment of fix '{FixName}' (ID={FixId}, Tags={Tags}) for game '{Game}'",
            fix.Name, fix.Id, fix.TagsSummary, instance.Name);

        string? stagingDir = null;
        try
        {
            // ─────────────────────────────────────────────────────────────
            // STAGE 1: DLC Preservation Snapshot
            // ─────────────────────────────────────────────────────────────
            progress?.Report(new DeployProgress
            {
                Percentage = 5,
                Message = "Preserving DLC configuration...",
                CurrentStep = "DLC_Snapshot"
            });

            var (wasDlcUnlocked, preservedDlcIds, sampleDlc) = await SnapshotDlcStateAsync(instance, ct).ConfigureAwait(false);
            if (wasDlcUnlocked)
            {
                try
                {
                    await _dlcInstaller.UninstallDlcAsync(instance, sampleDlc, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to temporarily remove DLC unlocker before fix deployment for {Game}", instance.Name);
                }
            }

            // ─────────────────────────────────────────────────────────────
            // STAGE 2: Download Game Fix Archive
            // ─────────────────────────────────────────────────────────────
            progress?.Report(new DeployProgress
            {
                Percentage = 15,
                Message = $"Downloading fix archive: {fix.DownloadName}...",
                CurrentStep = "Downloading"
            });

            var cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlueStar", "cache", "game_fixes");
            Directory.CreateDirectory(cacheDir);

            var downloadProgress = new Progress<DownloadProgress>(p =>
            {
                var scaledPct = 15.0 + (p.Percentage * 0.35); // 15% -> 50%
                progress?.Report(new DeployProgress
                {
                    Percentage = scaledPct,
                    Message = $"Downloading {fix.DownloadName} ({p.DownloadedBytes / 1024.0 / 1024.0:F1} MB)...",
                    CurrentStep = "Downloading"
                });
            });

            var identifier = !string.IsNullOrWhiteSpace(fix.DownloadName) ? fix.DownloadName : fix.Id;
            var zipPath = await _apiClient.DownloadGameFixAsync(identifier, cacheDir, downloadProgress, ct).ConfigureAwait(false);

            if (!File.Exists(zipPath))
            {
                throw new FileNotFoundException($"Downloaded fix archive was not found at {zipPath}");
            }

            // ─────────────────────────────────────────────────────────────
            // STAGE 3: Extract Archive to Staging Folder
            // ─────────────────────────────────────────────────────────────
            progress?.Report(new DeployProgress
            {
                Percentage = 55,
                Message = "Extracting fix files...",
                CurrentStep = "Extracting"
            });

            stagingDir = Path.Combine(Path.GetTempPath(), "BlueStar_FixDeploy", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDir);

            ZipFile.ExtractToDirectory(zipPath, stagingDir, overwriteFiles: true);

            // ─────────────────────────────────────────────────────────────
            // STAGE 4: Analyze Target Directories & Prepare Backups
            // ─────────────────────────────────────────────────────────────
            progress?.Report(new DeployProgress
            {
                Percentage = 65,
                Message = "Creating backup of existing files...",
                CurrentStep = "Backup"
            });

            var targetBaseDir = instance.InstallPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var exeDir = !string.IsNullOrWhiteSpace(instance.ExecutablePath) && File.Exists(instance.ExecutablePath)
                ? Path.GetDirectoryName(instance.ExecutablePath)!
                : targetBaseDir;

            // Determine effective staging directory (unwrap single top-level directory if present)
            var rootSubDirs = Directory.GetDirectories(stagingDir);
            var rootFiles = Directory.GetFiles(stagingDir);
            string effectiveStagingDir = (rootSubDirs.Length == 1 && rootFiles.Length == 0)
                ? rootSubDirs[0]
                : stagingDir;

            var stagedEntries = Directory.GetFileSystemEntries(effectiveStagingDir, "*", SafeEnumOptions);
            if (stagedEntries.Length == 0)
            {
                throw new InvalidOperationException("The downloaded fix archive is empty.");
            }

            var stagingFiles = Directory.GetFiles(effectiveStagingDir, "*", SafeEnumOptions);
            bool isFlatArchive = !Directory.GetDirectories(effectiveStagingDir).Any();

            // Destination directory decision:
            // If the archive is flat (no subdirectories) and the game's executable is inside a subfolder (e.g. Binaries/Win64)
            // and contains steam_api64.dll or similar hooks, use exeDir. Otherwise use targetBaseDir.
            string deploymentTargetDir = targetBaseDir;
            if (isFlatArchive && exeDir != targetBaseDir && File.Exists(Path.Combine(exeDir, "steam_api64.dll")))
            {
                deploymentTargetDir = exeDir;
            }

            var layerId = $"fix_{SanitizeIdentifier(fix.Id)}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            var backupDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BlueStar", "fix_backups", instance.Id.ToString(), layerId);
            Directory.CreateDirectory(backupDir);

            var deployedRelPaths = new List<string>();

            // ─────────────────────────────────────────────────────────────
            // STAGE 5: Copy Files & Backup Overwritten Items
            // ─────────────────────────────────────────────────────────────
            progress?.Report(new DeployProgress
            {
                Percentage = 75,
                Message = $"Deploying {fix.Name} files to game directory...",
                CurrentStep = "Deploying"
            });

            foreach (var srcFile in stagingFiles)
            {
                var relPath = Path.GetRelativePath(effectiveStagingDir, srcFile);
                var destFile = Path.Combine(deploymentTargetDir, relPath);
                var destParent = Path.GetDirectoryName(destFile);

                if (!string.IsNullOrEmpty(destParent))
                {
                    Directory.CreateDirectory(destParent);
                }

                // If file already exists, create backup
                if (File.Exists(destFile))
                {
                    var backupFile = Path.Combine(backupDir, relPath);
                    var backupParent = Path.GetDirectoryName(backupFile);
                    if (!string.IsNullOrEmpty(backupParent))
                    {
                        Directory.CreateDirectory(backupParent);
                    }

                    File.Copy(destFile, backupFile, overwrite: true);
                }

                // Copy staged file to destination
                File.Copy(srcFile, destFile, overwrite: true);
                deployedRelPaths.Add(relPath);
            }

            // ─────────────────────────────────────────────────────────────
            // STAGE 6: Register Fix Layer & Restore DLC Unlockers
            // ─────────────────────────────────────────────────────────────
            progress?.Report(new DeployProgress
            {
                Percentage = 90,
                Message = "Registering layer and restoring entitlements...",
                CurrentStep = "Finalizing"
            });

            var newLayer = new FixLayerInfo
            {
                LayerId = layerId,
                FixId = fix.Id,
                SourceType = "depotbox_gamefix",
                DisplayName = fix.Name,
                Tags = fix.Tags,
                DeployedFiles = deployedRelPaths.AsReadOnly(),
                BackupDirectory = backupDir,
                Version = "1.0",
                InstalledAt = DateTimeOffset.UtcNow
            };

            // Remove any older layer for the same fix if present
            var existingLayers = (instance.InstalledFixLayers ?? [])
                .Where(l => l.FixId != fix.Id)
                .ToList();
            existingLayers.Add(newLayer);

            var updatedInstance = instance with
            {
                InstalledFixLayers = existingLayers.AsReadOnly(),
                EmulatorEnabled = instance.EmulatorEnabled || fix.IsOnline || fix.Tags.Contains("online"),
                DlcUnlockerInstalled = wasDlcUnlocked,
                UnlockedDlcIds = preservedDlcIds
            };

            // Restore DLC unlocker if it was active
            if (wasDlcUnlocked)
            {
                try
                {
                    await _dlcInstaller.InstallDlcAsync(updatedInstance, sampleDlc, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to restore DLC unlocker after fix deployment for {Game}", instance.Name);
                }
            }

            // Persist instance updates
            await _instanceManager.UpdateAsync(updatedInstance, ct).ConfigureAwait(false);

            progress?.Report(new DeployProgress
            {
                Percentage = 100,
                Message = $"Successfully deployed fix '{fix.Name}'!",
                CurrentStep = "Complete"
            });

            _logger.LogInformation("Successfully deployed fix '{FixName}' with {Count} files for {Game}",
                fix.Name, deployedRelPaths.Count, instance.Name);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deploy game fix '{Fix}' for {Game}", fix.Name, instance.Name);
            progress?.Report(new DeployProgress
            {
                Percentage = 0,
                Message = $"Error deploying fix: {ex.Message}",
                CurrentStep = "Error"
            });
            return false;
        }
        finally
        {
            if (stagingDir != null && Directory.Exists(stagingDir))
            {
                try
                {
                    Directory.Delete(stagingDir, recursive: true);
                }
                catch { }
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> UninstallFixLayerAsync(
        GameInstance instance,
        FixLayerInfo layer,
        IProgress<DeployProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(layer);

        if (string.IsNullOrWhiteSpace(instance.InstallPath) || !Directory.Exists(instance.InstallPath))
        {
            return true;
        }

        _logger.LogInformation("Uninstalling fix layer '{LayerId}' ({DisplayName}) from {Game}",
            layer.LayerId, layer.DisplayName, instance.Name);

        try
        {
            progress?.Report(new DeployProgress
            {
                Percentage = 10,
                Message = $"Preserving DLC state and removing {layer.DisplayName}...",
                CurrentStep = "DLC_Snapshot"
            });

            var (wasDlcUnlocked, preservedDlcIds, sampleDlc) = await SnapshotDlcStateAsync(instance, ct).ConfigureAwait(false);
            if (wasDlcUnlocked)
            {
                try
                {
                    await _dlcInstaller.UninstallDlcAsync(instance, sampleDlc, ct).ConfigureAwait(false);
                }
                catch { }
            }

            progress?.Report(new DeployProgress
            {
                Percentage = 40,
                Message = $"Restoring original files for {layer.DisplayName}...",
                CurrentStep = "Restoring_Files"
            });

            var targetBaseDir = instance.InstallPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var exeDir = !string.IsNullOrWhiteSpace(instance.ExecutablePath) && File.Exists(instance.ExecutablePath)
                ? Path.GetDirectoryName(instance.ExecutablePath)!
                : targetBaseDir;

            foreach (var relPath in layer.DeployedFiles)
            {
                // Check both targetBaseDir and exeDir
                var targetFile = Path.Combine(targetBaseDir, relPath);
                if (!File.Exists(targetFile) && exeDir != targetBaseDir)
                {
                    targetFile = Path.Combine(exeDir, relPath);
                }

                var backupFile = !string.IsNullOrEmpty(layer.BackupDirectory)
                    ? Path.Combine(layer.BackupDirectory, relPath)
                    : null;

                try
                {
                    if (backupFile != null && File.Exists(backupFile))
                    {
                        // Restore backup file over the deployed file
                        var targetParent = Path.GetDirectoryName(targetFile);
                        if (!string.IsNullOrEmpty(targetParent))
                        {
                            Directory.CreateDirectory(targetParent);
                        }

                        File.Copy(backupFile, targetFile, overwrite: true);
                    }
                    else if (File.Exists(targetFile))
                    {
                        // No backup existed (it was a purely new file created by the fix) -> delete it
                        File.Delete(targetFile);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to restore/delete file {File} during layer uninstall", relPath);
                }
            }

            // Cleanup backup directory
            if (!string.IsNullOrEmpty(layer.BackupDirectory) && Directory.Exists(layer.BackupDirectory))
            {
                try
                {
                    Directory.Delete(layer.BackupDirectory, recursive: true);
                }
                catch { }
            }

            // Remove layer from instance
            var updatedLayers = (instance.InstalledFixLayers ?? [])
                .Where(l => l.LayerId != layer.LayerId)
                .ToList();

            var updatedInstance = instance with
            {
                InstalledFixLayers = updatedLayers.AsReadOnly(),
                DlcUnlockerInstalled = wasDlcUnlocked,
                UnlockedDlcIds = preservedDlcIds
            };

            // Restore DLC unlocker if it was active
            if (wasDlcUnlocked)
            {
                progress?.Report(new DeployProgress
                {
                    Percentage = 85,
                    Message = "Restoring DLC unlocker...",
                    CurrentStep = "DLC_Restore"
                });

                try
                {
                    await _dlcInstaller.InstallDlcAsync(updatedInstance, sampleDlc, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to restore DLC unlocker after layer uninstall for {Game}", instance.Name);
                }
            }

            await _instanceManager.UpdateAsync(updatedInstance, ct).ConfigureAwait(false);

            progress?.Report(new DeployProgress
            {
                Percentage = 100,
                Message = $"Successfully removed {layer.DisplayName}.",
                CurrentStep = "Complete"
            });

            _logger.LogInformation("Successfully uninstalled fix layer '{LayerId}' from {Game}", layer.LayerId, instance.Name);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uninstalling fix layer '{LayerId}' for {Game}", layer.LayerId, instance.Name);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> UninstallAllFixLayersAsync(
        GameInstance instance,
        IProgress<DeployProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var layers = (instance.InstalledFixLayers ?? []).ToList();
        if (layers.Count == 0) return true;

        // Uninstall in reverse order (LIFO)
        layers.Reverse();
        var currentInstance = instance;

        for (int i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            var layerProgress = new Progress<DeployProgress>(p =>
            {
                var basePct = (double)i / layers.Count * 100.0;
                var stepPct = basePct + (p.Percentage / layers.Count);
                progress?.Report(new DeployProgress
                {
                    Percentage = stepPct,
                    Message = $"Removing {layer.DisplayName} ({i + 1}/{layers.Count})...",
                    CurrentStep = p.CurrentStep
                });
            });

            await UninstallFixLayerAsync(currentInstance, layer, layerProgress, ct).ConfigureAwait(false);

            // Re-fetch latest instance state from manager
            var refreshed = await _instanceManager.GetByIdAsync(instance.Id, ct).ConfigureAwait(false);
            if (refreshed != null)
            {
                currentInstance = refreshed;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public bool IsFixCompatible(GameInstance instance, GameFixInfo fix)
    {
        if (instance == null || fix == null) return false;

        // Compatible if game is installed or has a valid AppId/Name
        return !string.IsNullOrWhiteSpace(instance.InstallPath);
    }

    /// <inheritdoc />
    public IReadOnlyList<FixLayerInfo> GetInstalledFixLayers(GameInstance instance)
    {
        return instance?.InstalledFixLayers ?? [];
    }

    private async Task<(bool WasDlcUnlocked, IReadOnlyList<uint> PreservedDlcIds, DlcInfo SampleDlc)> SnapshotDlcStateAsync(
        GameInstance instance,
        CancellationToken ct)
    {
        var sampleDlc = instance.Dlcs.Count > 0
            ? instance.Dlcs[0]
            : new DlcInfo { AppId = instance.AppId, Name = "Base Game", Depots = [] };

        bool isDlcInstalled = instance.DlcUnlockerInstalled;
        if (!isDlcInstalled)
        {
            try
            {
                isDlcInstalled = await _dlcInstaller.IsDlcInstalledAsync(instance, sampleDlc, ct).ConfigureAwait(false);
            }
            catch { }
        }

        var preservedIds = instance.UnlockedDlcIds != null && instance.UnlockedDlcIds.Count > 0
            ? instance.UnlockedDlcIds
            : instance.Dlcs.Select(d => d.AppId).ToList().AsReadOnly();

        return (isDlcInstalled, preservedIds, sampleDlc);
    }

    private static string SanitizeIdentifier(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "fix";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = id.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Replace(' ', '_');
    }
}
