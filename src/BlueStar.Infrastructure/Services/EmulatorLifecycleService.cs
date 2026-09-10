using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Interfaces;
using BlueStar.Core.Models;
using BlueStar.Infrastructure.Emulators;
using Microsoft.Extensions.Logging;

namespace BlueStar.Infrastructure.Services;

/// <summary>
/// Orchestrates emulator deployments, updates, uninstalls, and mode switches while safely preserving and restoring DLC unlockers.
/// </summary>
public sealed class EmulatorLifecycleService : IEmulatorLifecycleService
{
    private readonly IDlcInstaller _dlcInstaller;
    private readonly IInstanceManager? _instanceManager;
    private readonly ILogger<EmulatorLifecycleService> _logger;
    private readonly Func<GameInstance, string, IProgress<DeployProgress>?, CancellationToken, Task<bool>>? _deployHandler;
    private readonly Func<GameInstance, CancellationToken, Task<bool>>? _uninstallHandler;

    public EmulatorLifecycleService(
        IDlcInstaller dlcInstaller,
        ILogger<EmulatorLifecycleService> logger,
        IInstanceManager? instanceManager = null,
        Func<GameInstance, string, IProgress<DeployProgress>?, CancellationToken, Task<bool>>? deployHandler = null,
        Func<GameInstance, CancellationToken, Task<bool>>? uninstallHandler = null)
    {
        _dlcInstaller = dlcInstaller ?? throw new ArgumentNullException(nameof(dlcInstaller));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _instanceManager = instanceManager;
        _deployHandler = deployHandler;
        _uninstallHandler = uninstallHandler;
    }

    /// <inheritdoc />
    public async Task<bool> DeployOrUpdateEmulatorWithDlcPreservationAsync(
        GameInstance instance,
        string emulatorOptionId,
        IProgress<DeployProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        _logger.LogInformation("[EmulatorLifecycle] Deploying emulator {Option} with DLC preservation for {Game}", emulatorOptionId, instance.Name);

        // 1. Determine if DLC unlocker is currently active
        var (wasDlcUnlocked, preservedDlcIds, sampleDlc) = await SnapshotDlcStateAsync(instance, ct).ConfigureAwait(false);

        // 2. Cleanly remove DLC unlocker before emulator deployment
        if (wasDlcUnlocked)
        {
            progress?.Report(new DeployProgress { Percentage = 5, Message = "Preserving DLC configuration and uninstalling unlocker...", CurrentStep = "DLC_Backup" });
            try
            {
                await _dlcInstaller.UninstallDlcAsync(instance, sampleDlc, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to cleanly remove DLC unlocker before emulator update for {Game}", instance.Name);
            }
        }

        // 3. Deploy emulator option (ReFix / Re:Goldberg)
        bool deployResult;
        if (_deployHandler != null)
        {
            deployResult = await _deployHandler(instance, emulatorOptionId, progress, ct).ConfigureAwait(false);
        }
        else
        {
            var emulator = new ReFixEmulator(LoggerFactory.Create(b => { }).CreateLogger<ReFixEmulator>());
            deployResult = await emulator.DeployOptionAsync(instance, emulatorOptionId, progress, ct).ConfigureAwait(false);
        }

        if (!deployResult)
        {
            _logger.LogError("Emulator deployment failed for {Game}", instance.Name);
            return false;
        }

        var installedVersion = ReFixEmulator.GetCurrentVersion();

        // 4. Update instance emulator state
        var updatedInstance = instance with
        {
            EmulatorEnabled = true,
            EmulatorId = emulatorOptionId,
            InstalledEmulatorVersion = installedVersion,
            DlcUnlockerInstalled = wasDlcUnlocked,
            UnlockedDlcIds = preservedDlcIds
        };

        // 5. Restore DLC unlocker if it was previously active
        if (wasDlcUnlocked)
        {
            progress?.Report(new DeployProgress { Percentage = 95, Message = "Restoring DLC unlocker and active entitlements...", CurrentStep = "DLC_Restore" });
            try
            {
                var dlcRestored = await _dlcInstaller.InstallDlcAsync(updatedInstance, sampleDlc, ct).ConfigureAwait(false);
                if (dlcRestored)
                {
                    _logger.LogInformation("Successfully restored DLC unlocker with {Count} DLCs for {Game}", preservedDlcIds.Count, instance.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore DLC unlocker after emulator deployment for {Game}", instance.Name);
            }
        }

        if (_instanceManager != null)
        {
            try
            {
                await _instanceManager.UpdateAsync(updatedInstance, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist instance update after emulator deployment for {Game}", instance.Name);
            }
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> UninstallEmulatorWithDlcPreservationAsync(
        GameInstance instance,
        IProgress<DeployProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        _logger.LogInformation("[EmulatorLifecycle] Uninstalling emulator with DLC preservation for {Game}", instance.Name);

        // 1. Determine if DLC unlocker is currently active
        var (wasDlcUnlocked, preservedDlcIds, sampleDlc) = await SnapshotDlcStateAsync(instance, ct).ConfigureAwait(false);

        // 2. Cleanly remove DLC unlocker
        if (wasDlcUnlocked)
        {
            progress?.Report(new DeployProgress { Percentage = 10, Message = "Preserving DLC configuration...", CurrentStep = "DLC_Backup" });
            try
            {
                await _dlcInstaller.UninstallDlcAsync(instance, sampleDlc, ct).ConfigureAwait(false);
            }
            catch { }
        }

        // 3. Uninstall emulator suite
        bool uninstallResult;
        if (_uninstallHandler != null)
        {
            uninstallResult = await _uninstallHandler(instance, ct).ConfigureAwait(false);
        }
        else
        {
            var emulator = new ReFixEmulator(LoggerFactory.Create(b => { }).CreateLogger<ReFixEmulator>());
            uninstallResult = await emulator.UninstallAsync(instance, ct).ConfigureAwait(false);
        }

        var updatedInstance = instance with
        {
            EmulatorEnabled = false,
            EmulatorId = null,
            InstalledEmulatorVersion = null,
            DlcUnlockerInstalled = wasDlcUnlocked,
            UnlockedDlcIds = preservedDlcIds
        };

        // 4. Restore DLC unlocker if it was active
        if (wasDlcUnlocked)
        {
            progress?.Report(new DeployProgress { Percentage = 90, Message = "Restoring DLC unlocker...", CurrentStep = "DLC_Restore" });
            try
            {
                await _dlcInstaller.InstallDlcAsync(updatedInstance, sampleDlc, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore DLC unlocker after emulator uninstall for {Game}", instance.Name);
            }
        }

        if (_instanceManager != null)
        {
            try
            {
                await _instanceManager.UpdateAsync(updatedInstance, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist instance update after emulator uninstall for {Game}", instance.Name);
            }
        }

        return uninstallResult;
    }

    /// <inheritdoc />
    public async Task<GameInstance> PrepareForGameUpdateAsync(
        GameInstance instance,
        IProgress<DeployProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var (wasDlcUnlocked, preservedDlcIds, sampleDlc) = await SnapshotDlcStateAsync(instance, ct).ConfigureAwait(false);

        bool hadEmulator = instance.EmulatorEnabled
                           || !string.IsNullOrWhiteSpace(instance.EmulatorId)
                           || ReFixEmulator.IsEmulatorInstalled(instance.InstallPath);

        var emulatorOptionId = instance.EmulatorId;
        var fixLayerIds = (instance.InstalledFixLayers ?? [])
            .Select(l => l.LayerId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();

        if (!wasDlcUnlocked && !hadEmulator)
        {
            _logger.LogInformation("[EmulatorLifecycle] Nothing to strip before updating {Game}", instance.Name);
            return instance;
        }

        _logger.LogInformation(
            "[EmulatorLifecycle] Rolling {Game} back to a pristine state before the game update (emulator={Emulator}, dlcUnlocker={Dlc}, layers={Layers})",
            instance.Name, emulatorOptionId ?? "none", wasDlcUnlocked, fixLayerIds.Count);

        // 1. DLC unlocker comes off FIRST: it was layered on top of the emulator DLL, so removing it
        //    in the other order would restore the unlocker's DLL over the emulator's backup chain.
        if (wasDlcUnlocked)
        {
            progress?.Report(new DeployProgress { Percentage = 10, Message = "Removing DLC unlocker before the update...", CurrentStep = "PreUpdate_DlcUninstall" });
            try
            {
                await _dlcInstaller.UninstallDlcAsync(instance, sampleDlc, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove DLC unlocker before updating {Game}", instance.Name);
            }
        }

        // 2. Then the emulator, which restores the original Steam DLLs of the CURRENT build.
        if (hadEmulator)
        {
            progress?.Report(new DeployProgress { Percentage = 35, Message = "Removing emulator and restoring original game files...", CurrentStep = "PreUpdate_EmulatorUninstall" });
            try
            {
                if (_uninstallHandler != null)
                {
                    await _uninstallHandler(instance, ct).ConfigureAwait(false);
                }
                else
                {
                    var emulator = new ReFixEmulator(LoggerFactory.Create(b => { }).CreateLogger<ReFixEmulator>());
                    await emulator.UninstallAsync(instance, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove emulator before updating {Game}", instance.Name);
            }
        }

        progress?.Report(new DeployProgress { Percentage = 50, Message = "Instance is clean — downloading updated depot files...", CurrentStep = "PreUpdate_Done" });

        // 3. Record what has to come back once the new files land. The instance is now genuinely
        //    without an emulator/unlocker, so the flags must say so — otherwise the UI keeps
        //    claiming a working emulator over a half-updated install.
        return instance with
        {
            EmulatorEnabled = false,
            EmulatorId = null,
            InstalledEmulatorVersion = null,
            DlcUnlockerInstalled = false,
            InstalledFixLayers = [],
            UnlockedDlcIds = preservedDlcIds,
            AwaitingPostUpdateRedeploy = true,
            PendingRedeployEmulatorId = hadEmulator ? (emulatorOptionId ?? "refix_valve") : null,
            PendingRedeployDlcUnlocker = wasDlcUnlocked,
            PendingRedeployFixLayerIds = fixLayerIds.AsReadOnly()
        };
    }

    /// <inheritdoc />
    public async Task<GameInstance> RestoreAfterGameUpdateAsync(
        GameInstance instance,
        IProgress<DeployProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (!instance.AwaitingPostUpdateRedeploy)
        {
            return instance;
        }

        _logger.LogInformation(
            "[EmulatorLifecycle] Redeploying post-update layers for {Game} (emulator={Emulator}, dlcUnlocker={Dlc})",
            instance.Name, instance.PendingRedeployEmulatorId ?? "none", instance.PendingRedeployDlcUnlocker);

        var result = instance;
        var emulatorOptionId = instance.PendingRedeployEmulatorId;
        bool restoreDlc = instance.PendingRedeployDlcUnlocker;
        bool allSucceeded = true;

        // 1. Emulator first so the DLC unlocker can layer on top of it, mirroring a fresh install.
        if (!string.IsNullOrWhiteSpace(emulatorOptionId))
        {
            progress?.Report(new DeployProgress { Percentage = 60, Message = "Reinstalling emulator on the updated game files...", CurrentStep = "PostUpdate_EmulatorDeploy" });
            try
            {
                bool deployed;
                if (_deployHandler != null)
                {
                    deployed = await _deployHandler(result, emulatorOptionId!, progress, ct).ConfigureAwait(false);
                }
                else
                {
                    var emulator = new ReFixEmulator(LoggerFactory.Create(b => { }).CreateLogger<ReFixEmulator>());
                    deployed = await emulator.DeployOptionAsync(result, emulatorOptionId!, progress, ct).ConfigureAwait(false);
                }

                if (deployed)
                {
                    result = result with
                    {
                        EmulatorEnabled = true,
                        EmulatorId = emulatorOptionId,
                        InstalledEmulatorVersion = ReFixEmulator.GetCurrentVersion()
                    };
                }
                else
                {
                    allSucceeded = false;
                    _logger.LogError("Failed to redeploy emulator {Option} after updating {Game}", emulatorOptionId, instance.Name);
                }
            }
            catch (Exception ex)
            {
                allSucceeded = false;
                _logger.LogError(ex, "Error redeploying emulator after updating {Game}", instance.Name);
            }
        }

        // 2. DLC unlocker on top, re-backing up the NEW steam_api DLLs.
        if (restoreDlc)
        {
            progress?.Report(new DeployProgress { Percentage = 90, Message = "Reinstalling DLC unlocker...", CurrentStep = "PostUpdate_DlcDeploy" });
            var sampleDlc = result.Dlcs.Count > 0
                ? result.Dlcs[0]
                : new DlcInfo { AppId = result.AppId, Name = "Base Game", Depots = [] };
            try
            {
                var restored = await _dlcInstaller.InstallDlcAsync(result, sampleDlc, ct).ConfigureAwait(false);
                if (restored)
                {
                    result = result with { DlcUnlockerInstalled = true };
                }
                else
                {
                    allSucceeded = false;
                    _logger.LogError("Failed to reinstall DLC unlocker after updating {Game}", instance.Name);
                }
            }
            catch (Exception ex)
            {
                allSucceeded = false;
                _logger.LogError(ex, "Error reinstalling DLC unlocker after updating {Game}", instance.Name);
            }
        }

        // Clear the bookkeeping either way: leaving it set would retry the redeploy on every future
        // download. Failures are surfaced through the logs and the returned flags.
        result = result with
        {
            AwaitingPostUpdateRedeploy = false,
            PendingRedeployEmulatorId = null,
            PendingRedeployDlcUnlocker = false,
            PendingRedeployFixLayerIds = []
        };

        if (_instanceManager != null)
        {
            try
            {
                await _instanceManager.UpdateAsync(result, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to persist instance after post-update redeploy for {Game}", instance.Name);
            }
        }

        progress?.Report(new DeployProgress
        {
            Percentage = 100,
            Message = allSucceeded ? "Emulator and DLC unlocker reinstalled." : "Update finished, but some layers could not be reinstalled.",
            CurrentStep = "PostUpdate_Done"
        });

        return result;
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
}
