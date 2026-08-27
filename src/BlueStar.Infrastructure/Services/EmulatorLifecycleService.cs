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
