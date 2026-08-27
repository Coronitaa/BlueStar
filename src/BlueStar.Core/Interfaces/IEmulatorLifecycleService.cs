using System;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Orchestrates the full lifecycle of emulator installations, updates, uninstalls, and switching
/// with guaranteed preservation and restoration of DLC unlockers (SmokeAPI/CreamAPI).
/// </summary>
public interface IEmulatorLifecycleService
{
    /// <summary>
    /// Deploys or updates an emulator option on the target instance, cleanly preserving and restoring any active DLC unlocker configuration.
    /// </summary>
    Task<bool> DeployOrUpdateEmulatorWithDlcPreservationAsync(
        GameInstance instance,
        string emulatorOptionId,
        IProgress<DeployProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Uninstalls an emulator from the target instance, cleanly preserving and restoring any active DLC unlocker configuration.
    /// </summary>
    Task<bool> UninstallEmulatorWithDlcPreservationAsync(
        GameInstance instance,
        IProgress<DeployProgress>? progress = null,
        CancellationToken ct = default);
}
