using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Service responsible for deploying, layering, and uninstalling game-specific fixes and emulators
/// downloaded from the DepotBox API (/api/game-fixes), while ensuring full coexistence with
/// ReFix, Goldberg, DLC unlockers (SmokeAPI), and game updates.
/// </summary>
public interface IGameFixDeployService
{
    /// <summary>
    /// Deploys a game fix onto the target game instance.
    /// Handles download, backup of overwritten files, extraction, DLC state preservation, and registration in the layer stack.
    /// </summary>
    /// <param name="instance">The target game instance.</param>
    /// <param name="fix">The game fix to deploy.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if deployment succeeded; otherwise false.</returns>
    Task<bool> DeployFixAsync(
        GameInstance instance,
        GameFixInfo fix,
        IProgress<DeployProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Uninstalls a specific fix layer from the game instance, restoring original files from backup
    /// and removing any deployed artifacts without disturbing other active layers.
    /// </summary>
    /// <param name="instance">The target game instance.</param>
    /// <param name="layer">The layer to uninstall.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if uninstallation succeeded; otherwise false.</returns>
    Task<bool> UninstallFixLayerAsync(
        GameInstance instance,
        FixLayerInfo layer,
        IProgress<DeployProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Uninstalls all game fix layers from the instance and restores the original game state.
    /// </summary>
    Task<bool> UninstallAllFixLayersAsync(
        GameInstance instance,
        IProgress<DeployProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// Determines whether a fix is compatible with the current game instance configuration.
    /// </summary>
    bool IsFixCompatible(GameInstance instance, GameFixInfo fix);

    /// <summary>
    /// Gets all active fix layers installed on this instance.
    /// </summary>
    IReadOnlyList<FixLayerInfo> GetInstalledFixLayers(GameInstance instance);
}
