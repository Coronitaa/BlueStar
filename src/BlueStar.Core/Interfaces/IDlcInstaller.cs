using System;
using System.Threading;
using System.Threading.Tasks;
using BlueStar.Core.Models;

namespace BlueStar.Core.Interfaces;

/// <summary>
/// Extension point for DLC unlocker installation.
/// Implementors handle deploying, configuring, and removing DLC unlocker files.
/// </summary>
public interface IDlcInstaller
{
    /// <summary>
    /// Installs the DLC unlocker for a game instance.
    /// </summary>
    /// <param name="instance">The target game instance.</param>
    /// <param name="dlc">The DLC to unlock (used for context; all instance DLCs are written).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="progress">Optional progress reporter; receives human-readable step messages.</param>
    /// <returns><c>true</c> if installation succeeded; otherwise <c>false</c>.</returns>
    Task<bool> InstallDlcAsync(
        GameInstance instance,
        DlcInfo dlc,
        CancellationToken ct,
        IProgress<string>? progress = null);

    /// <summary>
    /// Removes the DLC unlocker and restores the original Steam API DLL.
    /// </summary>
    /// <param name="instance">The target game instance.</param>
    /// <param name="dlc">The DLC context (used for logging).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="progress">Optional progress reporter; receives human-readable step messages.</param>
    /// <returns><c>true</c> if uninstallation succeeded; otherwise <c>false</c>.</returns>
    Task<bool> UninstallDlcAsync(
        GameInstance instance,
        DlcInfo dlc,
        CancellationToken ct,
        IProgress<string>? progress = null);

    /// <summary>
    /// Returns whether the DLC unlocker is currently active for a game instance.
    /// </summary>
    /// <param name="instance">The target game instance.</param>
    /// <param name="dlc">The DLC to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> if the unlocker is installed; otherwise <c>false</c>.</returns>
    Task<bool> IsDlcInstalledAsync(GameInstance instance, DlcInfo dlc, CancellationToken ct);
}
