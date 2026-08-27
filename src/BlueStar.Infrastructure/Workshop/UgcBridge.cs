using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Storage;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Workshop;

/// <summary>
/// Subsystem that emulates native Steam ISteamUGC behavior, generating VDF manifests,
/// linking content folders, and bridging installed workshop items to ReFix and Goldberg emulators.
/// </summary>
public sealed class UgcBridge : IUgcBridge
{
    private readonly IWin32Linker _linker;
    private readonly ILogger<UgcBridge> _logger;

    public UgcBridge(IWin32Linker linker, ILogger<UgcBridge> logger)
    {
        _linker = linker ?? throw new ArgumentNullException(nameof(linker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string GetInstanceWorkshopContentPath(string instancePath, uint appId, ulong publishedFileId)
    {
        return Path.Combine(instancePath, "steamapps", "workshop", "content", appId.ToString(), publishedFileId.ToString());
    }

    /// <inheritdoc />
    public string GetInstanceWorkshopVdfPath(string instancePath, uint appId)
    {
        return Path.Combine(instancePath, "steamapps", "workshop", $"appworkshop_{appId}.vdf");
    }

    /// <inheritdoc />
    public Task<bool> DeployWorkshopItemToInstanceAsync(
        uint appId,
        ulong publishedFileId,
        string itemCachePath,
        string instancePath,
        CancellationToken ct = default)
    {
        if (appId == 0 || publishedFileId == 0) return Task.FromResult(false);
        if (string.IsNullOrWhiteSpace(itemCachePath) || !Directory.Exists(itemCachePath))
        {
            _logger.LogWarning("Cannot deploy workshop item {Id}: cache path not found: {Path}", publishedFileId, itemCachePath);
            return Task.FromResult(false);
        }

        try
        {
            var targetContentDir = GetInstanceWorkshopContentPath(instancePath, appId, publishedFileId);
            Directory.CreateDirectory(Path.GetDirectoryName(targetContentDir)!);

            // 1. Link or copy cache to steamapps/workshop/content/<AppId>/<PubFileId>/
            if (!Directory.Exists(targetContentDir))
            {
                bool linked = _linker.CreateJunction(targetContentDir, itemCachePath);
                if (!linked)
                {
                    // Fallback to hardlinking/copying files
                    Directory.CreateDirectory(targetContentDir);
                    foreach (var file in Directory.GetFiles(itemCachePath, "*.*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(itemCachePath, file);
                        var dest = Path.Combine(targetContentDir, rel);
                        var destDir = Path.GetDirectoryName(dest);
                        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);

                        if (!_linker.CreateHardLink(dest, file))
                        {
                            File.Copy(file, dest, overwrite: true);
                        }
                    }
                }
            }

            // 2. Emulator mapping (ReFix / Goldberg) - only if steam_settings already exists
            var steamSettingsDirs = Directory.GetDirectories(instancePath, "steam_settings", SearchOption.AllDirectories);

            foreach (var sDir in steamSettingsDirs)
            {
                var emulatorModsDir = Path.Combine(sDir, "mods");
                Directory.CreateDirectory(emulatorModsDir);

                var emulatorItemDir = Path.Combine(emulatorModsDir, publishedFileId.ToString());
                if (!Directory.Exists(emulatorItemDir))
                {
                    // Create junction or link pointing to the workshop content
                    if (!_linker.CreateJunction(emulatorItemDir, targetContentDir))
                    {
                        _linker.CreateSymbolicLink(emulatorItemDir, targetContentDir, isDirectory: true);
                    }
                }
            }

            _logger.LogInformation("Deployed Workshop item {PubId} to instance {Path} (Steamworks + Emulator mapping)", publishedFileId, instancePath);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deploy Workshop item {PubId} to instance {Path}", publishedFileId, instancePath);
            return Task.FromResult(false);
        }
    }

    /// <inheritdoc />
    public Task<bool> SynchronizeInstanceWorkshopAsync(
        uint appId,
        string instancePath,
        IEnumerable<WorkshopItemInfo> installedItems,
        CancellationToken ct = default)
    {
        if (appId == 0 || string.IsNullOrWhiteSpace(instancePath))
            return Task.FromResult(false);

        try
        {
            var itemsList = installedItems?.ToList() ?? [];
            var vdfPath = VdfBuilder.WriteAppWorkshopFile(instancePath, appId, itemsList);

            _logger.LogInformation("Synchronized Steam Workshop manifest for App {AppId} at {Path} ({Count} items)",
                appId, vdfPath, itemsList.Count);

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to synchronize workshop manifest for App {AppId} in {Path}", appId, instancePath);
            return Task.FromResult(false);
        }
    }
}
