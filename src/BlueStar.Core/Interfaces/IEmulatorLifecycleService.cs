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

    /// <summary>
    /// Prepares an instance for a game update (new depot files being written over the install folder).
    /// <para>
    /// Depot downloads overwrite <c>steam_api64.dll</c> and friends with the pristine Steam binaries.
    /// Any emulator or DLC unlocker sitting on top of them is silently destroyed, while their
    /// <c>*_valve.dll</c> / <c>*_o.dll</c> backups keep pointing at the OLD game build — restoring
    /// those later corrupts the installation. This method therefore cleanly rolls the instance back
    /// to a pristine state <b>before</b> the download starts and records what has to be redeployed
    /// afterwards on the returned instance.
    /// </para>
    /// </summary>
    /// <returns>
    /// The instance with <see cref="GameInstance.AwaitingPostUpdateRedeploy"/> and the
    /// <c>PendingRedeploy*</c> fields populated. Persisting it is the caller's responsibility.
    /// </returns>
    Task<GameInstance> PrepareForGameUpdateAsync(
        GameInstance instance,
        IProgress<DeployProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Redeploys whatever <see cref="PrepareForGameUpdateAsync"/> removed, after the updated depot
    /// files have been written. Safe to call on instances with nothing pending (returns the instance
    /// unchanged).
    /// </summary>
    /// <returns>The instance with the pending-redeploy bookkeeping cleared.</returns>
    Task<GameInstance> RestoreAfterGameUpdateAsync(
        GameInstance instance,
        IProgress<DeployProgress>? progress = null,
        CancellationToken ct = default);
}
